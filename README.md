# MirrorLoop

App gratuita, senza pubblicità e senza server, che trasmette lo schermo e l'audio dell'iPhone a un PC Windows sulla rete locale, con integrazione in OBS per le dirette.

> **Stato:** in sviluppo (Fase 4 · Audio). Lo schermo dell'iPhone (iOS 27 o successivo) si vede già sul PC: l'app cattura con ScreenCaptureKit, comprime in H.264 e trasmette al ricevitore Windows sulla rete di casa. L'audio non è ancora trasmesso.

## Come funziona

- **iPhone:** cattura lo schermo con ScreenCaptureKit, lo comprime in H.264 e lo trasmette sulla rete locale.
- **PC Windows:** riceve il flusso, lo decodifica e lo mostra.
- **OBS:** riprende la finestra del ricevitore (in futuro tramite un plugin dedicato).

## Requisiti per sviluppare

- Windows con [SDK di .NET 10](https://dotnet.microsoft.com/download)
- Git

L'app iOS (`ios/`) non richiede un Mac: il progetto Xcode si genera con [XcodeGen](https://github.com/yonaskolb/XcodeGen) e il workflow `.github/workflows/ios.yml` lo compila su macOS con GitHub Actions. Le build del ramo `main` vengono firmate e caricate su TestFlight; per ora l'installazione è riservata ai tester interni.

## Licenza

[MIT](LICENSE)
