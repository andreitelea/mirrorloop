; Installatore di MirrorLoop per Windows (Inno Setup 6, https://jrsoftware.org/isinfo.php).
; Installa il ricevitore (con .NET incluso), il plugin per OBS Studio e la regola del firewall per le
; sole reti private. Lo costruisce il workflow .github/workflows/rilascio.yml:
;   ISCC /DVersione=0.14.0 /DCartellaRicevitore=<ricevitore pubblicato> /DCartellaPlugin=<plugin> MirrorLoop.iss
; Il ricevitore è MIT, il plugin GPL-2.0 (cartella obs/): ognuno porta con sé la sua licenza.

#ifndef Versione
  #error Manca la versione: ISCC /DVersione=x.y.z
#endif
#ifndef CartellaRicevitore
  #error Manca la cartella del ricevitore pubblicato: ISCC /DCartellaRicevitore=...
#endif
#ifndef CartellaPlugin
  #error Manca la cartella del plugin (quella che contiene "mirrorloop"): ISCC /DCartellaPlugin=...
#endif

#define Eseguibile "MirrorLoop.Ricevitore.exe"
; La stessa cartella di C:\ProgramData usata finora: OBS 32 cerca qui i plugin installati a parte.
#define CartellaObs "{commonappdata}\obs-studio\plugins\mirrorloop"

[Setup]
; Identifica MirrorLoop tra i programmi installati: non va mai cambiato, altrimenti un aggiornamento
; diventerebbe un secondo programma.
AppId={{D22F4D56-ADEC-4FB3-AA9F-78276D9C9332}
AppName=MirrorLoop
AppVersion={#Versione}
AppVerName=MirrorLoop {#Versione}
AppPublisher=Andrei Telea
AppPublisherURL=https://github.com/andreitelea/mirrorloop
AppSupportURL=https://github.com/andreitelea/mirrorloop/issues
VersionInfoVersion={#Versione}
DefaultDirName={autopf}\MirrorLoop
DisableProgramGroupPage=yes
; Servono i permessi di amministratore per il firewall e per la cartella dei plugin di OBS.
PrivilegesRequired=admin
; L'avvio automatico si scrive per chi installa (HKCU), anche se l'installatore gira come amministratore.
UsedUserAreasWarning=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Windows 11 (build 22000): il ricevitore usa QUIC, che .NET supporta solo da Windows 11.
MinVersion=10.0.22000
OutputBaseFilename=MirrorLoop-{#Versione}-Windows
SetupIconFile=..\src\MirrorLoop.Ricevitore\MirrorLoop.ico
UninstallDisplayIcon={app}\{#Eseguibile}
UninstallDisplayName=MirrorLoop
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Se il ricevitore o OBS (che tiene aperto il plugin) sono in esecuzione, Windows propone di chiuderli
; prima di sostituire i file e li riapre alla fine.
CloseApplications=yes
RestartApplications=yes

[Languages]
Name: "it"; MessagesFile: "compiler:Languages\Italian.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
it.ComponenteRicevitore=Ricevitore MirrorLoop
en.ComponenteRicevitore=MirrorLoop receiver
it.ComponenteObs=Plugin per OBS Studio (sorgente "MirrorLoop")
en.ComponenteObs=OBS Studio plugin ("MirrorLoop" source)
it.InstallazioneCompleta=Installazione completa
en.InstallazioneCompleta=Full installation
it.SoloRicevitore=Solo il ricevitore
en.SoloRicevitore=Receiver only
it.GruppoAvvio=Avvio:
en.GruppoAvvio=Startup:
it.AvvioAutomatico=Avvia MirrorLoop all'accesso a Windows
en.AvvioAutomatico=Start MirrorLoop when you sign in to Windows
it.ConfiguraFirewall=Configurazione del firewall (solo reti private)...
en.ConfiguraFirewall=Configuring the firewall (private networks only)...

[Types]
Name: "completa"; Description: "{cm:InstallazioneCompleta}"
Name: "ricevitore"; Description: "{cm:SoloRicevitore}"

[Components]
Name: "ricevitore"; Description: "{cm:ComponenteRicevitore}"; Types: completa ricevitore; Flags: fixed
Name: "obs"; Description: "{cm:ComponenteObs}"; Types: completa

[Tasks]
Name: "desktop"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "avvio"; Description: "{cm:AvvioAutomatico}"; GroupDescription: "{cm:GruppoAvvio}"; Flags: unchecked

[Files]
Source: "{#CartellaRicevitore}\*"; DestDir: "{app}"; Components: ricevitore; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Components: ricevitore; Flags: ignoreversion
Source: "{#CartellaPlugin}\mirrorloop\bin\64bit\mirrorloop.dll"; DestDir: "{#CartellaObs}\bin\64bit"; Components: obs; Flags: ignoreversion
Source: "{#CartellaPlugin}\mirrorloop\data\*"; DestDir: "{#CartellaObs}\data"; Components: obs; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\..\obs\LICENSE"; DestDir: "{#CartellaObs}"; DestName: "LICENSE-GPL-2.0.txt"; Components: obs; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\MirrorLoop"; Filename: "{app}\{#Eseguibile}"
Name: "{autodesktop}\MirrorLoop"; Filename: "{app}\{#Eseguibile}"; Tasks: desktop

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "MirrorLoop"; ValueData: """{app}\{#Eseguibile}"""; Tasks: avvio; Flags: uninsdeletevalue

[Run]
; La regola si toglie e si rimette: reinstallando non si accumulano doppioni.
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""MirrorLoop"""; Flags: runhidden; StatusMsg: "{cm:ConfiguraFirewall}"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""MirrorLoop"" dir=in action=allow protocol=UDP localport=7450 program=""{app}\{#Eseguibile}"" profile=private"; Flags: runhidden; StatusMsg: "{cm:ConfiguraFirewall}"
Filename: "{app}\{#Eseguibile}"; Description: "{cm:LaunchProgram,MirrorLoop}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""MirrorLoop"""; Flags: runhidden; RunOnceId: "RegolaFirewall"
