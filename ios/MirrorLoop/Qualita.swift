/// Le impostazioni del video, scelte nell'app prima di avviare.
enum Qualita: String, CaseIterable, Sendable {
    /// Quella delle prime prove: leggera per il telefono e per la rete.
    case standard = "Standard"
    /// Per le dirette in 1080p: con il telefono in orizzontale il lato corto diventa l'altezza
    /// della diretta. 1088 e non 1080 perché la larghezza deve essere un multiplo di 16: con 1080
    /// il decompressore di Windows restituisce 1088 colonne, di cui 8 di riempimento (prova del
    /// 22/10/2026).
    case alta = "Alta"
    /// La risoluzione esatta dello schermo, qualunque iPhone sia: un pixel del telefono è un pixel
    /// sul PC, senza ridimensionamenti né cambi di proporzioni. Le misure non multiple di 16
    /// (1320×2868 sul 17 Pro Max) vanno bene perché il ricevitore toglie il bordo di riempimento.
    case massima = "Massima"

    /// Il lato corto del video in pixel; l'altro segue le proporzioni dello schermo.
    /// Nil per Massima: si usano le misure dello schermo così come sono.
    var latoCorto: Int? {
        switch self {
        case .standard: 720
        case .alta: 1088
        case .massima: nil
        }
    }

    var fps: Int {
        switch self {
        case .standard: 30
        case .alta, .massima: 60
        }
    }

    /// Bitrate medio chiesto al compressore, in bit al secondo. Massima: prova del 24/10/2026 sul
    /// 17 Pro Max a 1320×2868: a 40 Mbit/s nessuna immagine persa, a 60 e 80 il Wi-Fi dell'iPhone
    /// non reggeva (ritardi fino a 2 s, centinaia di immagini scartate). 30 per avere margine.
    var bitrate: Int {
        switch self {
        case .standard: 4_000_000
        case .alta: 10_000_000
        case .massima: 30_000_000
        }
    }
}
