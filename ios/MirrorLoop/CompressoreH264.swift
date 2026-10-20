import CoreMedia
import Foundation
import Synchronization
import VideoToolbox

/// Un frame compresso nel formato del protocollo (docs/protocollo.md, tipo 1):
/// Annex B, con SPS e PPS davanti a ogni keyframe e senza AUD.
struct FrameCompresso: Sendable {
    let datiAnnexB: Data
    let keyframe: Bool
    let istanteCatturaMicrosecondi: Int64
}

struct StatisticheCompressione: Sendable {
    var frameCompressi = 0
    var keyframe = 0
    var byteTotali = 0
    var msUltimaCompressione: Double = 0
    var msCompressioneMassima: Double = 0
    var errori = 0
    var ultimoErrore: OSStatus = 0
    var impostazioniRifiutate = 0
    var sessioniCreate = 0
}

/// Comprime in H.264 con il codificatore hardware dell'iPhone (VideoToolbox).
/// `comprimi` va chiamato sempre dalla stessa coda; i frame compressi arrivano su un thread
/// di VideoToolbox e vengono passati a `consegna`.
final class CompressoreH264: @unchecked Sendable {
    // Stesse impostazioni di partenza della Fase 1: ~4 Mbit/s, keyframe ogni 2 s, niente B-frame.
    static let bitrate = 4_000_000
    static let fps = 30
    static let secondiTraKeyframe = 2

    let statistiche = Mutex(StatisticheCompressione())

    private let larghezza: Int32
    private let altezza: Int32
    private let consegna: @Sendable (FrameCompresso) -> Void
    private var sessione: VTCompressionSession?

    init(larghezza: Int, altezza: Int, consegna: @escaping @Sendable (FrameCompresso) -> Void) {
        self.larghezza = Int32(larghezza)
        self.altezza = Int32(altezza)
        self.consegna = consegna
    }

    deinit {
        chiudi()
    }

    func chiudi() {
        guard let sessione else { return }
        VTCompressionSessionCompleteFrames(sessione, untilPresentationTimeStamp: .invalid)
        VTCompressionSessionInvalidate(sessione)
        self.sessione = nil
    }

    func comprimi(_ immagine: CVImageBuffer, tempo: CMTime, istanteCatturaMicrosecondi: Int64) {
        if sessione == nil {
            creaSessione()
        }
        guard let sessione else { return }
        let inizio = ContinuousClock.now
        let stato = VTCompressionSessionEncodeFrame(
            sessione,
            imageBuffer: immagine,
            presentationTimeStamp: tempo,
            duration: .invalid,
            frameProperties: nil,
            infoFlagsOut: nil
        ) { [self] stato, _, campione in
            completato(stato: stato, campione: campione, inizio: inizio, istante: istanteCatturaMicrosecondi)
        }
        if stato != noErr {
            registraErrore(stato)
            // Per esempio kVTInvalidSessionErr: si ricrea la sessione al frame successivo.
            chiudi()
        }
    }

    private func creaSessione() {
        // La modalità a bassa latenza di VideoToolbox è pensata per le videochiamate:
        // niente frame in attesa dentro al codificatore.
        let specifica = [kVTVideoEncoderSpecification_EnableLowLatencyRateControl: kCFBooleanTrue!] as CFDictionary
        var nuova: VTCompressionSession?
        let stato = VTCompressionSessionCreate(
            allocator: nil,
            width: larghezza,
            height: altezza,
            codecType: kCMVideoCodecType_H264,
            encoderSpecification: specifica,
            imageBufferAttributes: nil,
            compressedDataAllocator: nil,
            outputCallback: nil,
            refcon: nil,
            compressionSessionOut: &nuova
        )
        guard stato == noErr, let nuova else {
            registraErrore(stato)
            return
        }
        let proprieta: [(CFString, CFTypeRef)] = [
            (kVTCompressionPropertyKey_RealTime, kCFBooleanTrue!),
            (kVTCompressionPropertyKey_ProfileLevel, kVTProfileLevel_H264_ConstrainedHigh_AutoLevel),
            (kVTCompressionPropertyKey_AllowFrameReordering, kCFBooleanFalse!),
            (kVTCompressionPropertyKey_AverageBitRate, Self.bitrate as CFNumber),
            (kVTCompressionPropertyKey_ExpectedFrameRate, Self.fps as CFNumber),
            (kVTCompressionPropertyKey_MaxKeyFrameIntervalDuration, Self.secondiTraKeyframe as CFNumber),
        ]
        for (chiave, valore) in proprieta where VTSessionSetProperty(nuova, key: chiave, value: valore) != noErr {
            statistiche.withLock { $0.impostazioniRifiutate += 1 }
        }
        VTCompressionSessionPrepareToEncodeFrames(nuova)
        sessione = nuova
        statistiche.withLock { $0.sessioniCreate += 1 }
    }

    private func completato(stato: OSStatus, campione: CMSampleBuffer?, inizio: ContinuousClock.Instant, istante: Int64) {
        let ms = (ContinuousClock.now - inizio) / .milliseconds(1)
        guard stato == noErr else {
            registraErrore(stato)
            return
        }
        // Campione assente = frame scartato dal codificatore: non è un errore.
        guard let campione, let (dati, keyframe) = Self.annexB(da: campione) else { return }
        statistiche.withLock { s in
            s.frameCompressi += 1
            if keyframe { s.keyframe += 1 }
            s.byteTotali += dati.count
            s.msUltimaCompressione = ms
            s.msCompressioneMassima = max(s.msCompressioneMassima, ms)
        }
        consegna(FrameCompresso(datiAnnexB: dati, keyframe: keyframe, istanteCatturaMicrosecondi: istante))
    }

    private func registraErrore(_ stato: OSStatus) {
        statistiche.withLock { s in
            s.errori += 1
            s.ultimoErrore = stato
        }
    }

    /// VideoToolbox produce il formato "AVCC" (ogni NAL preceduto dalla sua lunghezza, SPS e PPS
    /// a parte nella descrizione del formato). Il protocollo vuole Annex B: codici di inizio
    /// `00 00 00 01` e SPS/PPS in testa a ogni keyframe.
    static func annexB(da campione: CMSampleBuffer) -> (Data, Bool)? {
        guard let blocco = CMSampleBufferGetDataBuffer(campione),
              let formato = CMSampleBufferGetFormatDescription(campione) else { return nil }

        let allegati = CMSampleBufferGetSampleAttachmentsArray(campione, createIfNecessary: false) as? [[CFString: Any]]
        let keyframe = (allegati?.first?[kCMSampleAttachmentKey_NotSync] as? Bool) != true

        let codiceInizio: [UInt8] = [0, 0, 0, 1]
        var uscita = Data()
        var numeroParametri = 0
        var byteLunghezza: Int32 = 4
        CMVideoFormatDescriptionGetH264ParameterSetAtIndex(
            formato, parameterSetIndex: 0, parameterSetPointerOut: nil, parameterSetSizeOut: nil,
            parameterSetCountOut: &numeroParametri, nalUnitHeaderLengthOut: &byteLunghezza)
        if keyframe {
            for indice in 0..<numeroParametri {
                var puntatore: UnsafePointer<UInt8>?
                var dimensione = 0
                let stato = CMVideoFormatDescriptionGetH264ParameterSetAtIndex(
                    formato, parameterSetIndex: indice, parameterSetPointerOut: &puntatore,
                    parameterSetSizeOut: &dimensione, parameterSetCountOut: nil, nalUnitHeaderLengthOut: nil)
                guard stato == noErr, let puntatore else { return nil }
                uscita.append(contentsOf: codiceInizio)
                uscita.append(puntatore, count: dimensione)
            }
        }

        let totale = CMBlockBufferGetDataLength(blocco)
        var avcc = [UInt8](repeating: 0, count: totale)
        guard CMBlockBufferCopyDataBytes(blocco, atOffset: 0, dataLength: totale, destination: &avcc) == noErr else {
            return nil
        }
        let n = Int(byteLunghezza)
        var posizione = 0
        while posizione + n <= totale {
            var lunghezza = 0
            for k in 0..<n {
                lunghezza = (lunghezza << 8) | Int(avcc[posizione + k])
            }
            posizione += n
            guard lunghezza > 0, posizione + lunghezza <= totale else { return nil }
            // Gli AUD (tipo 9) non servono: ogni messaggio contiene già un frame intero.
            if avcc[posizione] & 0x1F != 9 {
                uscita.append(contentsOf: codiceInizio)
                uscita.append(contentsOf: avcc[posizione..<(posizione + lunghezza)])
            }
            posizione += lunghezza
        }
        return (uscita, keyframe)
    }
}
