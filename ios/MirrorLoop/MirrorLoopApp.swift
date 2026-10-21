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
    // L'indirizzo del PC si ricorda tra un avvio e l'altro.
    @AppStorage("indirizzoPC") private var indirizzoPC = ""
    @AppStorage("trasporto") private var trasporto = Trasporto.quic

    var body: some View {
        let c = cattura.contatori
        let s = cattura.compressione
        let r = cattura.rete
        VStack(alignment: .leading, spacing: 6) {
            Text("Trasmissione al PC")
                .font(.headline)
            TextField("Indirizzo del PC (es. 192.168.1.23)", text: $indirizzoPC)
                .textFieldStyle(.roundedBorder)
                .keyboardType(.numbersAndPunctuation)
                .textInputAutocapitalization(.never)
                .autocorrectionDisabled()
            Picker("Trasporto", selection: $trasporto) {
                ForEach(Trasporto.allCases, id: \.self) { Text($0.rawValue) }
            }
            .pickerStyle(.segmented)
            if !cattura.disponibile {
                Text("La registrazione dello schermo non è disponibile su questo iPhone.")
                    .foregroundStyle(.red)
            }
            HStack {
                Button("Avvia") { cattura.avvia(indirizzoPC: indirizzoPC.trimmingCharacters(in: .whitespaces), trasporto: trasporto) }
                    .buttonStyle(.borderedProminent)
                    .disabled(indirizzoPC.trimmingCharacters(in: .whitespaces).isEmpty)
                Button("Ferma") { cattura.ferma() }
                    .buttonStyle(.bordered)
            }
            Text("Cattura: \(cattura.stato)")
            Text("Rete: \(r.stato)")
            Text("Inviati: \(r.inviati) · scartati \(r.scartati) · connessioni \(r.connessioni) · ping \(r.pingRisposti)")
            if let erroreRete = r.ultimoErrore {
                Text("Ultimo errore di rete: \(erroreRete)")
                    .foregroundStyle(.orange)
            }
            Text("Catturati: \(c.fotogrammi) (in background \(c.fotogrammiInBackground) in \(c.secondiInBackground, format: .number.precision(.fractionLength(1))) s)")
            Text("Scartati per il ritmo: \(c.scartatiPerRitmo)")
            Text("Dimensione: \(c.larghezza)×\(c.altezza) (richiesta \(cattura.dimensioneRichiesta))")
            Text("Orientamento: \(c.orientamento.map { String($0) } ?? "—") → rotazione \(Int(Ricevitore.rotazione(da: c.orientamento)) * 90)° · cambi \(c.cambiOrientamento)")
            Text("Compressi: \(s.frameCompressi) · keyframe \(s.keyframe)")
            Text("Bitrate: \(cattura.kbitAlSecondo) kbit/s (picco \(cattura.kbitPicco))")
            Text("Compressione: \(s.msUltimaCompressione, format: .number.precision(.fractionLength(1))) ms (max \(s.msCompressioneMassima, format: .number.precision(.fractionLength(1))) ms)")
            Text("Errori codificatore: \(s.errori) (ultimo \(s.ultimoErrore)) · sessioni \(s.sessioniCreate) · impostazioni rifiutate \(s.impostazioniRifiutate)")
            Text("Audio: \(c.pacchettiAudio) catturati · inviati \(r.audioInviati) · scartati \(r.audioScartati) · non convertiti \(c.audioNonConvertiti)")
            Text("Formato audio: \(c.formatoAudio)")
            Text("Pause in cattura: video \(c.pausaMassima * 1000, format: .number.precision(.fractionLength(0))) ms · audio \(c.pausaMassimaAudio * 1000, format: .number.precision(.fractionLength(0))) ms")
            Text("Coda di invio: picco \(r.piccoByteInAttesa / 1024) KB")
            Text("Ritardo di consegna: video \(c.ritardoConsegnaVideoMs, format: .number.precision(.fractionLength(1))) ms · audio \(c.ritardoConsegnaAudioMs, format: .number.precision(.fractionLength(1))) ms")
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
