; QuickMail InnoSetup Installer Script
; Copyright (c) 2026 Kelly Ford.
;
; QuickMail ships as a self-contained, single-file win-x64 executable: the .NET 8
; runtime is bundled inside QuickMail.exe, so no .NET runtime needs to be installed.
; The only external prerequisite is the Microsoft Edge WebView2 Runtime, which the
; installer detects and installs on demand (see [Code] below).

#define MyAppName "QuickMail"
#define MyAppNameLower Lowercase(MyAppName)
#define MyAppPublisher "Kelly Ford"
#define MyAppURL "https://github.com/kellylford/QuickMail"
#define MyAppSupportURL MyAppURL + "/issues"
#define MyAppExeName MyAppName + ".exe"
#define MyAppDescription "Keyboard-first, accessible desktop email client for Windows"

; Source path (relative to this script). Matches the output of `build.bat publish`
; and the GitHub Actions release step (`dotnet publish ... -o publish/`).
#define SourcePath "..\publish"

; Read the version straight from the compiled executable's FileVersion.
#define MyAppVersion GetVersionNumbersString(SourcePath + "\" + MyAppExeName)

[Setup]
; Application information
AppId={{2E0571C2-B240-443A-A1DB-9A3320639C69}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppSupportURL}
AppUpdatesURL={#MyAppURL}/releases
AppCopyright=Copyright (c) 2026 {#MyAppPublisher}.

; Installation directory
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes

; Output configuration
OutputDir=Output
OutputBaseFilename={#MyAppNameLower}-v{#MyAppVersion}-setup
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern

; Uninstall configuration
UninstallDisplayName={#MyAppName} {#MyAppVersion}
UninstallDisplayIcon={app}\{#MyAppExeName}

; System requirements (Windows 10 1809+, 64-bit)
MinVersion=10.0.17763
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; Privileges: install per-user without elevation by default, but let the user
; choose an all-users install (which elevates) via the standard dialog.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

; If QuickMail is running during an upgrade, use the Restart Manager to detect the
; locked executable and prompt the user to close it (the app defines no mutex).
CloseApplications=yes
RestartApplications=no

DisableProgramGroupPage=yes

; License shown during installation
LicenseFile=..\LICENSE

; Language options
ShowLanguageDialog=auto

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl,Languages\Custom.en.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Self-contained single-file build: QuickMail.exe is the only artifact we ship.
; The .pdb (debug symbols) and Microsoft.Web.WebView2.*.xml (NuGet IntelliSense
; docs) that also land in the publish folder are intentionally excluded.
Source: "{#SourcePath}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Comment: "{cm:AppDescription}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Comment: "{cm:AppDescription}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

#include "CodeDependencies.iss"

[Code]
function InitializeSetup(): Boolean;
begin
  // App is x64 only; keep dependency installers 64-bit too.
  Dependency_ForceX86 := False;

  // QuickMail renders HTML mail through WebView2. The runtime is preinstalled on
  // Windows 11 and recent Windows 10, but install it on demand when missing.
  Dependency_AddWebView2;

  Result := True;
end;

// Offer to remove user data (accounts config, local mail cache, contacts, rules,
// templates, saved views) on uninstall. QuickMail stores everything under
// %APPDATA%\QuickMail. Credentials live in Windows Credential Manager and are not
// touched here.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  UserDataPath: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    UserDataPath := ExpandConstant('{userappdata}\{#MyAppName}');
    if DirExists(UserDataPath) then
    begin
      if MsgBox(CustomMessage('RemoveUserData'),
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      begin
        DelTree(UserDataPath, True, True, True);
      end;
    end;
  end;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = wpReady then
    WizardForm.NextButton.Caption := SetupMessage(msgButtonInstall)
  else if CurPageID = wpFinished then
    WizardForm.NextButton.Caption := SetupMessage(msgButtonFinish)
  else
    WizardForm.NextButton.Caption := SetupMessage(msgButtonNext);
end;
