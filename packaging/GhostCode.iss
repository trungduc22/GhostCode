#ifndef AppVersion
  #define AppVersion "0.1.0-beta.1"
#endif
#ifndef PayloadDir
  #define PayloadDir "..\artifacts\Release\GhostCode-win-x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\Release"
#endif

[Setup]
AppId={{8F9C31E2-7C2A-4D4B-9D8F-2B9E6C41A010}
AppName=GhostCode
AppVersion={#AppVersion}
AppVerName=GhostCode {#AppVersion}
AppPublisher=trungduc22
AppPublisherURL=https://github.com/trungduc22/GhostCode
AppSupportURL=https://github.com/trungduc22/GhostCode/issues
AppUpdatesURL=https://github.com/trungduc22/GhostCode/releases
DefaultDirName={localappdata}\Programs\GhostCode
DefaultGroupName=GhostCode
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
DisableProgramGroupPage=yes
AllowNoIcons=yes
CloseApplications=yes
RestartApplications=no
OutputDir={#OutputDir}
OutputBaseFilename=GhostCode-Setup-x64
SetupIconFile=..\src\GhostCode.App\Assets\app.ico
UninstallDisplayIcon={app}\GhostCode.App.exe
UninstallDisplayName=GhostCode
Compression=lzma2/normal
SolidCompression=yes
WizardStyle=modern
VersionInfoVersion=0.1.0.0
VersionInfoTextVersion={#AppVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Excludes: "*pairing.generated.json,*.dpapi,*.db,*.db-wal,*.db-shm,*.log"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\GhostCode"; Filename: "{app}\GhostCode.App.exe"; WorkingDir: "{app}"; IconFilename: "{app}\Assets\app.ico"; Comment: "GhostCode - Code Companion & Lossless Archive"
Name: "{autodesktop}\GhostCode"; Filename: "{app}\GhostCode.App.exe"; WorkingDir: "{app}"; IconFilename: "{app}\Assets\app.ico"; Tasks: desktopicon; Comment: "GhostCode - Code Companion & Lossless Archive"

[Run]
Filename: "{app}\GhostCode.App.exe"; Description: "{cm:LaunchProgram,GhostCode}"; Flags: nowait postinstall skipifsilent

[Code]
function EscapeJsonBackslashes(const S: String): String;
begin
  Result := S;
  StringChangeEx(Result, '\', '\\', True);
end;

procedure WriteNativeHostFilesForBrowser(const AppDataDir, BrowserKey, ExePath: String; IsFirefox: Boolean);
var
  NmDir, CmdPath, JsonPath, CmdContent, JsonContent: String;
begin
  NmDir := AppDataDir + '\NativeMessaging';
  ForceDirectories(NmDir);

  CmdPath := NmDir + '\com.ghostcode.bridge.' + BrowserKey + '.cmd';
  JsonPath := NmDir + '\com.ghostcode.bridge.' + BrowserKey + '.json';

  CmdContent := '@echo off' + #13#10 + '"' + ExePath + '" --native-messaging-host --browser ' + BrowserKey + ' %*' + #13#10;
  SaveStringToFile(CmdPath, CmdContent, False);

  if IsFirefox then
  begin
    JsonContent := '{' + #13#10 +
      '  "name": "com.ghostcode.bridge",' + #13#10 +
      '  "description": "GhostCode Native Messaging Bridge (Firefox)",' + #13#10 +
      '  "path": "' + EscapeJsonBackslashes(CmdPath) + '",' + #13#10 +
      '  "type": "stdio",' + #13#10 +
      '  "allowed_extensions": ["ghostcode-bridge@trungduc22.github.io"]' + #13#10 +
      '}' + #13#10;
  end
  else
  begin
    JsonContent := '{' + #13#10 +
      '  "name": "com.ghostcode.bridge",' + #13#10 +
      '  "description": "GhostCode Native Messaging Bridge",' + #13#10 +
      '  "path": "' + EscapeJsonBackslashes(CmdPath) + '",' + #13#10 +
      '  "type": "stdio",' + #13#10 +
      '  "allowed_origins": ["chrome-extension://bifmfiolmohcdfknnicfikhhpipkokbc/"]' + #13#10 +
      '}' + #13#10;
  end;
  SaveStringToFile(JsonPath, JsonContent, False);

  if BrowserKey = 'chrome' then
    RegWriteStringValue(HKEY_CURRENT_USER, 'Software\Google\Chrome\NativeMessagingHosts\com.ghostcode.bridge', '', JsonPath)
  else if BrowserKey = 'edge' then
    RegWriteStringValue(HKEY_CURRENT_USER, 'Software\Microsoft\Edge\NativeMessagingHosts\com.ghostcode.bridge', '', JsonPath)
  else if BrowserKey = 'thorium' then
    RegWriteStringValue(HKEY_CURRENT_USER, 'Software\Thorium\NativeMessagingHosts\com.ghostcode.bridge', '', JsonPath)
  else if BrowserKey = 'firefox' then
    RegWriteStringValue(HKEY_CURRENT_USER, 'Software\Mozilla\NativeMessagingHosts\com.ghostcode.bridge', '', JsonPath);
end;

procedure RegisterAllNativeMessagingHosts(const ExePath: String);
var
  AppDataDir: String;
begin
  AppDataDir := ExpandConstant('{localappdata}\GhostCode');
  ForceDirectories(AppDataDir);
  WriteNativeHostFilesForBrowser(AppDataDir, 'chrome', ExePath, False);
  WriteNativeHostFilesForBrowser(AppDataDir, 'edge', ExePath, False);
  WriteNativeHostFilesForBrowser(AppDataDir, 'thorium', ExePath, False);
  WriteNativeHostFilesForBrowser(AppDataDir, 'firefox', ExePath, True);
end;

procedure UnregisterAllNativeMessagingHosts();
var
  NmDir: String;
begin
  RegDeleteKeyIncludingSubkeys(HKEY_CURRENT_USER, 'Software\Google\Chrome\NativeMessagingHosts\com.ghostcode.bridge');
  RegDeleteKeyIncludingSubkeys(HKEY_CURRENT_USER, 'Software\Microsoft\Edge\NativeMessagingHosts\com.ghostcode.bridge');
  RegDeleteKeyIncludingSubkeys(HKEY_CURRENT_USER, 'Software\Thorium\NativeMessagingHosts\com.ghostcode.bridge');
  RegDeleteKeyIncludingSubkeys(HKEY_CURRENT_USER, 'Software\Mozilla\NativeMessagingHosts\com.ghostcode.bridge');

  NmDir := ExpandConstant('{localappdata}\GhostCode\NativeMessaging');
  if DirExists(NmDir) then
  begin
    DeleteFile(NmDir + '\com.ghostcode.bridge.chrome.cmd');
    DeleteFile(NmDir + '\com.ghostcode.bridge.chrome.json');
    DeleteFile(NmDir + '\com.ghostcode.bridge.edge.cmd');
    DeleteFile(NmDir + '\com.ghostcode.bridge.edge.json');
    DeleteFile(NmDir + '\com.ghostcode.bridge.thorium.cmd');
    DeleteFile(NmDir + '\com.ghostcode.bridge.thorium.json');
    DeleteFile(NmDir + '\com.ghostcode.bridge.firefox.cmd');
    DeleteFile(NmDir + '\com.ghostcode.bridge.firefox.json');
  end;
end;

function InitializeUninstall(): Boolean;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM GhostCode.App.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(250);
  Result := True;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    RegisterAllNativeMessagingHosts(ExpandConstant('{app}\GhostCode.App.exe'));
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    UnregisterAllNativeMessagingHosts();
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM GhostCode.App.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(250);
  end;
end;
