# Note per la revisione di Apple (bozza)

Testo per il campo "Notes" di App Review Information in App Store Connect, in inglese.

---

MirrorLoop mirrors the iPhone screen and audio to the user's own Windows PC on the same local network. It is made for live streamers who show their phone in OBS Studio. There is no account, no login, no server and no data collection.

COMPANION APP
MirrorLoop needs its free, open source receiver for Windows 11, made by the same developer:
- Website: https://andreitelea.github.io/mirrorloop/
- Download: https://github.com/andreitelea/mirrorloop/releases
A demo video of the full flow (iPhone, PC and OBS Studio) is attached.

HOW TO TEST
1. On a Windows 11 PC connected to the same network as the iPhone (network profile set to Private), install and open MirrorLoop for Windows.
2. On the iPhone, open MirrorLoop, choose the PC under "PC" and tap "Start streaming".
3. Confirm screen sharing in the system picker.
4. The first time, the same 6-digit code appears on both devices: tap "The code matches" on the iPhone and click "Allow" on the PC.
5. The iPhone screen and audio appear on the PC. Switch to another app: streaming continues in the background.
6. Tap "Stop streaming" in the app to end the session.

BACKGROUND MODE "screen-capture"
MirrorLoop uses ScreenCaptureKit (SCContentSharingPicker and SCStream) to capture the screen, as described in Apple's documentation "Capturing screen content on iOS". Capture starts only after the user chooses it in the system picker, and iOS shows its recording indicator while it is active. The background mode is essential to the app's purpose: streamers show other apps (games, social apps) on the PC while they use them. Nothing is recorded or stored on the iPhone: frames are compressed with VideoToolbox and sent only to the PC the user paired with, over an encrypted QUIC connection.

OTHER PERMISSIONS
- Local Network: to find the PC with Bonjour (_mirrorloop._udp) and connect to it (QUIC, UDP port 7450).
- Motion sensor (Core Motion): used on the iPhone only, to know how the phone is held; only the image rotation is sent to the PC.

ENCRYPTION
Only encryption provided by iOS: TLS 1.3 through Network.framework (QUIC) and CryptoKit (Secure Enclave P-256 signatures for pairing).
