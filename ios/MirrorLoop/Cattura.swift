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
    var audioConvertiti = 0
    var audioNonConvertiti = 0
    var formatoAudio = "—"
    var ritardoConsegnaVideoMs: Double = 0
    var ritardoConsegnaAudioMs: Double = 0
    // Pause più lunghe tra due consegne di ScreenCaptureKit: se sono grandi, il ritardo nasce
    // sull'iPhone (cattura) e non in rete.
    var pausaMassimaAudio: TimeInterval = 0
    var ultimoAudio: TimeInterval?
    var larghezza = 0
    var altezza = 0
    // Ultimo valore grezzo di videoOrientation (CGImagePropertyOrientation), se ScreenCaptureKit lo
    // allega: su iOS 27.0.1 non succede mai, ma se un aggiornamento lo portasse vincerebbe sul sensore.
    var orientamento: UInt32?
    /// La rotazione mandata con l'ultimo fotogramma accettato (passi da 90° in senso orario).
    var rotazione: UInt8 = 0
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
    private let audio: @Sendable (AudioPcm16) -> Void
    private let eventi: @Sendable (EventoCattura) -> Void
    private let orologio: OrologioCattura

    // ScreenCaptureKit su iOS non permette di chiedere meno fotogrammi al secondo
    // (minimumFrameInterval non esiste): il ritmo lo limitiamo noi. Il margine evita di
    // scartare fotogrammi arrivati con qualche millisecondo di anticipo.
    private let intervalloMinimo: Double
    let sensore: SensoreOrientamento

    init(
        compressore: CompressoreH264,
        orologio: OrologioCattura,
        sensore: SensoreOrientamento,
        audio: @escaping @Sendable (AudioPcm16) -> Void,
        eventi: @escaping @Sendable (EventoCattura) -> Void
    ) {
        self.compressore = compressore
        self.intervalloMinimo = 1.0 / Double(compressore.fps) - 0.004
        self.orologio = orologio
        self.sensore = sensore
        self.audio = audio
        self.eventi = eventi
    }

    private func istanteMicrosecondi(_ tempo: CMTime) -> Int64 {
        orologio.microsecondi(tempo)
    }

    /// L'orientamento che ScreenCaptureKit allega al fotogramma (SCStreamFrameInfo.videoOrientation,
    /// iOS 27): un valore di CGImagePropertyOrientation. Nil se manca.
    private static func orientamento(_ campione: CMSampleBuffer) -> UInt32? {
        let allegati = CMSampleBufferGetSampleAttachmentsArray(campione, createIfNecessary: false) as? [[SCStreamFrameInfo: Any]]
        return (allegati?.first?[.videoOrientation] as? NSNumber)?.uint32Value
    }

    /// Di quanti passi da 90° in senso orario girare l'immagine per mostrarla dritta (protocollo,
    /// bit 1 e 2 del flag). Valori di CGImagePropertyOrientation come nelle foto (EXIF): 6 = da
    /// girare di 90° in senso orario, 8 = di 270°, 3 = capovolta. Le varianti a specchio non
    /// dovrebbero comparire: si trattano come la rotazione corrispondente.
    static func rotazione(da orientamento: UInt32?) -> UInt8 {
        switch orientamento {
        case 3, 4: 2
        case 6, 7: 1
        case 5, 8: 3
        default: 0
        }
    }

    /// Quanto tempo è passato tra la cattura del campione e il suo arrivo qui. Se gli istanti
    /// non fossero sull'orologio di sistema, questo numero sarebbe assurdo: è il controllo.
    private func ritardoConsegnaMs(_ tempo: CMTime) -> Double {
        (CMClockGetTime(CMClockGetHostTimeClock()).seconds - tempo.seconds) * 1000
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
            let ritardo = ritardoConsegnaMs(tempo)
            let orientamento = Self.orientamento(sampleBuffer)
            let rotazione = orientamento != nil
                ? Self.rotazione(da: orientamento)
                : sensore.stato.withLock { $0.rotazione }
            let accettato = contatori.withLock { c -> Bool in
                c.fotogrammi += 1
                c.ritardoConsegnaVideoMs = ritardo
                if c.inBackground { c.fotogrammiInBackground += 1 }
                c.larghezza = larghezza
                c.altezza = altezza
                c.orientamento = orientamento
                c.rotazione = rotazione
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
                compressore.comprimi(immagine, tempo: tempo, istanteCatturaMicrosecondi: istanteMicrosecondi(tempo),
                                     rotazione: rotazione)
            }
        case .audio:
            let tempo = CMSampleBufferGetPresentationTimeStamp(sampleBuffer)
            let ritardo = ritardoConsegnaMs(tempo)
            let convertito = ConvertitoreAudio.pcm16(da: sampleBuffer, istante: istanteMicrosecondi(tempo))
            contatori.withLock { c in
                c.pacchettiAudio += 1
                c.ritardoConsegnaAudioMs = ritardo
                if let ultimo = c.ultimoAudio {
                    c.pausaMassimaAudio = max(c.pausaMassimaAudio, adesso - ultimo)
                }
                c.ultimoAudio = adesso
                if let convertito {
                    c.audioConvertiti += 1
                    c.formatoAudio = convertito.descrizione
                } else {
                    c.audioNonConvertiti += 1
                }
            }
            if let convertito {
                audio(convertito.0)
            }
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
    private(set) var stato = "Ferma"
    private(set) var qualita = Qualita.standard
    private(set) var contatori = Contatori()
    private(set) var compressione = StatisticheCompressione()
    private(set) var kbitAlSecondo = 0
    private(set) var kbitPicco = 0
    private(set) var rete = StatisticheTrasmissione()
    private(set) var sensore = SensoreOrientamento.Stato()
    private(set) var dimensioneRichiesta = "–"
    private(set) var errore: String?

    var disponibile: Bool { SCContentSharingPicker.shared.isAvailable }

    @ObservationIgnored private var stream: SCStream?
    @ObservationIgnored private var ricevitore: Ricevitore?
    @ObservationIgnored private var trasmissione: Trasmissione?
    @ObservationIgnored private var inBackground = false
    @ObservationIgnored private var misuraBitrate: (byte: Int, istante: TimeInterval)?
    @ObservationIgnored private let coda = DispatchQueue(label: "io.github.andreitelea.mirrorloop.cattura")
    // L'audio ha una coda tutta sua: sulla coda del video c'è anche il compressore, che a volte si
    // ferma per centinaia di millisecondi (per esempio quando iOS lo fa ripartire), e l'audio
    // restava bloccato dietro di lui (registro del 21/10/2026: pausa di 481 ms).
    @ObservationIgnored private let codaAudio = DispatchQueue(label: "io.github.andreitelea.mirrorloop.audio", qos: .userInteractive)

    func avvia(destinazione: DestinazionePC, qualita: Qualita) {
        ferma()
        self.qualita = qualita
        errore = nil
        contatori = Contatori()
        compressione = StatisticheCompressione()
        rete = StatisticheTrasmissione()
        sensore = SensoreOrientamento.Stato()
        kbitAlSecondo = 0
        kbitPicco = 0
        misuraBitrate = nil

        let (larghezza, altezza) = Self.dimensioniVideo(qualita: qualita)
        dimensioneRichiesta = "\(larghezza)×\(altezza)"
        // Il compressore e la trasmissione si conoscono a vicenda: uno consegna i frame,
        // l'altra chiede un keyframe quando ne ha bisogno.
        let orologio = OrologioCattura()
        let scatolaCompressore = Mutex<CompressoreH264?>(nil)
        let trasmissione = Trasmissione(destinazione: destinazione, orologio: orologio) {
            scatolaCompressore.withLock { $0?.forzaKeyframe() }
        }
        let compressore = CompressoreH264(larghezza: larghezza, altezza: altezza, fps: qualita.fps,
                                          bitrate: qualita.bitrate) { frame in
            trasmissione.invia(frame)
        }
        scatolaCompressore.withLock { $0 = compressore }
        self.trasmissione = trasmissione
        trasmissione.avvia()
        let sensore = SensoreOrientamento()
        sensore.avvia()
        let ricevitore = Ricevitore(compressore: compressore, orologio: orologio, sensore: sensore) { audio in
            trasmissione.inviaAudio(audio)
        } eventi: { evento in
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
            ricevitore.sensore.ferma()
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
        sensore = ricevitore.sensore.stato.withLock { $0 }
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

    /// Le misure del video, sempre in verticale come il pannello. Massima: quelle dello schermo
    /// (nativeBounds, in pixel). Le altre: il lato corto della qualità come larghezza e l'altezza in
    /// proporzione allo schermo, arrotondata a un multiplo di 16 (H.264 lavora a blocchi di 16×16;
    /// la differenza di proporzioni è minima).
    static func dimensioniVideo(qualita: Qualita) -> (Int, Int) {
        let scena = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first
        let schermo = scena?.screen.nativeBounds.size ?? CGSize(width: 1170, height: 2532)
        let lato = min(schermo.width, schermo.height)
        let latoLungo = max(schermo.width, schermo.height)
        guard let latoCorto = qualita.latoCorto else {
            return (Int(lato), Int(latoLungo))
        }
        let altezza = Int((Double(latoCorto) * latoLungo / lato).rounded())
        return (latoCorto, Int((Double(altezza) / 16).rounded(.down)) * 16)
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
        let (larghezza, altezza) = Self.dimensioniVideo(qualita: qualita)
        let configurazione = SCStreamConfiguration()
        configurazione.width = larghezza
        configurazione.height = altezza
        configurazione.capturesAudio = true
        // I valori predefiniti di ScreenCaptureKit, scritti per esteso: sono quelli che il
        // ricevitore si aspetta.
        configurazione.sampleRate = 48_000
        configurazione.channelCount = 2
        let stream = SCStream(filter: filtro, configuration: configurazione, delegate: ricevitore)
        do {
            try stream.addStreamOutput(ricevitore, type: .screen, sampleHandlerQueue: coda)
            try stream.addStreamOutput(ricevitore, type: .audio, sampleHandlerQueue: codaAudio)
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
