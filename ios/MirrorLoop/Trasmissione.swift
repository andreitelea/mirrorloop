import Foundation
import Network
import Synchronization

struct StatisticheTrasmissione: Sendable {
    var stato = "Ferma"
    var collegato = false
    var inviati = 0
    var scartati = 0
    var byteInviati = 0
    var connessioni = 0
    var ultimoErrore: String?
}

/// Manda i frame compressi al ricevitore Windows via TCP, nel formato di docs/protocollo.md.
/// Tutto lo stato vive sulla coda `coda`; `invia` può essere chiamato da qualsiasi thread.
final class Trasmissione: @unchecked Sendable {
    static let porta: UInt16 = 7450
    // Oltre mezzo secondo di video in attesa di partire, la rete non tiene il passo: si smette
    // di accodare e si riparte dal keyframe successivo (lezione dei buffer della Fase 1).
    static let sogliaByteInAttesa = CompressoreH264.bitrate / 8 / 2

    let statistiche = Mutex(StatisticheTrasmissione())

    private let host: NWEndpoint.Host
    private let chiediKeyframe: @Sendable () -> Void
    private let coda = DispatchQueue(label: "io.github.andreitelea.mirrorloop.rete")
    private var connessione: NWConnection?
    private var generazione = 0
    private var attivo = false
    private var collegato = false
    private var attendiKeyframe = true
    private var byteInAttesa = 0

    init(indirizzo: String, chiediKeyframe: @escaping @Sendable () -> Void) {
        self.host = NWEndpoint.Host(indirizzo)
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
        let nuova = NWConnection(host: host, port: NWEndpoint.Port(rawValue: Self.porta)!, using: NWParameters(tls: nil, tcp: tcp))
        nuova.stateUpdateHandler = { [self] stato in
            guard mia == generazione else { return }
            switch stato {
            case .ready:
                collegato = true
                attendiKeyframe = true
                byteInAttesa = 0
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
