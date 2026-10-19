# MirrorLoop

App gratuita, senza pubblicità e senza server, che trasmette lo schermo e l'audio dell'iPhone a un PC Windows sulla rete locale, con integrazione in OBS per le dirette.

> **Stato:** in sviluppo (Fase 1 · Video da PC a PC). Non ancora utilizzabile.

## Come funziona

- **iPhone:** cattura lo schermo con ReplayKit, lo comprime in H.264 e lo trasmette sulla rete locale.
- **PC Windows:** riceve il flusso, lo decodifica e lo mostra.
- **OBS:** riprende la finestra del ricevitore (in futuro tramite un plugin dedicato).

## Requisiti per sviluppare

- Windows con [SDK di .NET 10](https://dotnet.microsoft.com/download)
- Git

## Licenza

[MIT](LICENSE)
