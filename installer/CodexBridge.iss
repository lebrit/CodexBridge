#ifndef AppVersion
  #error AppVersion must be passed by Build-Installer.ps1
#endif
#ifndef SourceDir
  #error SourceDir must be passed by Build-Installer.ps1
#endif
#ifndef OutputDir
  #error OutputDir must be passed by Build-Installer.ps1
#endif

[Setup]
AppId={{9F573740-5355-4FB5-996B-44A79C6A334C}
AppName=CodexBridge
AppVersion={#AppVersion}
AppPublisher=CodexBridge Contributors
AppPublisherURL=https://github.com/lebrit/CodexBridge
AppSupportURL=https://github.com/lebrit/CodexBridge/issues
AppUpdatesURL=https://github.com/lebrit/CodexBridge/releases
AppReadmeFile={app}\README.md
DefaultDirName={localappdata}\Programs\CodexBridge
DefaultGroupName=CodexBridge
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
AllowNetworkDrive=no
AllowUNCPath=no
UsePreviousAppDir=yes
Uninstallable=yes
UninstallDisplayIcon={app}\CodexBridge.App.exe
CloseApplications=yes
RestartApplications=yes
SetupLogging=yes
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
OutputDir={#OutputDir}
OutputBaseFilename=CodexBridge-{#AppVersion}-setup

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs restartreplace

[Icons]
Name: "{autoprograms}\CodexBridge"; Filename: "{app}\CodexBridge.App.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\CodexBridge"; Filename: "{app}\CodexBridge.App.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\CodexBridge.App.exe"; Description: "{cm:LaunchProgram,CodexBridge}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""CodexBridge Hourly Backup"" /F"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveScheduledBackup"

[UninstallDelete]
Type: files; Name: "{app}\CodexBridge-errors.log"
