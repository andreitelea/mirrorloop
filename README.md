# MirrorLoop

App gratuita, senza pubblicità e senza server, che trasmette lo schermo e l'audio dell'iPhone a un PC Windows sulla rete locale, con integrazione in OBS per le dirette.

> **Stato:** in sviluppo (Fase 6 · OBS). Schermo e audio dell'iPhone (iOS 27 o successivo) arrivano già sul PC, sincronizzati: l'app cattura con ScreenCaptureKit, comprime il video in H.264 e trasmette video e audio al ricevitore Windows sulla rete di casa.

## Come funziona

- **iPhone:** cattura lo schermo con ScreenCaptureKit, lo comprime in H.264 e lo trasmette sulla rete locale.
- **PC Windows:** riceve il flusso, lo decodifica e lo mostra.
- **OBS:** riprende la finestra del ricevitore con "Cattura finestra"; è in preparazione una sorgente dedicata (`obs/`).

## Requisiti per sviluppare

- Windows con [SDK di .NET 10](https://dotnet.microsoft.com/download)
- Git
- Per il plugin OBS (`obs/`): Visual Studio 2022 (anche solo Build Tools) con il carico di lavoro C++ e CMake 3.30 o successivo; si compila con `cmake --preset windows-x64` e `cmake --build --preset windows-x64` dentro `obs/`

L'app iOS (`ios/`) non richiede un Mac: il progetto Xcode si genera con [XcodeGen](https://github.com/yonaskolb/XcodeGen) e il workflow `.github/workflows/ios.yml` lo compila su macOS con GitHub Actions. Le build del ramo `main` vengono firmate e caricate su TestFlight; per ora l'installazione è riservata ai tester interni.

## Licenza

[MIT](LICENSE), tranne la cartella `obs/` (plugin per OBS Studio), che è [GPL-2.0](obs/LICENSE) come OBS stesso.
