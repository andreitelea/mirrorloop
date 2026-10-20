import CoreMedia
import CoreVideo
import Foundation
import Observation
import ScreenCaptureKit
import Synchronization

// Esperimento della Fase 3: ScreenCaptureKit (iOS 27) continua a consegnare i fotogrammi
// dello schermo intero quando MirrorLoop è in background? Se sì, non serve l'estensione
// ReplayKit con il suo limite di 50 MB.

struct Contatori: Sendable {
    var fotogrammi = 0
    var fotogrammiInBackground = 0
    var pacchettiAudio = 0
    var larghezza = 0
    var altezza = 0
    var pausaMassima: TimeInterval = 0
    var ultimoFotogramma: TimeInterval?
    var inBackground = false
    var inizioBackground: TimeInterval?
    var secondiInBackground: TimeInterval = 0
}

/// Contenitore per far passare tra i thread oggetti di sistema non marcati Sendable,
/// usati poi solo sul thread principale.
struct Scatola<Valore>: @unchecked Sendable {
    let valore: Valore
}

@available(iOS 27.0, *)
enum EventoCattura: Sendable {
    case filtroScelto(Scatola<SCContentFilter>)
    case annullato
    case erroreAvvio(String)
    case interrotto(String)
}

/// Riceve i fotogrammi sulla coda di cattura. I conteggi si fanno qui e non sul thread
/// principale: se l'app viene sospesa in background, il thread principale non gira e
/// il fotogramma verrebbe attribuito al momento sbagliato.
@available(iOS 27.0, *)
final class Ricevitore: NSObject, SCStreamOutput, SCStreamDelegate, SCContentSharingPickerObserver, @unchecked Sendable {
    let contatori = Mutex(Contatori())
    private let eventi: @Sendable (EventoCattura) -> Void

    init(eventi: @escaping @Sendable (EventoCattura) -> Void) {
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
            let larghezza = CVPixelBufferGetWidth(immagine)
            let altezza = CVPixelBufferGetHeight(immagine)
            contatori.withLock { c in
                c.fotogrammi += 1
                if c.inBackground { c.fotogrammiInBackground += 1 }
                c.larghezza = larghezza
                c.altezza = altezza
                if let ultimo = c.ultimoFotogramma {
                    c.pausaMassima = max(c.pausaMassima, adesso - ultimo)
                }
                c.ultimoFotogramma = adesso
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

@available(iOS 27.0, *)
@MainActor
@Observable
final class ProvaCattura {
    private(set) var stato = "Ferma"
    private(set) var contatori = Contatori()
    private(set) var errore: String?

    var disponibile: Bool { SCContentSharingPicker.shared.isAvailable }

    @ObservationIgnored private var stream: SCStream?
    @ObservationIgnored private var ricevitore: Ricevitore?
    @ObservationIgnored private var inBackground = false
    @ObservationIgnored private let coda = DispatchQueue(label: "io.github.andreitelea.mirrorloop.cattura")

    func avvia() {
        ferma()
        errore = nil
        contatori = Contatori()
        let ricevitore = Ricevitore { evento in
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
        }
        SCContentSharingPicker.shared.isActive = false
        ricevitore = nil
        stato = "Ferma"
    }

    func cambiaFase(inBackground: Bool) {
        self.inBackground = inBackground
        ricevitore?.impostaBackground(inBackground)
    }

    /// Copia i conteggi della coda di cattura nelle proprietà mostrate a schermo.
    func aggiorna() {
        if let ricevitore {
            contatori = ricevitore.contatori.withLock { $0 }
        }
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
        let configurazione = SCStreamConfiguration()
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
