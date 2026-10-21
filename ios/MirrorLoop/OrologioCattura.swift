import CoreMedia
import Foundation

/// L'unico orologio dell'app per gli istanti del protocollo, in microsecondi dal 1/1/1970 UTC.
///
/// Gli istanti dei campioni di ScreenCaptureKit sono sull'orologio di sistema dell'iPhone
/// (CMClockGetHostTimeClock); lo scarto, calcolato una volta all'avvio, li trasforma nell'ora
/// "da calendario". Video, audio e risposte ai ping del PC usano tutti questo orologio: così
/// il PC, misurando lo scarto con i ping, può correggere esattamente gli istanti di cattura.
struct OrologioCattura: Sendable {
    private let scarto = Date().timeIntervalSince1970 - CMClockGetTime(CMClockGetHostTimeClock()).seconds

    func microsecondi(_ tempo: CMTime) -> Int64 {
        Int64((tempo.seconds + scarto) * 1_000_000)
    }

    func adessoMicrosecondi() -> Int64 {
        microsecondi(CMClockGetTime(CMClockGetHostTimeClock()))
    }
}
