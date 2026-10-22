import Foundation
import Network
import Observation

/// Dove mandare la trasmissione.
enum DestinazionePC: Sendable, Equatable {
    /// Un PC trovato con Bonjour, per nome: l'indirizzo si ricava a ogni collegamento.
    case servizio(String)
    /// Indirizzo scritto a mano, per le reti dove Bonjour non passa.
    case indirizzo(String)

    var descrizione: String {
        switch self {
        case .servizio(let nome): nome
        case .indirizzo(let indirizzo): indirizzo
        }
    }
}

/// Cerca i ricevitori MirrorLoop nella rete di casa: il PC si annuncia con mDNS/DNS-SD (Bonjour)
/// come servizio `_mirrorloop._tcp`, dichiarato anche in NSBonjourServices (project.yml).
@MainActor
@Observable
final class CercaPC {
    static let tipoServizio = "_mirrorloop._tcp"

    /// I nomi dei PC trovati (di solito il nome del computer), in ordine alfabetico.
    private(set) var trovati: [String] = []
    private(set) var stato = "Ricerca dei PC…"

    @ObservationIgnored private var browser: NWBrowser?

    func avvia() {
        guard browser == nil else { return }
        let nuovo = NWBrowser(for: .bonjour(type: Self.tipoServizio, domain: nil), using: NWParameters())
        nuovo.browseResultsChangedHandler = { [weak self] risultati, _ in
            let nomi = risultati.compactMap { risultato -> String? in
                if case .service(let nome, _, _, _) = risultato.endpoint { nome } else { nil }
            }
            Task { @MainActor in self?.trovati = Set(nomi).sorted() }
        }
        nuovo.stateUpdateHandler = { [weak self] stato in
            Task { @MainActor in self?.descrivi(stato) }
        }
        browser = nuovo
        nuovo.start(queue: .main)
    }

    func ferma() {
        browser?.cancel()
        browser = nil
    }

    private func descrivi(_ statoBrowser: NWBrowser.State) {
        switch statoBrowser {
        case .ready:
            stato = "Ricerca dei PC attiva"
        case .waiting(let errore), .failed(let errore):
            // Il caso tipico è il permesso "Rete locale" negato: senza, iOS blocca Bonjour.
            stato = "Ricerca dei PC bloccata: \(errore.localizedDescription). Controlla Impostazioni › Privacy e sicurezza › Rete locale."
        default:
            break
        }
    }
}
