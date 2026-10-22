import CoreMotion
import Foundation
import Synchronization

/// Come è tenuto il telefono, letto dal sensore di movimento (CoreMotion), per raddrizzare
/// l'immagine sul PC.
///
/// Serve perché iOS non dice a un'app in background come è girata l'app in primo piano, e
/// ScreenCaptureKit su iOS 27.0.1 non allega l'orientamento ai fotogrammi (verificato il
/// 22/10/2026: `videoOrientation` non c'è mai). Il sensore sbaglia quando l'app in primo piano
/// non ruota (Home, app solo verticali, blocco della rotazione): per quei casi c'è la
/// correzione manuale sul PC.
final class SensoreOrientamento: @unchecked Sendable {
    enum Posizione: Sendable, Equatable {
        case verticale
        /// Il lato sinistro del telefono (visto dallo schermo) verso il basso.
        case orizzontaleSinistra
        /// Il lato destro del telefono verso il basso.
        case orizzontaleDestra
        /// Appoggiato su un piano: il sensore non sa quale sia "l'alto".
        case piatto
    }

    struct Stato: Sendable {
        var posizione: Posizione?
        /// Passi da 90° in senso orario per raddrizzare l'immagine (protocollo, bit 1 e 2 del flag).
        var rotazione: UInt8 = 0
        var cambi = 0
        var letture = 0
    }

    // Dieci letture al secondo bastano a seguire una rotazione e costano pochissimo.
    private static let intervallo: TimeInterval = 0.1
    // iOS ruota l'interfaccia con un po' di ritardo: una posizione nuova vale solo se dura tanto così.
    private static let stabilita: TimeInterval = 0.5

    let stato = Mutex(Stato())
    private let movimento = CMMotionManager()
    private let coda = OperationQueue()
    private var candidata: (posizione: Posizione, da: TimeInterval)?

    init() {
        coda.maxConcurrentOperationCount = 1
        coda.name = "io.github.andreitelea.mirrorloop.sensore"
    }

    func avvia() {
        guard movimento.isDeviceMotionAvailable else { return }
        movimento.deviceMotionUpdateInterval = Self.intervallo
        movimento.startDeviceMotionUpdates(to: coda) { [self] dati, _ in
            guard let dati else { return }
            leggi(dati.gravity, adesso: dati.timestamp)
        }
    }

    func ferma() {
        movimento.stopDeviceMotionUpdates()
    }

    /// La gravità è nelle coordinate del telefono: x verso il lato destro dello schermo, y verso
    /// l'alto, z fuori dallo schermo. Tenuto dritto in verticale la gravità è circa (0, −1, 0).
    private func leggi(_ gravita: CMAcceleration, adesso: TimeInterval) {
        let posizione: Posizione
        if abs(gravita.z) > 0.85 {
            posizione = .piatto
        } else if abs(gravita.x) > abs(gravita.y) {
            posizione = gravita.x < 0 ? .orizzontaleSinistra : .orizzontaleDestra
        } else {
            // Anche capovolto: quasi nessuna app per iPhone ruota a testa in giù.
            posizione = .verticale
        }

        if candidata?.posizione != posizione {
            candidata = (posizione, adesso)
        }
        let stabile = adesso - (candidata?.da ?? adesso) >= Self.stabilita
        stato.withLock { s in
            s.letture += 1
            guard stabile, s.posizione != posizione else { return }
            s.posizione = posizione
            // Piatto: si tiene la rotazione di prima.
            if let rotazione = Self.rotazione(per: posizione), rotazione != s.rotazione {
                s.rotazione = rotazione
                s.cambi += 1
            }
        }
    }

    /// Lato sinistro in basso = telefono girato in senso antiorario: il contenuto è dritto per chi
    /// guarda, quindi nell'immagine del pannello (sempre verticale) il suo "alto" sta sul lato
    /// destro, e per raddrizzarlo va girata di 270° in senso orario. Al contrario per il lato destro.
    /// Il verso va confermato sul telefono.
    static func rotazione(per posizione: Posizione) -> UInt8? {
        switch posizione {
        case .verticale: 0
        case .orizzontaleSinistra: 3
        case .orizzontaleDestra: 1
        case .piatto: nil
        }
    }
}
