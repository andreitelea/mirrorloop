import CoreMedia
import CoreVideo
import Foundation
import Observation
import ScreenCaptureKit
import Synchronization
import UIKit

// Cattura dello schermo intero con ScreenCaptureKit (iOS 27). Continua anche con l'app in
// background grazie alla modalità `screen-capture` dichiarata in project.yml.

struct Contatori: Sendable {
    var fotogrammi = 0
    var fotogrammiInBackground = 0
    var scartatiPerRitmo = 0
    var pacchettiAudio = 0
    var larghezza = 0
    var altezza = 0
    var pausaMassima: TimeInterval = 0
    var ultimoFotogramma: TimeInterval?
    var ultimoAccettato: Double?
    var inBackground = false
    var inizioBackground: TimeInterval?
    var secondiInBackground: TimeInterval = 0
}

/// Contenitore per far passare tra i thread oggetti di sistema non marcati Sendable,
/// usati poi solo sul thread principale.
struct Scatola<Valore>: @unchecked Sendable {
    let valore: Valore
}

enum EventoCattura: Sendable {
    case filtroScelto(Scatola<SCContentFilter>)
    case annullato
    case erroreAvvio(String)
    case interrotto(String)
}

/// Riceve i fotogrammi sulla coda di cattura e li passa al compressore. I conteggi si fanno
/// qui e non sul thread principale: se l'app viene sospesa in background, il thread
/// principale non gira e il fotogramma verrebbe attribuito al momento sbagliato.
final class Ricevitore: NSObject, SCStreamOutput, SCStreamDelegate, SCContentSharingPickerObserver, @unchecked Sendable {
    let contatori = Mutex(Contatori())
    let compressore: CompressoreH264
    private let eventi: @Sendable (EventoCattura) -> Void

    // ScreenCaptureKit su iOS non permette di chiedere meno fotogrammi al secondo
    // (minimumFrameInterval non esiste): il ritmo lo limitiamo noi. Il margine evita di
    // scartare fotogrammi arrivati con qualche millisecondo di anticipo.
    private let intervalloMinimo = 1.0 / Double(CompressoreH264.fps) - 0.004

    init(compressore: CompressoreH264, eventi: @escaping @Sendable (EventoCattura) -> Void) {
        self.compressore = compressore
        self.eventi = eventi
    }

    func impostaBackground(_ attivo: Bool) {
        let adesso = ProcessInfo.processInfo.systemUptime
        contatori.withLock { c in
            if attivo, !c.inBackground {
                c.inizioBackground = adesso
            } else if !attivo, c.inBackground, let inizio = c.inizioBackground {
                c.secondiInBackground += adesso - inizio
                c.inizioBackground = nil
            }
            c.inBackground = attivo
        }
    }

    func stream(_ stream: SCStream, didOutputSampleBuffer sampleBuffer: CMSampleBuffer, of type: SCStreamOutputType) {
        let adesso = ProcessInfo.processInfo.systemUptime
        switch type {
        case .screen:
            // Su macOS arrivano anche fotogrammi "inattivi" senza immagine: contiamo solo quelli veri.
            guard let immagine = CMSampleBufferGetImageBuffer(sampleBuffer) else { return }
            let tempo = CMSampleBufferGetPresentationTimeStamp(sampleBuffer)
            let larghezza = CVPixelBufferGetWidth(immagine)
            let altezza = CVPixelBufferGetHeight(immagine)
            let accettato = contatori.withLock { c -> Bool in
                c.fotogrammi += 1
                if c.inBackground { c.fotogrammiInBackground += 1 }
                c.larghezza = larghezza
                c.altezza = altezza
                if let ultimo = c.ultimoFotogramma {
                    c.pausaMassima = max(c.pausaMassima, adesso - ultimo)
                }
                c.ultimoFotogramma = adesso
                let secondi = tempo.seconds
                if let precedente = c.ultimoAccettato, secondi - precedente < intervalloMinimo {
                    c.scartatiPerRitmo += 1
                    return false
                }
                c.ultimoAccettato = secondi
                return true
            }
            if accettato {
                let istante = Int64(Date().timeIntervalSince1970 * 1_000_000)
                compressore.comprimi(immagine, tempo: tempo, istanteCatturaMicrosecondi: istante)
            }
        case .audio:
            contatori.withLock { $0.pacchettiAudio += 1 }
        default:
            break
        }
    }

    func stream(_ stream: SCStream, didStopWithError error: any Error) {
        eventi(.interrotto(error.localizedDescription))
    }

    func contentSharingPicker(_ picker: SCContentSharingPicker, didUpdateWith filter: SCContentFilter, for stream: SCStream?) {
        eventi(.filtroScelto(Scatola(valore: filter)))
    }

    func contentSharingPicker(_ picker: SCContentSharingPicker, didCancelFor stream: SCStream?) {
        eventi(.annullato)
    }

    func contentSharingPickerStartDidFailWithError(_ error: any Error) {
        eventi(.erroreAvvio(error.localizedDescription))
    }
}

@MainActor
@Observable
final class Cattura {
    // Larghezza di partenza del video (come i 720p della Fase 1); l'altezza segue le
    // proporzioni dello schermo, quindi il video è verticale.
    static let larghezzaVideo = 720

    private(set) var stato = "Ferma"
    private(set) var contatori = Contatori()
    private(set) var compressione = StatisticheCompressione()
    private(set) var kbitAlSecondo = 0
    private(set) var kbitPicco = 0
    private(set) var rete = StatisticheTrasmissione()
    private(set) var dimensioneRichiesta = "–"
    private(set) var errore: String?

    var disponibile: Bool { SCContentSharingPicker.shared.isAvailable }

    @ObservationIgnored private var stream: SCStream?
    @ObservationIgnored private var ricevitore: Ricevitore?
    @ObservationIgnored private var trasmissione: Trasmissione?
    @ObservationIgnored private var inBackground = false
    @ObservationIgnored private var misuraBitrate: (byte: Int, istante: TimeInterval)?
    @ObservationIgnored private let coda = DispatchQueue(label: "io.github.andreitelea.mirrorloop.cattura")

    func avvia(indirizzoPC: String) {
        ferma()
        errore = nil
        contatori = Contatori()
        compressione = StatisticheCompressione()
        rete = StatisticheTrasmissione()
        kbitAlSecondo = 0
        kbitPicco = 0
        misuraBitrate = nil

        let (larghezza, altezza) = Self.dimensioniVideo()
        dimensioneRichiesta = "\(larghezza)×\(altezza)"
        // Il compressore e la trasmissione si conoscono a vicenda: uno consegna i frame,
        // l'altra chiede un keyframe quando ne ha bisogno.
        let scatolaCompressore = Mutex<CompressoreH264?>(nil)
        let trasmissione = Trasmissione(indirizzo: indirizzoPC) {
            scatolaCompressore.withLock { $0?.forzaKeyframe() }
        }
        let compressore = CompressoreH264(larghezza: larghezza, altezza: altezza) { frame in
            trasmissione.invia(frame)
        }
        scatolaCompressore.withLock { $0 = compressore }
        self.trasmissione = trasmissione
        trasmissione.avvia()
        let ricevitore = Ricevitore(compressore: compressore) { evento in
            Task { @MainActor in self.gestisci(evento) }
        }
        ricevitore.impostaBackground(inBackground)
        self.ricevitore = ricevitore

        let picker = SCContentSharingPicker.shared
        picker.add(ricevitore)
        picker.isActive = true
        picker.present()
        stato = "Scegli cosa condividere…"
    }

    func ferma() {
        aggiorna()
        stream?.stopCapture { _ in }
        stream = nil
        if let ricevitore {
            SCContentSharingPicker.shared.remove(ricevitore)
            // Chiusa sulla coda di cattura, l'unica che usa il compressore.
            let compressore = ricevitore.compressore
            coda.async { compressore.chiudi() }
        }
        SCContentSharingPicker.shared.isActive = false
        ricevitore = nil
        trasmissione?.ferma()
        trasmissione = nil
        stato = "Ferma"
    }

    func cambiaFase(inBackground: Bool) {
        self.inBackground = inBackground
        ricevitore?.impostaBackground(inBackground)
    }

    /// Copia i conteggi della coda di cattura nelle proprietà mostrate a schermo.
    func aggiorna() {
        if let trasmissione {
            rete = trasmissione.statistiche.withLock { $0 }
        }
        guard let ricevitore else { return }
        contatori = ricevitore.contatori.withLock { $0 }
        compressione = ricevitore.compressore.statistiche.withLock { $0 }
        let adesso = ProcessInfo.processInfo.systemUptime
        if let precedente = misuraBitrate, adesso - precedente.istante >= 1 {
            let bit = Double(compressione.byteTotali - precedente.byte) * 8
            kbitAlSecondo = Int(bit / (adesso - precedente.istante) / 1000)
            kbitPicco = max(kbitPicco, kbitAlSecondo)
            misuraBitrate = (compressione.byteTotali, adesso)
        } else if misuraBitrate == nil {
            misuraBitrate = (compressione.byteTotali, adesso)
        }
    }

    /// 720 pixel di larghezza e altezza in proporzione allo schermo, arrotondata a un multiplo
    /// di 16: H.264 lavora a blocchi di 16×16 e con altezze diverse il decodificatore di Windows
    /// aggiunge righe di riempimento in fondo all'immagine. La differenza di proporzioni è minima.
    private static func dimensioniVideo() -> (Int, Int) {
        let scena = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first
        let schermo = scena?.screen.nativeBounds.size ?? CGSize(width: 1170, height: 2532)
        let lato = min(schermo.width, schermo.height)
        let altezza = Int((Double(larghezzaVideo) * max(schermo.width, schermo.height) / lato).rounded())
        return (larghezzaVideo, Int((Double(altezza) / 16).rounded(.down)) * 16)
    }

    private func gestisci(_ evento: EventoCattura) {
        switch evento {
        case .filtroScelto(let scatola):
            // Su iOS non esiste updateContentFilter: una nuova scelta richiede un nuovo flusso.
            if let stream {
                stream.stopCapture { _ in }
                self.stream = nil
            }
            avviaFlusso(scatola.valore)
        case .annullato:
            if stream == nil {
                ferma()
                stato = "Annullata"
            }
        case .erroreAvvio(let messaggio):
            errore = messaggio
            stato = "Errore all'avvio"
        case .interrotto(let messaggio):
            aggiorna()
            errore = messaggio
            stream = nil
            stato = "Interrotta"
        }
    }

    private func avviaFlusso(_ filtro: SCContentFilter) {
        guard let ricevitore else { return }
        let (larghezza, altezza) = Self.dimensioniVideo()
        let configurazione = SCStreamConfiguration()
        configurazione.width = larghezza
        configurazione.height = altezza
        configurazione.capturesAudio = true
        let stream = SCStream(filter: filtro, configuration: configurazione, delegate: ricevitore)
        do {
            try stream.addStreamOutput(ricevitore, type: .screen, sampleHandlerQueue: coda)
            try stream.addStreamOutput(ricevitore, type: .audio, sampleHandlerQueue: coda)
        } catch {
            errore = error.localizedDescription
            stato = "Errore"
            return
        }
        self.stream = stream
        stato = "Avvio…"
        stream.startCapture { errore in
            let messaggio = errore?.localizedDescription
            Task { @MainActor in self.avviato(errore: messaggio) }
        }
    }

    private func avviato(errore messaggio: String?) {
        if let messaggio {
            errore = messaggio
            stream = nil
            stato = "Errore all'avvio"
        } else {
            stato = "In cattura"
        }
    }
}
