<p align="center"><img src="sito/icona.svg" width="96" height="96" alt=""></p>

<h1 align="center">MirrorLoop</h1>

<p align="center">Your iPhone screen on your PC, ready for OBS.</p>

<p align="center">
  <a href="https://andreitelea.github.io/mirrorloop/">Website</a> ·
  <a href="https://andreitelea.github.io/mirrorloop/guide.html">Guide</a> ·
  <a href="https://github.com/andreitelea/mirrorloop/releases">Download</a> ·
  <a href="https://andreitelea.github.io/mirrorloop/support.html">Support</a> ·
  <a href="https://andreitelea.github.io/mirrorloop/privacy.html">Privacy</a>
</p>

<p align="center">English · <a href="README.it.md">Italiano</a></p>

MirrorLoop streams the screen and audio of your iPhone to a Windows PC on your home network, with a source for OBS Studio for live streaming. Free, no ads, no servers, no accounts.

> **Status:** preview 0.14.0. The Windows app and the OBS plugin can be downloaded from the [Releases](https://github.com/andreitelea/mirrorloop/releases) page. **The iPhone app is not available to the public yet:** it is coming soon to the App Store.

<p align="center"><img src="sito/illustrazione.svg" width="560" alt="The iPhone streams to the PC, which shows the image and sends it to OBS"></p>

## What it does

- Streams the iPhone **screen and audio** to the PC, in sync, even while the app is in the background.
- Three quality levels: **HD** (720p, 30 fps), **Full HD** (1088p, 60 fps, the default) and **Maximum** (the exact resolution of the iPhone screen, 60 fps).
- **Pairing** the first time: the same 6-digit code appears on the iPhone and on the PC and is confirmed on both. After that, the iPhone reconnects by itself.
- **Encrypted** connection (QUIC), only on your home network.
- **"MirrorLoop" source for OBS Studio**, with video and audio: no window capture needed.

## What you need

- An iPhone with **iOS 27** or later.
- A PC with **Windows 11** (64-bit).
- iPhone and PC on the **same home network**, set as a **Private** network in Windows (the receiver warns you if it is Public).
- For live streaming: **OBS Studio 31.1** or later.
- Tip: use 5 GHz Wi-Fi with the iPhone close to the router, and connect the PC with a cable. Maximum quality needs a strong signal.

## Getting started

1. Download `MirrorLoop-<version>-Windows.exe` from the latest [release](https://github.com/andreitelea/mirrorloop/releases) and open it. The installer is not digitally signed yet, so Windows may show **"Windows protected your PC"**: choose *More info* → *Run anyway*. Install the **receiver** and, if you want, the **OBS plugin**.
2. On the PC, open **MirrorLoop** from the Start menu. Press **F1** to see the keys.
3. On the iPhone, open the app, choose the PC in the list and tap **Start streaming**.
4. The same 6-digit code appears on the iPhone and on the PC: if it matches, tap **The code matches** on the iPhone and click **Allow** on the PC.
5. In OBS, add a **MirrorLoop** source to your scene.

The installer also adds the firewall rule that opens UDP port 7450 **on private networks only**. You can uninstall it from *Settings → Apps*. If you only want the OBS plugin, download `MirrorLoop-<version>-plugin-OBS.zip` and extract the `mirrorloop` folder into `C:\ProgramData\obs-studio\plugins\`.

All the details, OBS audio monitoring included, are in the [step-by-step guide](https://andreitelea.github.io/mirrorloop/guide.html). Something doesn't work? See the [common problems](https://andreitelea.github.io/mirrorloop/support.html#problems) or [open an issue](https://github.com/andreitelea/mirrorloop/issues/new/choose).

## Privacy

MirrorLoop has no servers and collects no data: images and audio go straight from the iPhone to the PC, inside your home network, and never go through the Internet. Details in the [privacy policy](https://andreitelea.github.io/mirrorloop/privacy.html).

## How it works

- **iPhone** (`ios/`): captures the screen with ScreenCaptureKit, compresses the video to H.264 with VideoToolbox and sends video and audio over QUIC (Network.framework). It finds the PC by itself with Bonjour.
- **Windows PC** (`windows/`): the receiver, in C# with .NET 10, decodes the video with Media Foundation, plays the audio and shows the image; it writes images and audio into shared memory for OBS.
- **OBS** (`obs/`): the plugin reads the shared memory and offers it as a source.

Technical documents (in Italian): [network protocol](docs/protocollo.md) (pairing included) and [shared memory for OBS](docs/memoria-condivisa.md).

## Development

- Windows with the [.NET 10 SDK](https://dotnet.microsoft.com/download) and Git. Receiver and tests: `dotnet build windows/MirrorLoop.slnx -c Release` and `dotnet test windows/MirrorLoop.slnx -c Release`.
- For the OBS plugin (`obs/`): Visual Studio 2022 (Build Tools are enough) with the C++ workload and CMake 3.30 or later; build with `cmake --preset windows-x64` and `cmake --build --preset windows-x64` inside `obs/`.
- The iOS app (`ios/`) doesn't need a Mac: the Xcode project is generated with [XcodeGen](https://github.com/yonaskolb/XcodeGen) and the `.github/workflows/ios.yml` workflow builds it on macOS with GitHub Actions. Builds from the `main` branch are signed and uploaded to TestFlight.
- **Windows release:** there is a single version number for app, receiver and plugin (`ios/project.yml`, `windows/src/MirrorLoop.Ricevitore/MirrorLoop.Ricevitore.csproj`, `obs/buildspec.json`). Pushing a `v<version>` tag makes the `.github/workflows/rilascio.yml` workflow build the installer ([Inno Setup](https://jrsoftware.org/isinfo.php), `windows/installatore/`) and the plugin zip, and prepare a draft release.

The code, comments and technical documents are in Italian.

## License

[MIT](LICENSE), except the `obs/` folder (OBS Studio plugin), which is [GPL-2.0](obs/LICENSE) like OBS itself.
