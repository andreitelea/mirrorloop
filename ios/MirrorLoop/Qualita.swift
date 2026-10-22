/// Le impostazioni del video, scelte nell'app prima di avviare.
enum Qualita: String, CaseIterable, Sendable {
    /// Quella delle prime prove: leggera per il telefono e per la rete.
    case standard = "Standard"
    /// Per le dirette in 1080p: con il telefono in orizzontale il lato corto diventa l'altezza
    /// della diretta. 1088 e non 1080 perché la larghezza deve essere un multiplo di 16: con 1080
    /// il decompressore di Windows restituisce 1088 colonne, di cui 8 di riempimento (prova del
    /// 22/10/2026).
    case alta = "Alta"

    /// Il lato corto del video in pixel; l'altro segue le proporzioni dello schermo.
    var latoCorto: Int {
        switch self {
        case .standard: 720
        case .alta: 1088
        }
    }

    var fps: Int {
        switch self {
        case .standard: 30
        case .alta: 60
        }
    }

    /// Bitrate medio chiesto al compressore, in bit al secondo.
    var bitrate: Int {
        switch self {
        case .standard: 4_000_000
        case .alta: 10_000_000
        }
    }
}
