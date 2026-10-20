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
            PannelloCattura()
        }
        .padding()
    }
}

struct PannelloCattura: View {
    @State private var cattura = Cattura()
    @Environment(\.scenePhase) private var fase

    var body: some View {
        let c = cattura.contatori
        let s = cattura.compressione
        VStack(alignment: .leading, spacing: 6) {
            Text("Cattura e compressione")
                .font(.headline)
            if !cattura.disponibile {
                Text("La registrazione dello schermo non è disponibile su questo iPhone.")
                    .foregroundStyle(.red)
            }
            HStack {
                Button("Avvia") { cattura.avvia() }
                    .buttonStyle(.borderedProminent)
                Button("Ferma") { cattura.ferma() }
                    .buttonStyle(.bordered)
            }
            Text("Stato: \(cattura.stato)")
            Text("Catturati: \(c.fotogrammi) (in background \(c.fotogrammiInBackground) in \(c.secondiInBackground, format: .number.precision(.fractionLength(1))) s)")
            Text("Scartati per il ritmo: \(c.scartatiPerRitmo)")
            Text("Dimensione: \(c.larghezza)×\(c.altezza) (richiesta \(cattura.dimensioneRichiesta))")
            Text("Compressi: \(s.frameCompressi) · keyframe \(s.keyframe)")
            Text("Bitrate: \(cattura.kbitAlSecondo) kbit/s")
            Text("Compressione: \(s.msUltimaCompressione, format: .number.precision(.fractionLength(1))) ms (max \(s.msCompressioneMassima, format: .number.precision(.fractionLength(1))) ms)")
            Text("Errori codificatore: \(s.errori) (ultimo \(s.ultimoErrore)) · sessioni \(s.sessioniCreate) · impostazioni rifiutate \(s.impostazioniRifiutate)")
            Text("Audio: \(c.pacchettiAudio) pacchetti")
            if let errore = cattura.errore {
                Text("Errore: \(errore)")
                    .foregroundStyle(.red)
            }
        }
        .font(.footnote.monospacedDigit())
        .frame(maxWidth: .infinity, alignment: .leading)
        .onChange(of: fase) { _, nuova in
            cattura.cambiaFase(inBackground: nuova == .background)
        }
        .task {
            while !Task.isCancelled {
                cattura.aggiorna()
                try? await Task.sleep(for: .milliseconds(500))
            }
        }
    }
}
