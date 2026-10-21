import AVFAudio
import CoreMedia
import Foundation

/// Un pezzo di audio pronto per il protocollo (docs/protocollo.md, tipo 2, codifica 1):
/// PCM a 16 bit little-endian, canali intercalati.
struct AudioPcm16: Sendable {
    let dati: Data
    let canali: Int
    let frequenza: Int
    let istanteCatturaMicrosecondi: Int64
}

/// Converte l'audio di ScreenCaptureKit nel formato del protocollo. Il formato d'origine
/// (virgola mobile o intero, canali separati o intercalati) si legge dal campione stesso
/// invece di darlo per scontato.
enum ConvertitoreAudio {
    static func pcm16(da campione: CMSampleBuffer, istante: Int64) -> (AudioPcm16, descrizione: String)? {
        guard let descrizioneFormato = CMSampleBufferGetFormatDescription(campione) else { return nil }
        let formato = AVAudioFormat(cmAudioFormatDescription: descrizioneFormato)
        let fotogrammi = CMSampleBufferGetNumSamples(campione)
        guard fotogrammi > 0,
              let buffer = AVAudioPCMBuffer(pcmFormat: formato, frameCapacity: AVAudioFrameCount(fotogrammi)) else { return nil }
        buffer.frameLength = buffer.frameCapacity
        guard CMSampleBufferCopyPCMDataIntoAudioBufferList(
            campione, at: 0, frameCount: Int32(fotogrammi), into: buffer.mutableAudioBufferList) == noErr else { return nil }

        let canali = Int(formato.channelCount)
        let n = Int(buffer.frameLength)
        // Con i canali intercalati i campioni stanno tutti nel primo buffer, a passi di `stride`.
        let passo = buffer.stride
        let intercalato = formato.isInterleaved
        var uscita = Data(count: n * canali * 2)
        var riuscito = true
        uscita.withUnsafeMutableBytes { grezzi in
            let destinazione = grezzi.bindMemory(to: Int16.self)
            switch formato.commonFormat {
            case .pcmFormatFloat32:
                guard let sorgente = buffer.floatChannelData else { riuscito = false; return }
                for f in 0..<n {
                    for c in 0..<canali {
                        let valore = intercalato ? sorgente[0][f * passo + c] : sorgente[c][f]
                        let limitato = max(-1, min(1, valore))
                        destinazione[f * canali + c] = Int16(limitato * 32767).littleEndian
                    }
                }
            case .pcmFormatInt16:
                guard let sorgente = buffer.int16ChannelData else { riuscito = false; return }
                for f in 0..<n {
                    for c in 0..<canali {
                        let valore = intercalato ? sorgente[0][f * passo + c] : sorgente[c][f]
                        destinazione[f * canali + c] = valore.littleEndian
                    }
                }
            default:
                riuscito = false
            }
        }
        guard riuscito else { return nil }

        let tipo = switch formato.commonFormat {
        case .pcmFormatFloat32: "Float32"
        case .pcmFormatInt16: "Int16"
        default: "altro"
        }
        let descrizione = "\(tipo) \(intercalato ? "intercalato" : "separato"), \(canali) canali, \(Int(formato.sampleRate)) Hz, \(n) campioni"
        let audio = AudioPcm16(dati: uscita, canali: canali, frequenza: Int(formato.sampleRate), istanteCatturaMicrosecondi: istante)
        return (audio, descrizione)
    }
}
