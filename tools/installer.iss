; Build with Inno Setup 6; publish.ps1 supplies the matching runtime output root.
#ifndef AppVersion
  #define AppVersion "1.0.2"
#endif
#ifndef AppVersionNumeric
  #define AppVersionNumeric "1.0.2"
#endif
#ifndef Architecture
  #define Architecture "x64"
#endif
#ifndef PublishRoot
  #if Architecture == "arm64"
    #define PublishRoot "..\out\win-arm64"
  #else
    #define PublishRoot "..\out"
  #endif
#endif
#ifndef SelfContained
  #define SelfContained "0"
#endif
#if SelfContained == "1"
  #define PublishKind "scd"
#else
  #define PublishKind "fdd"
#endif

[Setup]
AppId={{13C03752-4AB4-4E02-942F-1D7273BE8210}
AppName=屏译 SnapTrans
AppVersion={#AppVersion}
VersionInfoVersion={#AppVersionNumeric}
AppPublisher=SnapTrans contributors
DefaultDirName={localappdata}\Programs\SnapTrans
DefaultGroupName=屏译 SnapTrans
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
#if Architecture == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#elif Architecture == "x64"
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#else
  #error Unsupported Architecture. Select x64 or arm64.
#endif
MinVersion=10.0.19045
UninstallDisplayIcon={app}\SnapTrans.exe
OutputDir={#PublishRoot}\installers
OutputBaseFilename=SnapTrans-{#AppVersion}-win-{#Architecture}-{#PublishKind}-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
SetupLogging=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#PublishRoot}\{#PublishKind}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\屏译 SnapTrans"; Filename: "{app}\SnapTrans.exe"

[Run]
Filename: "{app}\SnapTrans.exe"; Description: "Launch SnapTrans"; Flags: nowait postinstall skipifsilent

[Registry]
; The application owns the optional entry; uninstall always removes it.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "SnapTrans"; Flags: uninsdeletevalue

[Code]
function HasDesktopRuntime10: Boolean;
var
  Versions: TArrayOfString;
  Index: Integer;
begin
  Result := False;
  if RegGetValueNames(HKLM64, 'SOFTWARE\dotnet\Setup\InstalledVersions\{#Architecture}\sharedfx\Microsoft.WindowsDesktop.App', Versions) then
    for Index := 0 to GetArrayLength(Versions) - 1 do
      if (Copy(Versions[Index], 1, 3) = '10.') and (Pos('-', Versions[Index]) = 0) then
      begin
        Result := True;
        Exit;
      end;
end;

function InitializeSetup: Boolean;
var
  ErrorCode: Integer;
begin
  Result := True;
#if SelfContained == "0"
  if not HasDesktopRuntime10 then
  begin
    if MsgBox('SnapTrans requires the {#Architecture} .NET 10 Desktop Runtime.' + #13#10 +
      'Open the official Microsoft download page? Install the Desktop Runtime, then run this installer again.' + #13#10 +
      'Alternatively, use the SnapTrans self-contained package.', mbInformation, MB_YESNO) = IDYES then
      ShellExec('open', 'https://dotnet.microsoft.com/en-us/download/dotnet/10.0', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
    Result := False;
  end;
#endif
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'SnapTrans');
    if (not UninstallSilent) and (MsgBox('Remove SnapTrans settings, logs and diagnostic image exports for this Windows user?' + #13#10 +
      'Manually saved translated images will be kept.', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES) then
    begin
      DelTree(ExpandConstant('{userappdata}\SnapTrans'), True, True, True);
      DelTree(ExpandConstant('{localappdata}\SnapTrans'), True, True, True);
    end;
  end;
end;
