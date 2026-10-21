import Foundation
import Network
import Security
import Synchronization

/// Come viaggiano i dati verso il ricevitore.
enum Trasporto: String, CaseIterable, Sendable {
    /// Una sola connessione TCP per tutto: semplice, ma una perdita blocca tutta la fila.
    case tcp = "TCP"
    /// QUIC (su UDP) con corsie indipendenti per controllo, video e audio.
    case quic = "QUIC"
}

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
    var ultimoErrore: String?
}

/// Manda i frame compressi e l'audio al ricevitore Windows, nel formato di docs/protocollo.md,
/// e risponde ai ping del PC (che li usa per misurare lo scarto tra i due orologi).
/// Tutto lo stato vive sulla coda `coda`; `invia` e `inviaAudio` possono essere chiamati da qualsiasi thread.
final class Trasmissione: @unchecked Sendable {
    static let porta: UInt16 = 7450
    /// Nome del protocollo applicativo per QUIC (ALPN): deve coincidere con quello del ricevitore.
    static let protocolloQuic = "mirrorloop/1"
    // Oltre mezzo secondo di dati in attesa di partire la rete non tiene il passo: si smette di
    // accodare (per il video si riparte dal keyframe successivo: lezione dei buffer della Fase 1).
    static let sogliaVideoInAttesa = CompressoreH264.bitrate / 8 / 2
    static let sogliaAudioInAttesa = 48_000 * 4 / 2
    /// Se il collegamento non si completa entro questo tempo si considera fallito e si riprova:
    /// un'attesa infinita senza errori (com'è successo con QUIC) non deve passare inosservata.
    static let limiteCollegamento: TimeInterval = 5

    let statistiche = Mutex(StatisticheTrasmissione())

    // Dal PC arrivano solo ping da 13 byte: un messaggio enorme vorrebbe dire dati corrotti.
    private static let lunghezzaMassimaInIngresso = 64 * 1024

    /// Il valore è il "ruolo" scritto nell'apertura della corsia (tipo 5).
    private enum Corsia: UInt8 { case controllo = 1, video = 2, audio = 3 }

    private let host: NWEndpoint.Host
    private let trasporto: Trasporto
    private let orologio: OrologioCattura
    private let chiediKeyframe: @Sendable () -> Void
    private let coda = DispatchQueue(label: "io.github.andreitelea.mirrorloop.rete")

    // TCP: una connessione. QUIC: una connessione per corsia, riunite dal PC grazie al numero di trasmissione.
    private var corsie: [Corsia: NWConnection] = [:]
    private var numeroTrasmissione: UInt64 = 0
    private var corsiePronte = Set<Corsia>()
    private var generazione = 0
    private var attivo = false
    private var collegato = false
    private var attendiKeyframe = true
    private var byteInAttesa: [Corsia: Int] = [:]
    private var ingresso: [UInt8] = []

    init(indirizzo: String, trasporto: Trasporto, orologio: OrologioCattura, chiediKeyframe: @escaping @Sendable () -> Void) {
        self.host = NWEndpoint.Host(indirizzo)
        self.trasporto = trasporto
        self.orologio = orologio
        self.chiediKeyframe = chiediKeyframe
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
            if inAttesa(per: .video) > Self.sogliaVideoInAttesa {
                if !attendiKeyframe { chiediKeyframe() }
                attendiKeyframe = true
                statistiche.withLock { $0.scartati += 1 }
                return
            }
            if attendiKeyframe && !frame.keyframe {
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

    /// Quanti byte aspettano di partire sulla fila di questa corsia. In TCP c'è una sola fila per
    /// tutto; in QUIC ogni corsia ha la sua, ed è proprio il motivo per usarlo.
    private func inAttesa(per corsia: Corsia) -> Int {
        trasporto == .tcp ? byteInAttesa.values.reduce(0, +) : byteInAttesa[corsia, default: 0]
    }

    private func spedisci(_ messaggio: Data, su corsia: Corsia, conteggio: @escaping @Sendable (inout StatisticheTrasmissione, Int) -> Void) {
        guard let connessione = connessione(per: corsia) else { return }
        byteInAttesa[corsia, default: 0] += messaggio.count
        let picco = inAttesa(per: corsia)
        statistiche.withLock { $0.piccoByteInAttesa = max($0.piccoByteInAttesa, picco) }
        let mia = generazione
        connessione.send(content: messaggio, completion: .contentProcessed { [self] errore in
            // Il completamento arriva sulla coda della connessione, cioè `coda`.
            guard mia == generazione else { return }
            byteInAttesa[corsia, default: 0] -= messaggio.count
            if errore == nil {
                statistiche.withLock { conteggio(&$0, messaggio.count) }
            }
        })
    }

    private func connessione(per corsia: Corsia) -> NWConnection? {
        trasporto == .tcp ? corsie[.controllo] : corsie[corsia]
    }

    // MARK: - Collegamento

    private func collega() {
        guard attivo else { return }
        generazione += 1
        let mia = generazione
        statistiche.withLock { $0.stato = "Collegamento a \(host) (\(trasporto.rawValue))…" }
        switch trasporto {
        case .tcp: collegaTcp(generazione: mia)
        case .quic: collegaQuic(generazione: mia)
        }
        coda.asyncAfter(deadline: .now() + Self.limiteCollegamento) { [self] in
            guard mia == generazione, !collegato else { return }
            riprova("collegamento \(trasporto.rawValue) non riuscito entro \(Int(Self.limiteCollegamento)) s")
        }
    }

    private func collegaTcp(generazione mia: Int) {
        let tcp = NWProtocolTCP.Options()
        tcp.noDelay = true
        let parametri = NWParameters(tls: nil, tcp: tcp)
        parametri.serviceClass = .interactiveVideo
        let nuova = NWConnection(host: host, port: NWEndpoint.Port(rawValue: Self.porta)!, using: parametri)
        nuova.stateUpdateHandler = { [self] stato in
            guard mia == generazione else { return }
            switch stato {
            case .ready:
                ricevi(nuova, generazione: mia)
                collegamentoRiuscito()
            case .waiting(let errore), .failed(let errore):
                riprova(errore.localizedDescription)
            default:
                break
            }
        }
        corsie[.controllo] = nuova
        nuova.start(queue: coda)
    }

    /// Una connessione QUIC semplice per ogni corsia, come negli esempi QUIC collaudati su iPhone.
    /// (Con `NWConnectionGroup` il gruppo non diventava mai pronto, senza nessun errore.)
    /// Ogni connessione ha il suo recupero delle perdite: l'audio non aspetta mai il video.
    private func collegaQuic(generazione mia: Int) {
        // Nuovo a ogni tentativo: il PC riparte sempre da una trasmissione pulita.
        numeroTrasmissione = UInt64.random(in: .min ... .max)
        let numero = numeroTrasmissione
        for corsia in [Corsia.controllo, .video, .audio] {
            let connessione = NWConnection(host: host, port: NWEndpoint.Port(rawValue: Self.porta)!, using: Self.parametriQuic(coda: coda))
            connessione.stateUpdateHandler = { [self] stato in
                guard mia == generazione else { return }
                switch stato {
                case .ready:
                    // Prima di ogni altro dato: l'ordine su una corsia QUIC è garantito.
                    connessione.send(content: Self.aperturaCorsia(corsia, numero: numero), completion: .contentProcessed { _ in })
                    if corsia == .controllo { ricevi(connessione, generazione: mia) }
                    corsiePronte.insert(corsia)
                    if corsiePronte.count == 3 { collegamentoRiuscito() }
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

    private static func parametriQuic(coda: DispatchQueue) -> NWParameters {
        let quic = NWProtocolQUIC.Options(alpn: [protocolloQuic])
        quic.idleTimeout = 5_000
        // Bidirezionale anche per video e audio: è la forma usata negli esempi che funzionano, e il
        // PC la accetta. Solo sulla corsia di controllo il PC scrive (i ping).
        quic.direction = .bidirectional
        // Il certificato del ricevitore è autofirmato: sulla rete di casa per ora si accetta.
        // Prima della pubblicazione si memorizzerà la sua impronta al primo collegamento (abbinamento).
        sec_protocol_options_set_verify_block(quic.securityProtocolOptions, { _, _, completamento in
            completamento(true)
        }, coda)
        let parametri = NWParameters(quic: quic)
        parametri.serviceClass = .interactiveVideo
        return parametri
    }

    private func collegamentoRiuscito() {
        collegato = true
        attendiKeyframe = true
        byteInAttesa.removeAll()
        ingresso.removeAll()
        // Un keyframe subito, così il ricevitore mostra il video senza aspettare 2 secondi.
        chiediKeyframe()
        statistiche.withLock { s in
            s.stato = "Collegata a \(host):\(Self.porta) (\(trasporto.rawValue))"
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
        for connessione in corsie.values { connessione.cancel() }
        corsie.removeAll()
        corsiePronte.removeAll()
        collegato = false
        byteInAttesa.removeAll()
        statistiche.withLock { s in
            s.stato = stato
            s.collegato = false
            if let errore { s.ultimoErrore = errore }
        }
    }

    // MARK: - Ping dal PC

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

    /// Estrae i messaggi completi (lunghezza su 4 byte + contenuto) e risponde ai ping.
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
            // Tipo 3 = ping: numero (4 byte) + T1 (8 byte). Gli altri tipi si ignorano.
            if messaggio[0] == 3 && messaggio.count == 13 {
                rispondiPing(messaggio, connessione)
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
