import Foundation
import Network
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
    var ultimoErrore: String?
}

/// Manda i frame compressi al ricevitore Windows via TCP, nel formato di docs/protocollo.md,
/// e risponde ai ping del PC (che li usa per misurare lo scarto tra i due orologi).
/// Tutto lo stato vive sulla coda `coda`; `invia` può essere chiamato da qualsiasi thread.
final class Trasmissione: @unchecked Sendable {
    static let porta: UInt16 = 7450
    // Oltre mezzo secondo di video in attesa di partire, la rete non tiene il passo: si smette
    // di accodare e si riparte dal keyframe successivo (lezione dei buffer della Fase 1).
    static let sogliaByteInAttesa = CompressoreH264.bitrate / 8 / 2

    let statistiche = Mutex(StatisticheTrasmissione())

    // Dal PC arrivano solo ping da 13 byte: un messaggio enorme vorrebbe dire dati corrotti.
    private static let lunghezzaMassimaInIngresso = 64 * 1024

    private let host: NWEndpoint.Host
    private let orologio: OrologioCattura
    private let chiediKeyframe: @Sendable () -> Void
    private let coda = DispatchQueue(label: "io.github.andreitelea.mirrorloop.rete")
    private var connessione: NWConnection?
    private var generazione = 0
    private var attivo = false
    private var collegato = false
    private var attendiKeyframe = true
    private var byteInAttesa = 0
    private var ingresso: [UInt8] = []

    init(indirizzo: String, orologio: OrologioCattura, chiediKeyframe: @escaping @Sendable () -> Void) {
        self.host = NWEndpoint.Host(indirizzo)
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
            guard collegato, let connessione else { return }
            if byteInAttesa > Self.sogliaByteInAttesa {
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

            let messaggio = Self.messaggio(frame)
            byteInAttesa += messaggio.count
            let picco = byteInAttesa
            statistiche.withLock { $0.piccoByteInAttesa = max($0.piccoByteInAttesa, picco) }
            let mia = generazione
            connessione.send(content: messaggio, completion: .contentProcessed { [self] errore in
                // Il completamento arriva sulla coda della connessione, cioè `coda`.
                guard mia == generazione else { return }
                byteInAttesa -= messaggio.count
                if errore == nil {
                    statistiche.withLock { s in
                        s.inviati += 1
                        s.byteInviati += messaggio.count
                    }
                }
            })
        }
    }

    func inviaAudio(_ audio: AudioPcm16) {
        coda.async { [self] in
            guard collegato, let connessione else { return }
            // Con la rete in affanno anche l'audio si scarta: meglio un buco breve che un
            // ritardo che cresce. Il video intanto riparte dal keyframe successivo.
            if byteInAttesa > Self.sogliaByteInAttesa {
                statistiche.withLock { $0.audioScartati += 1 }
                return
            }
            let messaggio = Self.messaggioAudio(audio)
            byteInAttesa += messaggio.count
            let picco = byteInAttesa
            statistiche.withLock { $0.piccoByteInAttesa = max($0.piccoByteInAttesa, picco) }
            let mia = generazione
            connessione.send(content: messaggio, completion: .contentProcessed { [self] errore in
                guard mia == generazione else { return }
                byteInAttesa -= messaggio.count
                if errore == nil {
                    statistiche.withLock { s in
                        s.audioInviati += 1
                        s.byteAudio += messaggio.count
                    }
                }
            })
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
    static func messaggio(_ frame: FrameCompresso) -> Data {
        var dati = Data(capacity: 14 + frame.datiAnnexB.count)
        let lunghezza = UInt32(1 + 1 + 8 + frame.datiAnnexB.count)
        withUnsafeBytes(of: lunghezza.bigEndian) { dati.append(contentsOf: $0) }
        dati.append(1)
        dati.append(frame.keyframe ? 1 : 0)
        withUnsafeBytes(of: frame.istanteCatturaMicrosecondi.bigEndian) { dati.append(contentsOf: $0) }
        dati.append(frame.datiAnnexB)
        return dati
    }

    private func collega() {
        guard attivo else { return }
        generazione += 1
        let mia = generazione
        let tcp = NWProtocolTCP.Options()
        tcp.noDelay = true
        let parametri = NWParameters(tls: nil, tcp: tcp)
        // Dice al sistema e al Wi-Fi che è un flusso interattivo a ritmo costante: dovrebbe
        // ridurre le spedizioni "a gruppi" del risparmio energetico, che creano pause nell'audio.
        parametri.serviceClass = .interactiveVideo
        let nuova = NWConnection(host: host, port: NWEndpoint.Port(rawValue: Self.porta)!, using: parametri)
        nuova.stateUpdateHandler = { [self] stato in
            guard mia == generazione else { return }
            switch stato {
            case .ready:
                collegato = true
                attendiKeyframe = true
                byteInAttesa = 0
                ingresso.removeAll()
                ricevi(nuova, generazione: mia)
                // Un keyframe subito, così il ricevitore mostra il video senza aspettare 2 secondi.
                chiediKeyframe()
                statistiche.withLock { s in
                    s.stato = "Collegata a \(host):\(Self.porta)"
                    s.collegato = true
                    s.connessioni += 1
                    s.ultimoErrore = nil
                }
            case .waiting(let errore), .failed(let errore):
                // Ricevitore spento o irraggiungibile: nuovo tentativo tra un secondo.
                chiudi(stato: "In attesa del ricevitore…", errore: errore.localizedDescription)
                coda.asyncAfter(deadline: .now() + 1) { [self] in collega() }
            default:
                break
            }
        }
        connessione = nuova
        statistiche.withLock { $0.stato = "Collegamento a \(host)…" }
        nuova.start(queue: coda)
    }

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

    private func chiudi(stato: String, errore: String? = nil) {
        generazione += 1
        connessione?.cancel()
        connessione = nil
        collegato = false
        byteInAttesa = 0
        statistiche.withLock { s in
            s.stato = stato
            s.collegato = false
            if let errore { s.ultimoErrore = errore }
        }
    }
}
