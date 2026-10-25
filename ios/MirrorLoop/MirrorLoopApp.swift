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
        ScrollView {
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
}

struct PannelloCattura: View {
    @State private var cattura = Cattura()
    @State private var cercaPC = CercaPC()
    @Environment(\.scenePhase) private var fase
    // Il PC scelto e l'indirizzo scritto a mano si ricordano tra un avvio e l'altro.
    // pcScelto: il nome di un PC trovato con Bonjour, `manuale`, oppure vuoto se non ancora scelto.
    @AppStorage("pcScelto") private var pcScelto = ""
    @AppStorage("indirizzoPC") private var indirizzoPC = ""
    private static let manuale = "manuale"
    @AppStorage("trasporto") private var trasporto = Trasporto.quic
    @AppStorage("qualita") private var qualita = Qualita.alta

    var body: some View {
        let c = cattura.contatori
        let s = cattura.compressione
        let r = cattura.rete
        VStack(alignment: .leading, spacing: 6) {
            Text("Trasmissione al PC")
                .font(.headline)
            Picker("PC", selection: $pcScelto) {
                if pcScelto.isEmpty {
                    Text("Scegli il PC").tag("")
                }
                ForEach(opzioniPC, id: \.self) { nome in
                    Text(cercaPC.trovati.contains(nome) ? nome : "\(nome) · non trovato").tag(nome)
                }
                Text("Indirizzo a mano…").tag(Self.manuale)
            }
            .pickerStyle(.menu)
            Text(cercaPC.trovati.isEmpty ? cercaPC.stato : "PC trovati in rete: \(cercaPC.trovati.count)")
                .font(.caption)
                .foregroundStyle(.secondary)
            if pcScelto == Self.manuale {
                TextField("Indirizzo del PC (es. 192.168.1.23)", text: $indirizzoPC)
                    .textFieldStyle(.roundedBorder)
                    .keyboardType(.numbersAndPunctuation)
                    .textInputAutocapitalization(.never)
                    .autocorrectionDisabled()
            }
            Picker("Trasporto", selection: $trasporto) {
                ForEach(Trasporto.allCases, id: \.self) { Text($0.rawValue) }
            }
            .pickerStyle(.segmented)
            Picker("Qualità", selection: $qualita) {
                ForEach(Qualita.allCases, id: \.self) { Text($0.rawValue) }
            }
            .pickerStyle(.segmented)
            let dimensioni = Cattura.dimensioniVideo(qualita: qualita)
            Text("\(dimensioni.0)×\(dimensioni.1) · \(qualita.fps) fps · \(qualita.bitrate / 1_000_000) Mbit/s")
                .font(.caption)
                .foregroundStyle(.secondary)
            if !cattura.disponibile {
                Text("La registrazione dello schermo non è disponibile su questo iPhone.")
                    .foregroundStyle(.red)
            }
            HStack {
                Button("Avvia") {
                    if let destinazione {
                        cattura.avvia(destinazione: destinazione, trasporto: trasporto, qualita: qualita)
                    }
                }
                    .buttonStyle(.borderedProminent)
                    .disabled(destinazione == nil)
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
            Text("Qualità: \(cattura.qualita.rawValue) · \(cattura.qualita.fps) fps · \(cattura.qualita.bitrate / 1_000_000) Mbit/s")
            Text("Dimensione: \(c.larghezza)×\(c.altezza) (richiesta \(cattura.dimensioneRichiesta))")
            Text("Orientamento: \(descrizione(cattura.sensore.posizione)) · rotazione \(Int(c.rotazione) * 90)° · cambi \(cattura.sensore.cambi)\(c.orientamento.map { " · ScreenCaptureKit \($0)" } ?? "")")
            Text("Compressi: \(s.frameCompressi) · keyframe \(s.keyframe)")
            Text("Bitrate: \(cattura.kbitAlSecondo) kbit/s (picco \(cattura.kbitPicco))")
            Text("Compressione: \(s.msUltimaCompressione, format: .number.precision(.fractionLength(1))) ms (max \(s.msCompressioneMassima, format: .number.precision(.fractionLength(1))) ms)")
            Text("Errori codificatore: \(s.errori) (ultimo \(s.ultimoErrore)) · sessioni \(s.sessioniCreate) · impostazioni rifiutate \(s.impostazioniRifiutate)")
            Text("Audio: \(c.pacchettiAudio) catturati · inviati \(r.audioInviati) · scartati \(r.audioScartati) · non convertiti \(c.audioNonConvertiti)")
            Text("Formato audio: \(c.formatoAudio)")
            Text("Pause in cattura: video \(c.pausaMassima * 1000, format: .number.precision(.fractionLength(0))) ms · audio \(c.pausaMassimaAudio * 1000, format: .number.precision(.fractionLength(0))) ms")
            Text("Coda di invio: picco \(r.piccoByteInAttesa / 1024) KB · attesa max \(r.attesaMassimaMs) ms")
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
        .onChange(of: cercaPC.trovati) { _, trovati in
            // Con un solo PC in rete non c'è niente da scegliere.
            if pcScelto.isEmpty, trovati.count == 1 {
                pcScelto = trovati[0]
            }
        }
        .task {
            cercaPC.avvia()
            while !Task.isCancelled {
                cattura.aggiorna()
                try? await Task.sleep(for: .milliseconds(500))
            }
        }
    }

    /// I PC trovati, più quello scelto in passato se adesso non risponde (così la scelta non sparisce).
    private var opzioniPC: [String] {
        var nomi = cercaPC.trovati
        if !pcScelto.isEmpty, pcScelto != Self.manuale, !nomi.contains(pcScelto) {
            nomi.append(pcScelto)
        }
        return nomi
    }

    private var destinazione: DestinazionePC? {
        if pcScelto == Self.manuale {
            let indirizzo = indirizzoPC.trimmingCharacters(in: .whitespaces)
            return indirizzo.isEmpty ? nil : .indirizzo(indirizzo)
        }
        return pcScelto.isEmpty ? nil : .servizio(pcScelto)
    }

    private func descrizione(_ posizione: SensoreOrientamento.Posizione?) -> String {
        switch posizione {
        case .verticale: "verticale"
        case .orizzontaleSinistra: "orizzontale (lato sinistro in basso)"
        case .orizzontaleDestra: "orizzontale (lato destro in basso)"
        case .piatto: "appoggiato piatto"
        case nil: "sensore —"
        }
    }
}
