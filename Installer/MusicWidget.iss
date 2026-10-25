; ============================================================================
; MusicWidget — Inno Setup installer script
; ============================================================================
;
; Builds a single .exe installer that drops the published, self-contained build
; into C:\Program Files\MusicWidget, adds Start Menu + Desktop shortcuts,
; shows the MusicWidget License Agreement (license.rtf), and uses the app icon.
;
; Build (from repo root):
;   powershell -ExecutionPolicy Bypass -File Installer\Build-Installer.ps1
;
; Output: Installer\Output\MusicWidget-Setup-x64.exe
; ============================================================================

#ifndef MyAppVersion
  #define MyAppVersion "2.2.23"
#endif

#define MyAppName "MusicWidget"
#define MyAppPublisher "Delexo"
#define MyAppStoreURL "https://delexo.store"
#define MyAppHelpURL "https://delexoo.github.io/MusicWidget/help.html"
#define MyAppUpdatesURL "https://github.com/Delexoo/MusicWidget/releases"
#define MyAppExeName "MusicWidget.exe"

[Setup]
AppId={{8E3B4F0A-6B5A-4D6E-9C3F-7F1E1A2D9C4A}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppCopyright=Copyright (C) {#MyAppPublisher} 2026
AppPublisherURL={#MyAppStoreURL}
AppSupportURL={#MyAppHelpURL}
AppUpdatesURL={#MyAppUpdatesURL}
AppContact={#MyAppStoreURL}
AppComments=Published by {#MyAppPublisher}. Visit {#MyAppStoreURL} for support and more products.
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
OutputDir=Output
OutputBaseFilename=MusicWidget-Setup-x64
SetupIconFile=..\MusicWidget\Assets\AppIcon.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
WizardSizePercent=120
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
CloseApplications=yes
RestartApplications=yes
MinVersion=10.0
UsedUserAreasWarning=no
DisableDirPage=no
DisableReadyPage=no
ShowLanguageDialog=no
SetupLogging=yes
LicenseFile=license.rtf
InfoBeforeFile=welcome.txt
VersionInfoVersion={#MyAppVersion}.0
VersionInfoProductVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} installer for Windows (64-bit), published by {#MyAppPublisher}
VersionInfoProductName={#MyAppName}
VersionInfoCopyright=Copyright (C) {#MyAppPublisher} 2026
VersionInfoTextVersion={#MyAppPublisher} | {#MyAppStoreURL}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
english.WelcomeLabel2=This will install [name/ver] on your computer.%n%nMusicWidget is a free floating desktop music player for Windows, published by Delexo. It is not affiliated with Microsoft Corporation. You must accept the MusicWidget License Agreement on the next page before setup can continue.%n%nPublisher: Delexo%nWebsite: https://delexo.store
english.LicenseLabel=MusicWidget License Agreement
english.LicenseLabel3=I agree to the MusicWidget License Agreement. I understand that Delexo (https://delexo.store) created MusicWidget only, is not affiliated with Microsoft Corporation, YouTube, Spotify, or other platforms, and that I am solely responsible for any music or content I use with this application.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "startupicon"; Description: "Launch {#MyAppName} when Windows starts"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\MusicWidget\bin\publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\MusicWidget\Assets\AppIcon.png"; DestDir: "{app}\Assets"; Flags: ignoreversion
Source: "..\MusicWidget\Assets\AppIcon.ico"; DestDir: "{app}\Assets"; Flags: ignoreversion
Source: "license.rtf"; DestDir: "{app}\Legal"; DestName: "License-Agreement.rtf"; Flags: ignoreversion
Source: "terms.txt"; DestDir: "{app}\Legal"; Flags: ignoreversion
Source: "privacy.txt"; DestDir: "{app}\Legal"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}\Legal"; DestName: "MIT-License.txt"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\Assets\AppIcon.ico"; Comment: "MusicWidget by Delexo"
Name: "{group}\Help && manual"; Filename: "{#MyAppHelpURL}"; IconFilename: "{app}\Assets\AppIcon.ico"
Name: "{group}\Delexo — delexo.store"; Filename: "{#MyAppStoreURL}"; IconFilename: "{app}\Assets\AppIcon.ico"
Name: "{group}\License Agreement"; Filename: "{app}\Legal\License-Agreement.rtf"
Name: "{group}\Terms of Service"; Filename: "notepad.exe"; Parameters: """{app}\Legal\terms.txt"""
Name: "{group}\Privacy Policy"; Filename: "notepad.exe"; Parameters: """{app}\Legal\privacy.txt"""
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"; IconFilename: "{app}\Assets\AppIcon.ico"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\Assets\AppIcon.ico"; Tasks: desktopicon
Name: "{userstartup}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\Assets\AppIcon.ico"; Tasks: startupicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall

[UninstallDelete]
