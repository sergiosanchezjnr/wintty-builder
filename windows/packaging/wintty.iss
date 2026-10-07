; Inno Setup script for the Wintty Windows installer.
;
; Compiled by .github/workflows/windows-release.yml, which passes:
;   /DPublishDir=<staged publish folder>   (the exact payload of both artifacts)
;   /DAppVersion=<version string>          (tag name, or base+short-SHA for tip)
;   /DOutputDir=<directory for the setup exe>
;
; The portable .zip ships the same PublishDir contents, so installer and zip
; are byte-identical payloads delivered two ways. PublishDir is harvested
; wholesale because Ghostty.csproj publish targets already stage licenses,
; resources, and the Windows App SDK Insights dll into it - the same folder
; the upstream WiX MSI harvest and Velopack pack steps read.

#define MyAppName "Wintty"
#define MyAppPublisher "deblasis"
#define MyAppURL "https://github.com/deblasis/wintty"

#ifndef AppVersion
  #define AppVersion "0.0.0-dev"
#endif

[Setup]
AppId={{8E4A2C1B-6F3D-4C9A-B7E5-2D81F0A6C349}
AppName={#MyAppName}
AppVersion={#AppVersion}
AppVerName={#MyAppName} {#AppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\Wintty
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
LicenseFile=..\Ghostty\licenses\Wintty-LICENSE.txt
OutputDir={#OutputDir}
OutputBaseFilename=wintty-setup-{#AppVersion}-win-x64
UninstallDisplayIcon={app}\Wintty.exe
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
; Self-contained NativeAOT payload: no framework prerequisites to detect.
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Whole staged publish folder, recursively. Source is absolute because the
; workflow always passes an absolute /DPublishDir.
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\Wintty.exe"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\Wintty.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Wintty.exe"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Config lives in %LOCALAPPDATA% and survives uninstall by design (mirrors
; the upstream expectation); anything else left in {app} after upgrade
; churn is removed with the install.
Type: filesandordirs; Name: "{app}"
