; Inno Setup script for Billing Software.
; Build with installer\build-installer.ps1 (it publishes the program first), or open this file in Inno Setup 6 after publishing to artifacts\publish\BillingSoftware.
; NOT YET COMPILED OR RUN: Inno Setup was not available where this was written. Compile it, install on a clean Windows 10/11 PC,
; start the program, uninstall, and reinstall over the top before giving it to a customer.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define AppName "Billing Software"
#define AppExe "BillingSoftware.exe"

[Setup]
; Keep this AppId the same in every release so a new installer updates the old one in place.
AppId={{6E0D3E4B-5C1A-4F3E-9B1D-2A7C8F0B5D11}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppName}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=..\artifacts\installer
OutputBaseFilename=BillingSoftware-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; The program is 64-bit and self-contained (no .NET needed on the customer PC).
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Installing under Program Files and preparing the shared data folder needs administrator rights.
PrivilegesRequired=admin
UninstallDisplayIcon={app}\{#AppExe}
CloseApplications=yes
RestartApplications=no
; Set SignTool in the Inno Setup IDE (or pass /S on the command line) to sign the installer; see build-installer.ps1.
; SignTool=signtool

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Put a shortcut on the desktop"; GroupDescription: "Shortcuts:"

[Files]
Source: "..\artifacts\publish\BillingSoftware\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Dirs]
; The shop's data lives apart from the program, so updating or reinstalling never touches it.
; Everyone who uses this PC must be able to write here (the database is encrypted; its key is protected by Windows).
Name: "{commonappdata}\BillingSoftware"; Permissions: users-modify
Name: "{commonappdata}\BillingSoftware\keys"; Permissions: users-modify
Name: "{commonappdata}\BillingSoftware\logs"; Permissions: users-modify
; Where the program keeps backups unless the owner chooses another place. It is outside the program folder on purpose: uninstalling or
; updating never removes it, and neither does restarting Windows.
Name: "{commondocs}\BillingSoftware Backups"; Permissions: users-modify

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "Start {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Only program files are removed. The data folder (database, key, logs) and the backup folder in the shared Documents are deliberately left alone.
Type: filesandordirs; Name: "{app}"

[Code]
const
  DataDir = '{commonappdata}\BillingSoftware';

{ Before an update replaces the program, keep a copy of the database (the program is closed by now, so the file is complete). }
procedure SafetyCopyBeforeUpdate;
var
  Db, Target, Stamp: String;
begin
  Db := ExpandConstant(DataDir + '\billing.db');
  if not FileExists(Db) then Exit;
  Stamp := GetDateTimeString('yyyymmdd-hhnnss', '-', ':');
  Target := ExpandConstant(DataDir + '\before-update');
  ForceDirectories(Target);
  CopyFile(Db, Target + '\billing-before-update-' + Stamp + '.db', False);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then SafetyCopyBeforeUpdate;
end;

function InitializeUninstall: Boolean;
begin
  Result := True;
  if FileExists(ExpandConstant(DataDir + '\billing.db')) then
    MsgBox('Your shop data (bills, items, customers and the database key) stays on this computer in ' + ExpandConstant(DataDir) +
           '.' + #13#10 + 'Your backups stay in ' + ExpandConstant('{commondocs}\BillingSoftware Backups') + '.' + #13#10 +
           'Uninstalling does not delete either. Keep the recovery key safe.', mbInformation, MB_OK);
end;
