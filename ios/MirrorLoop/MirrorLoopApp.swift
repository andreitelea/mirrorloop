import SwiftUI

// I testi dell'interfaccia sono scritti in inglese (la lingua per chi non ha l'iPhone in italiano);
// le traduzioni italiane stanno in Localizable.xcstrings. Text("…") con un testo fisso lo traduce
// da solo; i testi preparati altrove (stati, errori) si traducono dove nascono, con String(localized:).

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
                    Text(verbatim: "MirrorLoop")
                        .font(.largeTitle)
                        .bold()
                    Text("Version \(versione)")
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
    @AppStorage("qualita") private var qualita = Qualita.alta
    // I numeri della trasmissione servono per capire un problema, non per usare l'app: chiusi finché
    // non li si apre, e la scelta si ricorda.
    @AppStorage("dettagliTecnici") private var dettagliAperti = false
    @State private var consigliAperti = false
    @State private var chiediDimentica = false

    var body: some View {
        let r = cattura.rete
        VStack(alignment: .leading, spacing: 6) {
            Text("Stream to PC")
                .font(.headline)
            Picker("PC", selection: $pcScelto) {
                if pcScelto.isEmpty {
                    Text("Choose the PC").tag("")
                }
                ForEach(opzioniPC, id: \.self) { nome in
                    (cercaPC.trovati.contains(nome) ? Text(verbatim: nome) : Text("\(nome) · not found")).tag(nome)
                }
                Text("Enter address…").tag(Self.manuale)
            }
            .pickerStyle(.menu)
            Text(cercaPC.trovati.isEmpty ? cercaPC.stato : String(localized: "PCs found on the network: \(cercaPC.trovati.count)"))
                .font(.caption)
                .foregroundStyle(.secondary)
            if pcScelto == Self.manuale {
                TextField("PC address (e.g. 192.168.1.23)", text: $indirizzoPC)
                    .textFieldStyle(.roundedBorder)
                    .keyboardType(.numbersAndPunctuation)
                    .textInputAutocapitalization(.never)
                    .autocorrectionDisabled()
            }
            Picker("Quality", selection: $qualita) {
                ForEach(Qualita.allCases, id: \.self) { Text($0.nome) }
            }
            .pickerStyle(.segmented)
            let dimensioni = Cattura.dimensioniVideo(qualita: qualita)
            Text(verbatim: "\(dimensioni.0)×\(dimensioni.1) · \(qualita.fps) fps · \(qualita.bitrate / 1_000_000) Mbit/s")
                .font(.caption)
                .foregroundStyle(.secondary)
            if qualita == .massima {
                Text("Needs strong Wi-Fi: far from the router, choose High.")
                    .font(.caption)
                    .foregroundStyle(.orange)
            }
            if !cattura.disponibile {
                Text("Screen recording isn't available on this iPhone.")
                    .foregroundStyle(.red)
            }
            HStack {
                Button("Start") {
                    if let destinazione {
                        cattura.avvia(destinazione: destinazione, qualita: qualita)
                    }
                }
                    .buttonStyle(.borderedProminent)
                    .disabled(destinazione == nil)
                Button("Stop") { cattura.ferma() }
                    .buttonStyle(.bordered)
            }
            if let abbinamento = r.abbinamento {
                RiquadroAbbinamento(abbinamento: abbinamento,
                                    conferma: { cattura.confermaCodice() },
                                    annulla: { cattura.annullaAbbinamento() })
            }
            Text("Capture: \(cattura.stato)")
            Text("Network: \(r.stato)")
            if let errore = cattura.errore {
                Text("Error: \(errore)")
                    .foregroundStyle(.red)
            }

            DisclosureGroup("Tips for smooth streaming", isExpanded: $consigliAperti) {
                VStack(alignment: .leading, spacing: 8) {
                    Text("Use 5 GHz Wi-Fi and keep the iPhone close to the router. The PC works best connected with a cable.")
                    Text("Maximum quality needs a strong signal: far from the router, choose High.")
                    Text("Keep the iPhone volume low but not at zero: at zero the PC receives no sound.")
                    Text("Turn on a Focus (such as Do Not Disturb) so notifications don't end up on stream.")
                }
                .padding(.top, 6)
            }
            .padding(.top, 12)

            DisclosureGroup("Technical details", isExpanded: $dettagliAperti) {
                DettagliTecnici(cattura: cattura)
                    .padding(.top, 6)
            }

            let abbinati = PCAbbinati.numero
            Button("Forget paired PCs (\(abbinati))", role: .destructive) { chiediDimentica = true }
                .disabled(abbinati == 0)
                .padding(.top, 12)
                .confirmationDialog("Forget paired PCs?", isPresented: $chiediDimentica, titleVisibility: .visible) {
                    Button("Forget", role: .destructive) { PCAbbinati.dimenticaTutti() }
                } message: {
                    Text("The code will be needed again at the next connection, on the PC and on the iPhone.")
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
}

/// I numeri della trasmissione, per capire un problema (come le statistiche del ricevitore, tasto S).
struct DettagliTecnici: View {
    let cattura: Cattura

    var body: some View {
        let c = cattura.contatori
        let s = cattura.compressione
        let r = cattura.rete
        VStack(alignment: .leading, spacing: 6) {
            Text("Sent: \(r.inviati) · dropped \(r.scartati) · connections \(r.connessioni) · pings \(r.pingRisposti)")
            if let erroreRete = r.ultimoErrore {
                Text("Last network error: \(erroreRete)")
                    .foregroundStyle(.orange)
            }
            Text("Captured: \(c.fotogrammi) (in background \(c.fotogrammiInBackground) in \(c.secondiInBackground, format: .number.precision(.fractionLength(1))) s)")
            Text("Dropped for frame rate: \(c.scartatiPerRitmo)")
            Text("Quality: \(cattura.qualita.nome) · \(cattura.qualita.fps) fps · \(cattura.qualita.bitrate / 1_000_000) Mbit/s")
            Text("Size: \(c.larghezza)×\(c.altezza) (requested \(cattura.dimensioneRichiesta))")
            Text("Orientation: \(descrizione(cattura.sensore.posizione)) · rotation \(Int(c.rotazione) * 90)° · changes \(cattura.sensore.cambi)\(c.orientamento.map { " · ScreenCaptureKit \($0)" } ?? "")")
            Text("Compressed: \(s.frameCompressi) · keyframes \(s.keyframe)")
            Text("Bitrate: \(cattura.kbitAlSecondo) kbit/s (peak \(cattura.kbitPicco))")
            Text("Compression: \(s.msUltimaCompressione, format: .number.precision(.fractionLength(1))) ms (max \(s.msCompressioneMassima, format: .number.precision(.fractionLength(1))) ms)")
            Text("Encoder errors: \(s.errori) (last \(s.ultimoErrore)) · sessions \(s.sessioniCreate) · rejected settings \(s.impostazioniRifiutate)")
            Text("Audio: \(c.pacchettiAudio) captured · sent \(r.audioInviati) · dropped \(r.audioScartati) · not converted \(c.audioNonConvertiti)")
            Text("Audio format: \(c.formatoAudio)")
            Text("Capture gaps: video \(c.pausaMassima * 1000, format: .number.precision(.fractionLength(0))) ms · audio \(c.pausaMassimaAudio * 1000, format: .number.precision(.fractionLength(0))) ms")
            Text("Send queue: peak \(r.piccoByteInAttesa / 1024) KB · max wait \(r.attesaMassimaMs) ms")
            Text("Delivery delay: video \(c.ritardoConsegnaVideoMs, format: .number.precision(.fractionLength(1))) ms · audio \(c.ritardoConsegnaAudioMs, format: .number.precision(.fractionLength(1))) ms")
        }
    }

    private func descrizione(_ posizione: SensoreOrientamento.Posizione?) -> String {
        switch posizione {
        case .verticale: String(localized: "portrait")
        case .orizzontaleSinistra: String(localized: "landscape (left side down)")
        case .orizzontaleDestra: String(localized: "landscape (right side down)")
        case .piatto: String(localized: "lying flat")
        case nil: String(localized: "sensor —")
        }
    }
}

/// Il codice dell'abbinamento, da confrontare con quello mostrato dal ricevitore sul PC.
struct RiquadroAbbinamento: View {
    let abbinamento: AbbinamentoInCorso
    let conferma: () -> Void
    let annulla: () -> Void

    var body: some View {
        VStack(spacing: 10) {
            Text("Pairing with \(abbinamento.pc)")
                .font(.headline)
            if abbinamento.improntaCambiata {
                Text("This isn't the PC you paired before: pair only if you reinstalled MirrorLoop on the PC.")
                    .foregroundStyle(.orange)
                    .multilineTextAlignment(.center)
            }
            Text(verbatim: Abbinamento.leggibile(abbinamento.codice))
                .font(.system(size: 44, weight: .semibold, design: .monospaced))
            if abbinamento.serveConferma && !abbinamento.confermato {
                Text("Confirm only if the PC shows the same code.")
                    .multilineTextAlignment(.center)
                HStack {
                    Button("The code matches", action: conferma)
                        .buttonStyle(.borderedProminent)
                    Button("Cancel", role: .cancel, action: annulla)
                        .buttonStyle(.bordered)
                }
            } else {
                (abbinamento.serveConferma ? Text("Now allow the connection on the PC.") : Text("Allow the connection on the PC."))
                    .multilineTextAlignment(.center)
                Button("Cancel", role: .cancel, action: annulla)
                    .buttonStyle(.bordered)
            }
        }
        .font(.body)
        .padding()
        .frame(maxWidth: .infinity)
        .background(.thinMaterial, in: RoundedRectangle(cornerRadius: 12))
    }
}
