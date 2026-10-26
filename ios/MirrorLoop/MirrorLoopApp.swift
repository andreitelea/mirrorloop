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

/// Una sola schermata a sezioni, come Impostazioni di iOS: modalità scura, testo ingrandito e
/// accessibilità li gestisce il sistema.
struct SchermataPrincipale: View {
    @State private var cattura = Cattura()
    @State private var cercaPC = CercaPC()
    @Environment(\.scenePhase) private var fase
    // Il PC scelto e l'indirizzo scritto a mano si ricordano tra un avvio e l'altro.
    // pcScelto: il nome di un PC trovato con Bonjour, `manuale`, oppure vuoto se non ancora scelto.
    @AppStorage("pcScelto") private var pcScelto = ""
    @AppStorage("indirizzoPC") private var indirizzoPC = ""
    private static let manuale = "manuale"
    @AppStorage("qualita") private var qualita = Qualita.alta
    // I numeri completi servono per capire un problema, non per usare l'app: compaiono solo in
    // modalità diagnostica, che si accende e si spegne con 5 tocchi sul numero di versione.
    @AppStorage("diagnostica") private var diagnostica = false
    @State private var chiediDimentica = false

    var body: some View {
        Form {
            Section {
                RiquadroStato(cattura: cattura, pc: destinazione?.descrizione,
                              avvia: avvia, ferma: { cattura.ferma() })
            } header: {
                // Nell'intestazione della sezione e non in una riga dell'elenco: le righe hanno gli
                // angoli arrotondati e ritagliavano l'icona.
                HStack(spacing: 14) {
                    IconaMirrorLoop()
                        .frame(width: 52, height: 52)
                    VStack(alignment: .leading, spacing: 2) {
                        Text(verbatim: "MirrorLoop")
                            .font(.title2.bold())
                            .foregroundStyle(.primary)
                        Text("Version \(VersioneApp.semplice)")
                            .font(.footnote)
                            .foregroundStyle(.secondary)
                    }
                }
                .textCase(nil)
                .frame(maxWidth: .infinity, alignment: .leading)
                .contentShape(Rectangle())
                .onTapGesture(count: 5) { diagnostica.toggle() }
                .padding(.bottom, 12)
            }

            Section {
                Picker(selection: $pcScelto) {
                    if pcScelto.isEmpty {
                        Text("Choose the PC").tag("")
                    }
                    ForEach(opzioniPC, id: \.self) { nome in
                        (cercaPC.trovati.contains(nome) ? Text(verbatim: nome) : Text("\(nome) · not found")).tag(nome)
                    }
                    Text("Enter address…").tag(Self.manuale)
                } label: {
                    Label("PC", systemImage: "desktopcomputer")
                }
                .pickerStyle(.menu)
                if pcScelto == Self.manuale {
                    TextField("PC address (e.g. 192.168.1.23)", text: $indirizzoPC)
                        .keyboardType(.numbersAndPunctuation)
                        .textInputAutocapitalization(.never)
                        .autocorrectionDisabled()
                }
            } header: {
                Text("PC")
            } footer: {
                Text(cercaPC.trovati.isEmpty ? cercaPC.stato : String(localized: "PCs found on the network: \(cercaPC.trovati.count)"))
            }

            Section {
                Picker("Quality", selection: $qualita) {
                    ForEach(Qualita.allCases, id: \.self) { Text($0.nome) }
                }
                .pickerStyle(.segmented)
                .listRowInsets(EdgeInsets(top: 10, leading: 12, bottom: 10, trailing: 12))
            } header: {
                Text("Quality")
            } footer: {
                Text(qualita.descrizione)
            }

            Section {
                DisclosureGroup {
                    Label("Use 5 GHz Wi-Fi and keep the iPhone close to the router. The PC works best connected with a cable.", systemImage: "wifi")
                    Label("Maximum quality needs a strong signal: far from the router, choose Full HD.", systemImage: "sparkles")
                    Label("Keep the iPhone volume low but not at zero: at zero the PC receives no sound.", systemImage: "speaker.wave.1")
                    Label("Turn on a Focus (such as Do Not Disturb) so notifications don't end up on stream.", systemImage: "moon")
                } label: {
                    Label("Tips for smooth streaming", systemImage: "lightbulb")
                }
                DisclosureGroup {
                    InformazioniTrasmissione(cattura: cattura, pc: destinazione?.descrizione)
                } label: {
                    Label("Transmission info", systemImage: "info.circle")
                }
            }

            if diagnostica {
                Section {
                    DettagliTecnici(cattura: cattura)
                } header: {
                    Text("Technical details")
                } footer: {
                    Text("Diagnostic mode: tap the version 5 times to hide it.")
                }
            }

            Section {
                Link(destination: Sito.guida) { Label("Guide", systemImage: "book") }
                Link(destination: Sito.supporto) { Label("Support", systemImage: "questionmark.circle") }
                Link(destination: Sito.privacy) { Label("Privacy policy", systemImage: "hand.raised") }
            } header: {
                Text("About")
            }

            Section {
                let abbinati = PCAbbinati.numero
                Button("Forget paired PCs (\(abbinati))", role: .destructive) { chiediDimentica = true }
                    // Anche senza PC nell'elenco può restare la chiave (dopo una reinstallazione).
                    .disabled(abbinati == 0 && !ChiaveIphone.esiste)
                    .confirmationDialog("Forget paired PCs?", isPresented: $chiediDimentica, titleVisibility: .visible) {
                        Button("Forget", role: .destructive) {
                            PCAbbinati.dimenticaTutti()
                            ChiaveIphone.cancella()
                        }
                    } message: {
                        Text("The iPhone's pairing key is deleted too. The code will be needed again at the next connection, on the PC and on the iPhone.")
                    }
            }
        }
        // Il codice dell'abbinamento sale sopra la schermata: si chiude solo con i suoi pulsanti.
        .sheet(isPresented: abbinamentoInCorso) {
            if let abbinamento = cattura.rete.abbinamento {
                RiquadroAbbinamento(abbinamento: abbinamento,
                                    conferma: { cattura.confermaCodice() },
                                    annulla: { cattura.annullaAbbinamento() })
                    .presentationDetents([.medium])
                    .interactiveDismissDisabled()
            }
        }
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

    private var abbinamentoInCorso: Binding<Bool> {
        Binding(get: { cattura.rete.abbinamento != nil }, set: { _ in })
    }

    private func avvia() {
        if let destinazione {
            cattura.avvia(destinazione: destinazione, qualita: qualita)
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

/// Le pagine del sito, nella lingua in cui è mostrata l'app.
enum Sito {
    private static let base = "https://andreitelea.github.io/mirrorloop/"
    private static let italiano = Bundle.main.preferredLocalizations.first == "it"
    static let guida = URL(string: base + (italiano ? "it/guida.html" : "guide.html"))!
    static let supporto = URL(string: base + (italiano ? "it/supporto.html" : "support.html"))!
    static let privacy = URL(string: base + (italiano ? "it/privacy.html" : "privacy.html"))!
}

/// Il numero di versione, letto dall'Info.plist (coincide sempre con il pacchetto installato).
enum VersioneApp {
    private static let marketing = Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "?"
    private static let build = Bundle.main.infoDictionary?["CFBundleVersion"] as? String ?? "?"

    /// Per tutti: senza gli zeri finali (0.14.0 → 0.14, 1.0.0 → 1) e senza il numero di build.
    static let semplice: String = {
        var parti = marketing.split(separator: ".").map(String.init)
        while parti.count > 1, parti.last == "0" {
            parti.removeLast()
        }
        return parti.joined(separator: ".")
    }()

    /// Per la diagnostica: tutto, con il numero di build di TestFlight.
    static let completa = "\(marketing) (\(build))"
}

/// Lo stato in cima: l'anello dell'icona (viola mentre trasmette), una riga con il pallino colorato e
/// il pulsante grande Avvia/Ferma.
struct RiquadroStato: View {
    let cattura: Cattura
    let pc: String?
    let avvia: () -> Void
    let ferma: () -> Void

    var body: some View {
        let r = cattura.rete
        VStack(spacing: 12) {
            FormaAnello(conPunto: cattura.inCorso)
                .fill(cattura.inCorso ? AnyShapeStyle(Self.viola) : AnyShapeStyle(Color.secondary.opacity(0.35)))
                .frame(width: 78, height: 78)
                .accessibilityHidden(true)
            HStack(spacing: 7) {
                Circle()
                    .fill(colorePallino)
                    .frame(width: 9, height: 9)
                Text(titolo)
                    .font(.headline)
            }
            if cattura.inCorso, r.collegato {
                Text(cattura.qualita.nome)
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
            } else if !cattura.inCorso, pc == nil {
                Text("Choose the PC, then start.")
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
            }
            if let errore = cattura.errore {
                Text("Error: \(errore)")
                    .font(.footnote)
                    .foregroundStyle(.red)
                    .multilineTextAlignment(.center)
            }
            if !cattura.disponibile {
                Text("Screen recording isn't available on this iPhone.")
                    .font(.footnote)
                    .foregroundStyle(.red)
                    .multilineTextAlignment(.center)
            }
            if cattura.inCorso {
                Button(role: .destructive, action: ferma) {
                    Text("Stop streaming").frame(maxWidth: .infinity)
                }
                .buttonStyle(.bordered)
                .controlSize(.large)
            } else {
                Button(action: avvia) {
                    Text("Start streaming").frame(maxWidth: .infinity)
                }
                .buttonStyle(.borderedProminent)
                .controlSize(.large)
                .disabled(pc == nil || !cattura.disponibile)
            }
        }
        .frame(maxWidth: .infinity)
        .padding(.vertical, 8)
    }

    private var titolo: String {
        guard cattura.inCorso else { return String(localized: "Ready") }
        if cattura.rete.collegato, let pc { return String(localized: "Connected to \(pc)") }
        // Prima la scelta di cosa condividere (cattura), poi il collegamento al PC (rete).
        return cattura.catturaAttiva ? cattura.rete.stato : cattura.stato
    }

    private var colorePallino: Color {
        if cattura.errore != nil { return .red }
        guard cattura.inCorso else { return .gray }
        return cattura.rete.collegato ? .green : .orange
    }

    /// I colori dell'anello nella variante scura dell'icona (grafica/icona.py).
    static let viola = LinearGradient(colors: [Color(red: 0x6D / 255, green: 0x8C / 255, blue: 0xFF / 255),
                                               Color(red: 0xB4 / 255, green: 0x8C / 255, blue: 0xFF / 255)],
                                      startPoint: .topLeading, endPoint: .bottomTrailing)
}

/// L'anello con il punto dell'icona, con la geometria di grafica/icona.py (icona da 1024): anello di
/// raggio 300 e spessore 72, punto di raggio 92 a -45°, staccato dall'anello da un bordo di 26.
struct FormaAnello: Shape {
    var conPunto = true

    func path(in rect: CGRect) -> Path {
        let lato = min(rect.width, rect.height)
        let s = lato / 1024
        let origine = CGPoint(x: rect.midX - lato / 2, y: rect.midY - lato / 2)
        func cerchio(_ cx: Double, _ cy: Double, _ raggio: Double) -> Path {
            Path(ellipseIn: CGRect(x: origine.x + (cx - raggio) * s, y: origine.y + (cy - raggio) * s,
                                   width: 2 * raggio * s, height: 2 * raggio * s))
        }
        let anello = cerchio(512, 512, 336).subtracting(cerchio(512, 512, 264))
        guard conPunto else { return anello }
        let px = 512 + 300 * cos(-Double.pi / 4)
        let py = 512 + 300 * sin(-Double.pi / 4)
        return anello.subtracting(cerchio(px, py, 118)).union(cerchio(px, py, 92))
    }
}

/// L'icona dell'app disegnata (l'icona vera non si può usare come immagine nell'app).
struct IconaMirrorLoop: View {
    var body: some View {
        GeometryReader { geometria in
            RoundedRectangle(cornerRadius: geometria.size.width * 0.2237, style: .continuous)
                .fill(LinearGradient(colors: [Color(red: 0x1E / 255, green: 0x3A / 255, blue: 0x8A / 255),
                                              Color(red: 0x7C / 255, green: 0x3A / 255, blue: 0xED / 255)],
                                     startPoint: .topLeading, endPoint: .bottomTrailing))
                .overlay(FormaAnello().fill(.white))
        }
        .accessibilityHidden(true)
    }
}

/// Poche righe in parole semplici (come il tasto S del ricevitore).
struct InformazioniTrasmissione: View {
    let cattura: Cattura
    let pc: String?

    var body: some View {
        let r = cattura.rete
        let collegato = cattura.inCorso && r.collegato
        LabeledContent("Connected to", value: collegato ? (pc ?? "—") : String(localized: "no PC"))
        LabeledContent("Quality", value: cattura.inCorso ? cattura.qualita.nome : "—")
        LabeledContent("Streaming time") {
            if collegato, let inizio = cattura.collegatoDa {
                Text(inizio, style: .timer)
            } else {
                Text(verbatim: "—")
            }
        }
        LabeledContent("Reconnections", value: cattura.inCorso ? "\(max(0, r.connessioni - 1))" : "—")
    }
}

/// I numeri completi della trasmissione, per capire un problema (come Ctrl+S nel ricevitore).
struct DettagliTecnici: View {
    let cattura: Cattura

    var body: some View {
        let c = cattura.contatori
        let s = cattura.compressione
        let r = cattura.rete
        VStack(alignment: .leading, spacing: 6) {
            Text("Version: \(VersioneApp.completa)")
            Text("Capture: \(cattura.stato)")
            Text("Network: \(r.stato)")
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
        .font(.caption.monospacedDigit())
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
        VStack(spacing: 14) {
            Text("Pairing with \(abbinamento.pc)")
                .font(.title3.bold())
                .multilineTextAlignment(.center)
            if abbinamento.improntaCambiata {
                Text("This isn't the PC you paired before: pair only if you reinstalled MirrorLoop on the PC.")
                    .foregroundStyle(.orange)
                    .multilineTextAlignment(.center)
            }
            Text(verbatim: Abbinamento.leggibile(abbinamento.codice))
                .font(.system(size: 48, weight: .semibold, design: .monospaced))
            if abbinamento.serveConferma && !abbinamento.confermato {
                Text("Confirm only if the PC shows the same code.")
                    .foregroundStyle(.secondary)
                    .multilineTextAlignment(.center)
                Button(action: conferma) {
                    Text("The code matches").frame(maxWidth: .infinity)
                }
                .buttonStyle(.borderedProminent)
                .controlSize(.large)
            } else {
                (abbinamento.serveConferma ? Text("Now allow the connection on the PC.") : Text("Allow the connection on the PC."))
                    .foregroundStyle(.secondary)
                    .multilineTextAlignment(.center)
            }
            Button("Cancel", role: .cancel, action: annulla)
        }
        .padding(24)
    }
}
