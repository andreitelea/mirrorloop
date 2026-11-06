# Note per la certificazione del Microsoft Store (bozza)

Testo per il campo "Notes for certification" dell'invio in Partner Center, in inglese. Regola 10.3 delle Store Policies: il prodotto deve essere "testable", e chi lo prova ha bisogno di un iPhone: per questo il video dimostrativo. Il video è quello mandato ad Apple, pubblicato nel sito (`sito/demo.mp4`, senza metadati: controllati il 06/11/2026), così nessuna terza parte vede chi lo apre.

---

MirrorLoop for Windows is the receiver for the free MirrorLoop iPhone app (App Store: https://apps.apple.com/app/id6825028097), made by the same developer. It shows the iPhone screen and audio on the PC and passes them to OBS Studio through an OBS plugin. There is no account, no login, no server and no data collection. Demo video of pairing and streaming (iPhone and PC): https://andreitelea.github.io/mirrorloop/demo.mp4

HOW TO TEST
Without an iPhone, the app opens, shows its waiting screen with the PC name and address, and its help with F1.
With an iPhone (iOS 27 or later) on the same network, with the network profile set to Private in Windows:
1. Open MirrorLoop on the PC.
2. On the iPhone, open MirrorLoop, choose the PC and tap "Start streaming", then confirm screen sharing.
3. The first time, the same 6-digit code appears on both devices: confirm it on the iPhone and click "Allow" on the PC.
4. The iPhone screen and audio appear in the PC window.

FIREWALL RULE
The package declares one inbound firewall rule (desktop2:FirewallRules): UDP port 7450, private networks only, for the receiver. The iPhone connects to it over QUIC (TLS 1.3); the receiver also announces itself on the local network with Bonjour/mDNS so the iPhone can find it.

OBS PLUGIN
The package contains our OBS Studio plugin (open source, GPL-2.0) in obs-plugin\mirrorloop. If OBS Studio is installed, the app asks the user before copying it to C:\ProgramData\obs-studio\plugins\mirrorloop, the standard folder for OBS plugins; no administrator rights are needed. The user can install or update it with the P key and remove it with Shift+P (the help, F1, explains both), also before uninstalling the app. Nothing is downloaded: the plugin is the one inside the package.

STARTUP
A startup task is declared but disabled; the user can turn it on in Settings > Apps > Startup.

WACK
The "Blocked executables" warnings of the Windows App Certification Kit come from the .NET runtime libraries included in the package; the app never starts other processes.

Source code: https://github.com/andreitelea/mirrorloop
