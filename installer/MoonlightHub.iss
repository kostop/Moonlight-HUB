; Inno Setup 6 script for Moonlight Hub
; Build:  ISCC.exe MoonlightHub.iss  (expects the published files in ..\dist)

#define MyAppName "Moonlight Hub"
#define MyAppNameCn "Moonlight Hub 副屏中心"
#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif
#define MyAppPublisher "kostop"
#define MyAppURL "https://github.com/kostop/Moonlight-HUB"
#define MyAppExeName "MoonlightHub.exe"
#define SourceDir "..\dist"

[Setup]
AppId={{7C3E5F2A-9B41-4E8B-9C5D-2F0A6D3C1B77}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppNameCn} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\Moonlight Hub
DefaultGroupName=Moonlight Hub
UninstallDisplayIcon={app}\{#MyAppExeName}
OutputDir=..\release
OutputBaseFilename=MoonlightHub-Setup-{#MyAppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
SetupIconFile=..\src\Assets\app.ico
DisableProgramGroupPage=yes
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "autostart"; Description: "开机自动启动 Moonlight Hub（登录后最小化到托盘）"; GroupDescription: "启动:"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppNameCn}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppNameCn}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Parameters: "--cli autostart on"; Flags: runhidden waituntilterminated; Tasks: autostart
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\{#MyAppExeName}"; Parameters: "--cli shutdown"; Flags: runhidden waituntilterminated; RunOnceId: "shutdownhub"
Filename: "{app}\{#MyAppExeName}"; Parameters: "--cli autostart off"; Flags: runhidden waituntilterminated; RunOnceId: "autostartoff"

[Code]
// .NET 9 Desktop Runtime check (framework-dependent build)
function DotNetDesktopRuntimeInstalled(): Boolean;
var
  Names: TArrayOfString;
  I: Integer;
  Key: String;
begin
  Result := False;
  Key := 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App';
  if RegGetValueNames(HKLM, Key, Names) then
  begin
    for I := 0 to GetArrayLength(Names) - 1 do
      if Copy(Names[I], 1, 2) = '9.' then
        Result := True;
  end;
end;

function InitializeSetup(): Boolean;
var
  ErrorCode: Integer;
begin
  Result := True;
  if not DotNetDesktopRuntimeInstalled() then
  begin
    if MsgBox('Moonlight Hub 需要 .NET 9 Desktop Runtime (x64)。' + #13#10 +
              '是否现在打开下载页面？（安装运行库后再重新运行本安装程序）', mbConfirmation, MB_YESNO) = IDYES then
      ShellExec('open', 'https://dotnet.microsoft.com/download/dotnet/9.0/runtime', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
    Result := False;
  end;
end;
