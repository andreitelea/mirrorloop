import SwiftUI

@main
struct MirrorLoopApp: App {
    var body: some Scene {
        WindowGroup {
            SchermataPrincipale()
        }
    }
}

struct SchermataPrincipale: View {
    // Letta dall'Info.plist, così il numero mostrato coincide sempre con quello del pacchetto installato.
    private let versione: String = {
        let info = Bundle.main.infoDictionary
        let marketing = info?["CFBundleShortVersionString"] as? String ?? "?"
        let build = info?["CFBundleVersion"] as? String ?? "?"
        return "\(marketing) (\(build))"
    }()

    var body: some View {
        VStack(spacing: 32) {
            VStack(spacing: 12) {
                Text("MirrorLoop")
                    .font(.largeTitle)
                    .bold()
                Text("Versione \(versione)")
                    .font(.footnote)
                    .foregroundStyle(.secondary)
            }
            if #available(iOS 27.0, *) {
                PannelloProvaCattura()
            } else {
                Text("La prova di cattura richiede iOS 27")
                    .font(.footnote)
                    .foregroundStyle(.secondary)
            }
        }
        .padding()
    }
}

@available(iOS 27.0, *)
struct PannelloProvaCattura: View {
    @State private var prova = ProvaCattura()
    @Environment(\.scenePhase) private var fase

    var body: some View {
        let c = prova.contatori
        VStack(alignment: .leading, spacing: 8) {
            Text("Prova di cattura")
                .font(.headline)
            if !prova.disponibile {
                Text("La registrazione dello schermo non è disponibile su questo iPhone.")
                    .foregroundStyle(.red)
            }
            HStack {
                Button("Prova cattura") { prova.avvia() }
                    .buttonStyle(.borderedProminent)
                Button("Ferma") { prova.ferma() }
                    .buttonStyle(.bordered)
            }
            Text("Stato: \(prova.stato)")
            Text("Fotogrammi: \(c.fotogrammi)")
            Text("In background: \(c.fotogrammiInBackground) in \(c.secondiInBackground, format: .number.precision(.fractionLength(1))) s")
            Text("Audio: \(c.pacchettiAudio) pacchetti")
            Text("Dimensione: \(c.larghezza)×\(c.altezza)")
            Text("Pausa massima: \(c.pausaMassima, format: .number.precision(.fractionLength(2))) s")
            if let errore = prova.errore {
                Text("Errore: \(errore)")
                    .foregroundStyle(.red)
            }
        }
        .font(.callout.monospacedDigit())
        .frame(maxWidth: .infinity, alignment: .leading)
        .onChange(of: fase) { _, nuova in
            prova.cambiaFase(inBackground: nuova == .background)
        }
        .task {
            while !Task.isCancelled {
                prova.aggiorna()
                try? await Task.sleep(for: .milliseconds(500))
            }
        }
    }
}
