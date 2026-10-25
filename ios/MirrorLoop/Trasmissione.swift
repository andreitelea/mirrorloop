import Foundation
import Network
import Security
import Synchronization

struct StatisticheTrasmissione: Sendable {
    var stato = "Ferma"
    var collegato = false
    var inviati = 0
    var scartati = 0
    var byteInviati = 0
    var audioInviati = 0
    var audioScartati = 0
    var byteAudio = 0
    var connessioni = 0
    var pingRisposti = 0
    var piccoByteInAttesa = 0
    var attesaMassimaMs = 0
    var ultimoErrore: String?
    /// L'abbinamento in corso, se c'è un codice da mostrare.
    var abbinamento: AbbinamentoInCorso?
}

/// Ciò che la schermata mostra durante un abbinamento.
struct AbbinamentoInCorso: Sendable, Equatable {
    var pc: String
    var codice: String
    /// Un PC abbinato prima con lo stesso nome aveva un altro certificato.
    var improntaCambiata: Bool
    /// Falso nel ricollegamento a un PC che l'iPhone conosce già: decide solo chi sta al PC.
    var serveConferma: Bool
    var confermato = false
}

/// Manda i frame compressi e l'audio al ricevitore Windows, nel formato di docs/protocollo.md,
/// e risponde ai ping del PC (che li usa per misurare lo scarto tra i due orologi).
/// Solo QUIC, con corsie indipendenti per controllo, video e audio (TCP tolto il 25/10/2026:
/// non cifrato, e senza vantaggi misurati).
/// Prima di video e audio, l'abbinamento sulla corsia di controllo (docs/protocollo.md, "Abbinamento"):
/// il PC si riconosce dall'impronta del suo certificato, l'iPhone dalla sua chiave.
/// Tutto lo stato vive sulla coda `coda`; `invia` e `inviaAudio` possono essere chiamati da qualsiasi thread.
final class Trasmissione: @unchecked Sendable {
    static let porta: UInt16 = 7450
    /// Nome del protocollo applicativo per QUIC (ALPN): deve coincidere con quello del ricevitore.
    static let protocolloQuic = "mirrorloop/2"
    /// Quanto aspettare, durante un abbinamento, la conferma delle persone (sul PC al massimo 60 s).
    static let limiteAbbinamento: TimeInterval = 90
    // Oltre mezzo secondo di attesa la rete non tiene il passo: si smette di accodare (per il video
    // si riparte dal keyframe successivo: lezione dei buffer della Fase 1). L'audio arriva a ritmo
    // costante, quindi mezzo secondo di byte è mezzo secondo di attesa. Il video no: con lo schermo
    // quasi fermo produce molto meno del bitrate scelto, e un limite in byte (collaudo del 25/10/2026,
    // Massima a 30 Mbit/s: 1,9 MB) lasciava crescere il ritardo fino a 2 s. Per il video conta il tempo.
    static let sogliaAudioInAttesa = 48_000 * 4 / 2
    static let attesaMassimaVideo: TimeInterval = 0.5
    /// Se il collegamento non si completa entro questo tempo si considera fallito e si riprova:
    /// un'attesa infinita senza errori (com'è successo con QUIC) non deve passare inosservata.
    static let limiteCollegamento: TimeInterval = 5

    let statistiche = Mutex(StatisticheTrasmissione())

    // Dal PC arrivano solo messaggi brevi (ping, sfida, esito): uno enorme vorrebbe dire dati corrotti.
    private static let lunghezzaMassimaInIngresso = 64 * 1024

    /// Il valore è il "ruolo" scritto nell'apertura della corsia (tipo 5).
    private enum Corsia: UInt8 { case controllo = 1, video = 2, audio = 3 }

    private let destinazione: DestinazionePC
    // L'indirizzo usato dal tentativo in corso: per un PC trovato con Bonjour si ricava ogni volta,
    // così un cambio di indirizzo del PC (per esempio deciso dal router) non ferma la trasmissione.
    private var host: NWEndpoint.Host?
    private var risolutore: NWConnection?
    private let orologio: OrologioCattura
    private let chiediKeyframe: @Sendable () -> Void
    private let coda = DispatchQueue(label: "io.github.andreitelea.mirrorloop.rete")

    // Una connessione QUIC per corsia, riunite dal PC grazie al numero di trasmissione.
    private var corsie: [Corsia: NWConnection] = [:]
    private var numeroTrasmissione: UInt64 = 0
    private var corsiePronte = Set<Corsia>()
    private var generazione = 0
    private var attivo = false
    private var collegato = false
    private var attendiKeyframe = true
    private var keyframeChiesto = false
    private var byteInAttesa: [Corsia: Int] = [:]
    /// Per ogni corsia, gli istanti in cui sono stati consegnati alla rete i messaggi che aspettano
    /// ancora di partire, nell'ordine di consegna (i completamenti arrivano nello stesso ordine).
    private var istantiInAttesa: [Corsia: [TimeInterval]] = [:]
    private var ingresso: [UInt8] = []

    // Abbinamento del tentativo in corso.
    private let chiave: any ChiaveFirma
    private var impronte: [Corsia: Data] = [:]
    private var modo = Abbinamento.Modo.ricollegamento
    private var numeroIphone = Data()
    private var numeroPc = Data()
    private var abbinato = false
    private var accettatoDalPC = false
    private var confermatoDaIphone = false
    // Il PC ha risposto "in attesa": si aspettano le persone, non vale il limite di 5 s.
    private var inAbbinamento = false

    init(destinazione: DestinazionePC, chiave: any ChiaveFirma, orologio: OrologioCattura,
         chiediKeyframe: @escaping @Sendable () -> Void) {
        self.destinazione = destinazione
        self.chiave = chiave
        self.orologio = orologio
        self.chiediKeyframe = chiediKeyframe
    }

    /// Il codice sull'iPhone coincide con quello sul PC.
    func confermaCodice() {
        coda.async { [self] in
            guard var inCorso = statistiche.withLock({ $0.abbinamento }) else { return }
            confermatoDaIphone = true
            inCorso.confermato = true
            statistiche.withLock { $0.abbinamento = inCorso }
            if accettatoDalPC { completaAbbinamento() }
        }
    }

    /// Annulla l'abbinamento: niente nuovi tentativi finché non si preme di nuovo Avvia.
    func annullaAbbinamento() {
        coda.async { [self] in
            fermaDefinitivamente("Abbinamento annullato")
        }
    }

    func avvia() {
        coda.async { [self] in
            attivo = true
            collega()
        }
    }

    func ferma() {
        coda.async { [self] in
            attivo = false
            chiudi(stato: "Ferma")
        }
    }

    func invia(_ frame: FrameCompresso) {
        coda.async { [self] in
            guard collegato else { return }
            if attesa(per: .video) > Self.attesaMassimaVideo {
                attendiKeyframe = true
                keyframeChiesto = false
                statistiche.withLock { $0.scartati += 1 }
                return
            }
            if attendiKeyframe && !frame.keyframe {
                // Il keyframe si chiede solo ora che la coda si è liberata: chiesto prima, arrivava
                // con la coda ancora piena, veniva scartato e si aspettava quello automatico (fino a 2 s).
                if !keyframeChiesto {
                    chiediKeyframe()
                    keyframeChiesto = true
                }
                statistiche.withLock { $0.scartati += 1 }
                return
            }
            attendiKeyframe = false
            spedisci(Self.messaggio(frame), su: .video) { s, byte in
                s.inviati += 1
                s.byteInviati += byte
            }
        }
    }

    func inviaAudio(_ audio: AudioPcm16) {
        coda.async { [self] in
            guard collegato else { return }
            // Con la rete in affanno anche l'audio si scarta: meglio un buco breve che un
            // ritardo che cresce.
            if inAttesa(per: .audio) > Self.sogliaAudioInAttesa {
                statistiche.withLock { $0.audioScartati += 1 }
                return
            }
            spedisci(Self.messaggioAudio(audio), su: .audio) { s, byte in
                s.audioInviati += 1
                s.byteAudio += byte
            }
        }
    }

    /// Lunghezza (4 byte, big-endian) + tipo 2 + codifica 1 (PCM 16 bit) + canali + frequenza
    /// (4 byte, big-endian) + istante di cattura (8 byte, big-endian) + campioni little-endian.
    static func messaggioAudio(_ audio: AudioPcm16) -> Data {
        var dati = Data(capacity: 19 + audio.dati.count)
        let lunghezza = UInt32(15 + audio.dati.count)
        withUnsafeBytes(of: lunghezza.bigEndian) { dati.append(contentsOf: $0) }
        dati.append(2)
        dati.append(1)
        dati.append(UInt8(audio.canali))
        withUnsafeBytes(of: UInt32(audio.frequenza).bigEndian) { dati.append(contentsOf: $0) }
        withUnsafeBytes(of: audio.istanteCatturaMicrosecondi.bigEndian) { dati.append(contentsOf: $0) }
        dati.append(audio.dati)
        return dati
    }

    /// Lunghezza (4 byte, big-endian) + tipo 1 + flag + istante di cattura (8 byte, big-endian) + dati.
    /// Flag: bit 0 = keyframe, bit 1 e 2 = rotazione in passi da 90° in senso orario.
    static func messaggio(_ frame: FrameCompresso) -> Data {
        var dati = Data(capacity: 14 + frame.datiAnnexB.count)
        let lunghezza = UInt32(1 + 1 + 8 + frame.datiAnnexB.count)
        withUnsafeBytes(of: lunghezza.bigEndian) { dati.append(contentsOf: $0) }
        dati.append(1)
        dati.append((frame.keyframe ? 1 : 0) | (frame.rotazione & 0b11) << 1)
        withUnsafeBytes(of: frame.istanteCatturaMicrosecondi.bigEndian) { dati.append(contentsOf: $0) }
        dati.append(frame.datiAnnexB)
        return dati
    }

    /// Tipo 5 · apertura corsia: lunghezza 10 + tipo 5 + ruolo + numero di trasmissione (8 byte, big-endian).
    /// Il PC riunisce le corsie con lo stesso numero; inoltre in QUIC una corsia esiste per il PC solo
    /// dopo il primo dato, e quella di controllo deve esistere subito perché è lì che arrivano i ping.
    private static func aperturaCorsia(_ corsia: Corsia, numero: UInt64) -> Data {
        var dati = Data([0, 0, 0, 10, 5, corsia.rawValue])
        withUnsafeBytes(of: numero.bigEndian) { dati.append(contentsOf: $0) }
        return dati
    }

    // MARK: - Spedizione

    /// Quanti byte aspettano di partire sulla fila di questa corsia: ogni corsia ha la sua, ed è
    /// proprio il motivo per usare QUIC.
    private func inAttesa(per corsia: Corsia) -> Int {
        byteInAttesa[corsia, default: 0]
    }

    /// Da quanti secondi aspetta di partire il messaggio più vecchio della fila di questa corsia;
    /// 0 se la fila è vuota.
    private func attesa(per corsia: Corsia) -> TimeInterval {
        guard let piuVecchio = istantiInAttesa[corsia]?.first else { return 0 }
        return ProcessInfo.processInfo.systemUptime - piuVecchio
    }

    private func spedisci(_ messaggio: Data, su corsia: Corsia, conteggio: @escaping @Sendable (inout StatisticheTrasmissione, Int) -> Void) {
        guard let connessione = corsie[corsia] else { return }
        byteInAttesa[corsia, default: 0] += messaggio.count
        let consegna = ProcessInfo.processInfo.systemUptime
        istantiInAttesa[corsia, default: []].append(consegna)
        let picco = inAttesa(per: corsia)
        statistiche.withLock { $0.piccoByteInAttesa = max($0.piccoByteInAttesa, picco) }
        let mia = generazione
        connessione.send(content: messaggio, completion: .contentProcessed { [self] errore in
            // Il completamento arriva sulla coda della connessione, cioè `coda`.
            guard mia == generazione else { return }
            byteInAttesa[corsia, default: 0] -= messaggio.count
            if istantiInAttesa[corsia]?.isEmpty == false { istantiInAttesa[corsia]?.removeFirst() }
            let attesaMs = Int((ProcessInfo.processInfo.systemUptime - consegna) * 1000)
            statistiche.withLock { $0.attesaMassimaMs = max($0.attesaMassimaMs, attesaMs) }
            if errore == nil {
                statistiche.withLock { conteggio(&$0, messaggio.count) }
            }
        })
    }

    // MARK: - Collegamento

    private func collega() {
        guard attivo else { return }
        generazione += 1
        let mia = generazione
        statistiche.withLock { $0.stato = "Collegamento a \(destinazione.descrizione)…" }
        switch destinazione {
        case .indirizzo(let indirizzo):
            host = NWEndpoint.Host(indirizzo)
            collegaQuic(generazione: mia)
        case .servizio(let nome):
            risolvi(nome, generazione: mia)
        }
        coda.asyncAfter(deadline: .now() + Self.limiteCollegamento) { [self] in
            // Durante un abbinamento si aspettano le persone: vale il limite dell'abbinamento.
            guard mia == generazione, !collegato, !inAbbinamento else { return }
            riprova("collegamento non riuscito entro \(Int(Self.limiteCollegamento)) s")
        }
    }

    /// Ricava l'indirizzo IPv4 del PC trovato con Bonjour. Una "connessione" UDP diventa pronta appena
    /// il nome è risolto, senza spedire niente: il PC non vede nulla. Solo IPv4 perché il ricevitore
    /// ascolta su IPv4. Poi ci si collega come con un indirizzo scritto a mano.
    private func risolvi(_ nome: String, generazione mia: Int) {
        let parametri = NWParameters.udp
        if let ip = parametri.defaultProtocolStack.internetProtocol as? NWProtocolIP.Options {
            ip.version = .v4
        }
        let nuovo = NWConnection(to: .service(name: nome, type: CercaPC.tipoServizio, domain: "local.", interface: nil),
                                 using: parametri)
        nuovo.stateUpdateHandler = { [self] stato in
            guard mia == generazione else { return }
            switch stato {
            case .ready:
                let trovato: NWEndpoint.Host? = if case .hostPort(let risolto, _) = nuovo.currentPath?.remoteEndpoint { risolto } else { nil }
                nuovo.cancel()
                risolutore = nil
                guard let trovato else {
                    riprova("indirizzo di \(nome) non ricavato")
                    return
                }
                host = trovato
                statistiche.withLock { $0.stato = "Collegamento a \(nome) (\(trovato))…" }
                collegaQuic(generazione: mia)
            case .waiting(let errore), .failed(let errore):
                riprova("\(nome) non trovato in rete: \(errore.localizedDescription)")
            default:
                break
            }
        }
        risolutore = nuovo
        nuovo.start(queue: coda)
    }

    /// Una connessione QUIC semplice per ogni corsia, come negli esempi QUIC collaudati su iPhone.
    /// (Con `NWConnectionGroup` il gruppo non diventava mai pronto, senza nessun errore.)
    /// Ogni connessione ha il suo recupero delle perdite: l'audio non aspetta mai il video.
    private func collegaQuic(generazione mia: Int) {
        guard let host else { return }
        // Nuovo a ogni tentativo: il PC riparte sempre da una trasmissione pulita.
        numeroTrasmissione = UInt64.random(in: .min ... .max)
        let numero = numeroTrasmissione
        for corsia in [Corsia.controllo, .video, .audio] {
            let connessione = NWConnection(host: host, port: NWEndpoint.Port(rawValue: Self.porta)!,
                                           using: parametriQuic(corsia: corsia, generazione: mia))
            connessione.stateUpdateHandler = { [self] stato in
                guard mia == generazione else { return }
                switch stato {
                case .ready:
                    // Prima di ogni altro dato: l'ordine su una corsia QUIC è garantito. Le corsie
                    // video e audio il PC le tiene in sospeso finché l'abbinamento non è concluso.
                    connessione.send(content: Self.aperturaCorsia(corsia, numero: numero), completion: .contentProcessed { _ in })
                    if corsia == .controllo {
                        ricevi(connessione, generazione: mia)
                        presentati(connessione)
                    }
                    corsiePronte.insert(corsia)
                    forseCollegato()
                case .waiting(let errore), .failed(let errore):
                    riprova("corsia \(corsia): \(errore.localizedDescription)")
                default:
                    break
                }
            }
            corsie[corsia] = connessione
            connessione.start(queue: coda)
        }
    }

    private func parametriQuic(corsia: Corsia, generazione mia: Int) -> NWParameters {
        let quic = NWProtocolQUIC.Options(alpn: [Self.protocolloQuic])
        quic.idleTimeout = 5_000
        // Bidirezionale anche per video e audio: è la forma usata negli esempi che funzionano, e il
        // PC la accetta. Solo sulla corsia di controllo il PC scrive (ping, sfida, esito).
        quic.direction = .bidirectional
        // Il certificato del ricevitore è autofirmato, quindi il sistema non può garantirlo: si accetta,
        // ma se ne annota l'impronta. È da quella che l'iPhone riconosce il PC, ed è quella che firma
        // nella prova: un impostore in mezzo avrebbe un'impronta diversa (docs/protocollo.md).
        sec_protocol_options_set_verify_block(quic.securityProtocolOptions, { [self] _, fiducia, completamento in
            let catena = SecTrustCopyCertificateChain(sec_trust_copy_ref(fiducia).takeRetainedValue()) as? [SecCertificate]
            guard let certificato = catena?.first else {
                completamento(false)
                return
            }
            if mia == generazione {
                impronte[corsia] = Abbinamento.impronta(certificatoDer: SecCertificateCopyData(certificato) as Data)
            }
            completamento(true)
        }, coda)
        let parametri = NWParameters(quic: quic)
        parametri.serviceClass = .interactiveVideo
        return parametri
    }

    // MARK: - Abbinamento

    /// Presentazione (tipo 6): modo, chiave e impegno sul numero dell'iPhone.
    private func presentati(_ connessione: NWConnection) {
        guard let impronta = impronte[.controllo] else {
            riprova("certificato del PC non letto")
            return
        }
        modo = PCAbbinati.conosciuto(impronta) ? .ricollegamento : .nuovoAbbinamento
        numeroIphone = Abbinamento.nuovoNumero()
        let pubblica = chiave.pubblica
        let presentazione = Abbinamento.messaggioPresentazione(
            modo: modo, chiave: pubblica, impegno: Abbinamento.impegno(chiave: pubblica, numeroIphone: numeroIphone),
            // Da iOS 16 un'app riceve solo il nome generico del dispositivo.
            nome: "iPhone")
        connessione.send(content: presentazione, completion: .contentProcessed { _ in })
        statistiche.withLock { $0.stato = "Abbinamento con \(destinazione.descrizione)…" }
    }

    /// Sfida (tipo 7) dal PC: si risponde con la prova (tipo 8).
    private func rispondiSfida(_ numeroPc: Data, _ connessione: NWConnection) {
        guard let impronta = impronte[.controllo], !numeroIphone.isEmpty else { return }
        self.numeroPc = numeroPc
        do {
            let firma = try chiave.firma(Abbinamento.datiDaFirmare(
                impronta: impronta, chiave: chiave.pubblica, numeroPc: numeroPc, numeroIphone: numeroIphone))
            connessione.send(content: Abbinamento.messaggioProva(numeroIphone: numeroIphone, firma: firma),
                             completion: .contentProcessed { _ in })
        } catch {
            fermaDefinitivamente("Firma non riuscita: \(error.localizedDescription)")
        }
    }

    /// Esito (tipo 9) dal PC.
    private func gestisciEsito(_ valore: UInt8) {
        switch Abbinamento.Esito(rawValue: valore) {
        case .accettato:
            accettatoDalPC = true
            if modo == .ricollegamento || confermatoDaIphone {
                completaAbbinamento()
            } else {
                statistiche.withLock { $0.stato = "Il PC ha accettato: conferma il codice sull'iPhone" }
            }
        case .inAttesa:
            guard let impronta = impronte[.controllo] else { return }
            let nuovo = modo == .nuovoAbbinamento
            let inCorso = AbbinamentoInCorso(
                pc: destinazione.descrizione,
                codice: Abbinamento.codice(impronta: impronta, chiave: chiave.pubblica, numeroPc: numeroPc, numeroIphone: numeroIphone),
                improntaCambiata: nuovo && PCAbbinati.improntaCambiata(nome: destinazione.descrizione, impronta: impronta),
                // Se l'iPhone conosce già il PC (impronta verificata) decide solo chi sta al PC.
                serveConferma: nuovo)
            statistiche.withLock {
                $0.abbinamento = inCorso
                $0.stato = nuovo ? "Confronta il codice con quello sul PC" : "Consenti il collegamento sul PC"
            }
            inAbbinamento = true
            let mia = generazione
            coda.asyncAfter(deadline: .now() + Self.limiteAbbinamento) { [self] in
                guard mia == generazione, !collegato else { return }
                fermaDefinitivamente("Abbinamento non concluso in tempo")
            }
        case .rifiutato, nil:
            fermaDefinitivamente("Il PC ha rifiutato il collegamento")
        }
    }

    /// Accettato dal PC e, se serviva, confermato sull'iPhone: il PC si memorizza e si parte.
    private func completaAbbinamento() {
        guard !abbinato, let impronta = impronte[.controllo] else { return }
        abbinato = true
        inAbbinamento = false
        if modo == .nuovoAbbinamento {
            PCAbbinati.aggiungi(impronta, nome: destinazione.descrizione)
        }
        statistiche.withLock { $0.abbinamento = nil }
        forseCollegato()
    }

    /// Video e audio partono solo con le tre corsie pronte, l'abbinamento concluso e lo stesso
    /// certificato su tutte e tre le connessioni.
    private func forseCollegato() {
        guard abbinato, corsiePronte.count == 3, !collegato else { return }
        guard impronte.count == 3, Set(impronte.values).count == 1 else {
            riprova("le corsie portano a PC diversi")
            return
        }
        collegamentoRiuscito()
    }

    /// Rifiuto del PC, annullamento o errore dell'abbinamento: niente nuovi tentativi automatici,
    /// che riempirebbero il PC di richieste. Si riparte premendo Avvia.
    private func fermaDefinitivamente(_ motivo: String) {
        attivo = false
        chiudi(stato: motivo)
    }

    private func collegamentoRiuscito() {
        collegato = true
        attendiKeyframe = true
        byteInAttesa.removeAll()
        istantiInAttesa.removeAll()
        // Un keyframe subito, così il ricevitore mostra il video senza aspettare 2 secondi.
        chiediKeyframe()
        keyframeChiesto = true
        statistiche.withLock { s in
            let indirizzo = host.map { "\($0):\(Self.porta)" } ?? "?"
            s.stato = switch destinazione {
            case .servizio(let nome): "Collegata a \(nome) (\(indirizzo))"
            case .indirizzo: "Collegata a \(indirizzo)"
            }
            s.collegato = true
            s.connessioni += 1
            s.ultimoErrore = nil
        }
    }

    /// Ricevitore spento o irraggiungibile, o connessione persa: nuovo tentativo tra un secondo.
    private func riprova(_ errore: String) {
        chiudi(stato: "In attesa del ricevitore…", errore: errore)
        coda.asyncAfter(deadline: .now() + 1) { [self] in collega() }
    }

    private func chiudi(stato: String, errore: String? = nil) {
        generazione += 1
        risolutore?.cancel()
        risolutore = nil
        for connessione in corsie.values { connessione.cancel() }
        corsie.removeAll()
        corsiePronte.removeAll()
        collegato = false
        byteInAttesa.removeAll()
        istantiInAttesa.removeAll()
        ingresso.removeAll()
        impronte.removeAll()
        numeroIphone = Data()
        numeroPc = Data()
        abbinato = false
        accettatoDalPC = false
        confermatoDaIphone = false
        inAbbinamento = false
        statistiche.withLock { s in
            s.stato = stato
            s.collegato = false
            s.abbinamento = nil
            if let errore { s.ultimoErrore = errore }
        }
    }

    // MARK: - Messaggi dal PC

    /// Legge quello che manda il PC, un pezzo alla volta, finché dura la connessione.
    private func ricevi(_ connessione: NWConnection, generazione mia: Int) {
        connessione.receive(minimumIncompleteLength: 1, maximumLength: 64 * 1024) { [self] dati, _, finito, errore in
            guard mia == generazione else { return }
            if let dati {
                ingresso.append(contentsOf: dati)
                elaboraIngresso(connessione)
            }
            // Gli errori di connessione li gestisce già stateUpdateHandler.
            if errore == nil && !finito {
                ricevi(connessione, generazione: mia)
            }
        }
    }

    /// Estrae i messaggi completi (lunghezza su 4 byte + contenuto): ping, sfida ed esito.
    private func elaboraIngresso(_ connessione: NWConnection) {
        while ingresso.count >= 4 {
            let lunghezza = ingresso[0..<4].reduce(0) { $0 << 8 | Int($1) }
            guard lunghezza >= 1, lunghezza <= Self.lunghezzaMassimaInIngresso else {
                ingresso.removeAll()
                return
            }
            guard ingresso.count >= 4 + lunghezza else { return }
            let messaggio = Array(ingresso[4..<(4 + lunghezza)])
            ingresso.removeFirst(4 + lunghezza)
            // Tipo 3 = ping: numero (4 byte) + T1 (8 byte); 7 = sfida: numero del PC (32 byte);
            // 9 = esito (1 byte). Gli altri tipi si ignorano.
            switch (messaggio[0], messaggio.count) {
            case (3, 13): rispondiPing(messaggio, connessione)
            case (7, 33): rispondiSfida(Data(messaggio[1...]), connessione)
            case (9, 2): gestisciEsito(messaggio[1])
            default: break
            }
        }
    }

    /// Pong (tipo 4): numero e T1 copiati dal ping, T2 = arrivo del ping, T3 = partenza della risposta,
    /// entrambi con l'orologio degli istanti di cattura.
    private func rispondiPing(_ ping: [UInt8], _ connessione: NWConnection) {
        let t2 = orologio.adessoMicrosecondi()
        var pong = Data(capacity: 33)
        withUnsafeBytes(of: UInt32(29).bigEndian) { pong.append(contentsOf: $0) }
        pong.append(4)
        pong.append(contentsOf: ping[1..<13])
        withUnsafeBytes(of: t2.bigEndian) { pong.append(contentsOf: $0) }
        let t3 = orologio.adessoMicrosecondi()
        withUnsafeBytes(of: t3.bigEndian) { pong.append(contentsOf: $0) }
        connessione.send(content: pong, completion: .contentProcessed { _ in })
        statistiche.withLock { $0.pingRisposti += 1 }
    }
}
