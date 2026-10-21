; Inno Setup script: builds ScreenshotVault-Setup.exe. Installs for the current user only
; (no admin prompt) into %LOCALAPPDATA%\Programs\Screenshot Vault.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

[Setup]
AppId={{6F1C2B9E-5A47-4C1D-9B8E-3D2F7A6C5E10}
AppName=Screenshot Vault
AppVersion={#AppVersion}
AppPublisher=Screenshot Vault
AppPublisherURL=https://github.com/aarushibhallawork-source/screenshotter
DefaultDirName={localappdata}\Programs\Screenshot Vault
DefaultGroupName=Screenshot Vault
DisableProgramGroupPage=yes
DisableDirPage=yes
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=ScreenshotVault-Setup
SetupIconFile=app.ico
UninstallDisplayIcon={app}\ScreenshotVault.exe
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=force

[Files]
Source: "..\build\windows\ScreenshotVault.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\Screenshot Vault"; Filename: "{app}\ScreenshotVault.exe"

[Registry]
; The app manages this itself ("Start with Windows" in its menu); remove it on uninstall.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "ScreenshotVault"; Flags: uninsdeletevalue dontcreatekey

[Run]
Filename: "{app}\ScreenshotVault.exe"; Description: "Open Screenshot Vault"; Flags: nowait postinstall

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM ScreenshotVault.exe"; Flags: runhidden; RunOnceId: "StopApp"
