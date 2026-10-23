#define MyAppName "Peek"
#define MyAppVersion "7.0.0"
#define MyAppExe "Peek.exe"

[Setup]
AppId={{dc755e4f-e771-45d7-91c5-59cf5cc9031e}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
DefaultDirName={localappdata}\Programs\Peek
DefaultGroupName=Peek
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=PeekSetup-v7
SetupIconFile=..\publish\PeekPreview.ico
UninstallDisplayIcon={app}\PeekPreview.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: "startup"; Description: "Start Peek when I sign in"; Flags: checkedonce

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Peek"; Filename: "{app}\{#MyAppExe}"; Parameters: "--show"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Peek"; ValueData: """{app}\{#MyAppExe}"" --tray"; Tasks: startup; Flags: uninsdeletevalue

[Code]
function CheckWebView2(): Boolean;
var
  WebView2Path: String;
  WebView2Found: Boolean;
begin
  WebView2Found := RegKeyExists(HKEY_LOCAL_MACHINE, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}');
  if not WebView2Found then
	WebView2Found := RegKeyExists(HKEY_CURRENT_USER, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}');

  if not WebView2Found then
  begin
	MsgBox('The WebView2 Runtime is required but not installed. Please install it from: https://developer.microsoft.com/en-us/microsoft-edge/webview2/', mbInformation, MB_OK);
	Result := False;
  end
  else
	Result := True;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  if not CheckWebView2() then
	Result := 'WebView2 Runtime is required. Please install it first.';
end;

[Run]
Filename: "{app}\{#MyAppExe}"; Parameters: "--tray"; Description: "Start Peek"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{cmd}"; Parameters: "/C taskkill /IM {#MyAppExe} /F"; Flags: runhidden; RunOnceId: "KillPeek"
