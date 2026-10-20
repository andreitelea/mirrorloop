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
        VStack(spacing: 12) {
            Text("MirrorLoop")
                .font(.largeTitle)
                .bold()
            Text("Versione \(versione)")
                .font(.footnote)
                .foregroundStyle(.secondary)
        }
        .padding()
    }
}
