; Snip — PowerToys Run plugin installer (Inno Setup 6.5+).
;
; One script, four packages, selected with ISCC /D defines:
;   /DAppVersion=1.4.0   plugin.json version (passed by build\build-installer.ps1)
;   /DArch=x64|arm64     target OS architecture (separate AppId per arch)
;   /DVariant=slim|full  slim = no wco_data (OCR downloads on demand); full = bundled wco_data
;   /DStageDir=<path>    staged plugin folder (contents of Snip\)
;   /DOutputDir=<path>   where Snip-Setup-<ver>-<arch>-<variant>.exe is written
;
; Install target (PowerToys doc "Installing and upgrading third-party PowerToys Run plugins"):
;   %LOCALAPPDATA%\Microsoft\PowerToys\PowerToys Run\Plugins\Snip
; Per-user, PrivilegesRequired=lowest (no UAC). PowerToys must already be installed, >= 0.100.0:
; the first release whose host runs on .NET 10 (microsoft/PowerToys#41280). This plugin targets
; net10.0-windows, so older (.NET 9) hosts cannot load it.

#if Ver < EncodeVer(6, 5, 0)
  #error Inno Setup 6.5 or newer is required (Languages\ChineseSimplified.isl targets 6.5+).
#endif

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef Arch
  #define Arch "x64"
#endif
#ifndef Variant
  #define Variant "slim"
#endif
#ifndef StageDir
  #error StageDir is required: /DStageDir=<staged Snip folder>
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\installer"
#endif

#define PluginFolder "Snip"
#define PluginAssembly "Community.PowerToys.Run.Plugin.Snip.dll"
; Keep in sync with README "系统要求 / Requirements".
#define MinPowerToysVersion "0.100.0"
; Plugin data written at runtime (OCR cache, HyMT models, sidecars) — never deleted on upgrade.
#define PluginDataSubdir "PowerToys\RunPlugins\Community.PowerToys.Run.Plugin.Snip"

#if Arch == "x64"
  #define AppGuid "{{6E8C1B5A-3F2D-4C7E-9A41-5D0B7E2C9F10}"
  #define ArchIdent "x64os"
#elif Arch == "arm64"
  #define AppGuid "{{B2D47F93-81C6-4A0E-B5F3-7C9E1A2D6084}"
  #define ArchIdent "arm64"
#else
  #error Arch must be x64 or arm64
#endif

#if Variant != "slim" && Variant != "full"
  #error Variant must be slim or full
#endif

#if !FileExists(AddBackslash(StageDir) + PluginAssembly)
  #error StageDir does not contain the plugin assembly
#endif
#if !FileExists(AddBackslash(StageDir) + "plugin.json")
  #error StageDir does not contain plugin.json
#endif
#if Variant == "full"
  #if !FileExists(AddBackslash(StageDir) + "wco_data\WeChatOCR.exe")
    #error Full variant requires wco_data\WeChatOCR.exe in StageDir
  #endif
#endif

[Setup]
AppId={#AppGuid}
AppName=Snip for PowerToys Run
AppVersion={#AppVersion}
AppVerName=Snip {#AppVersion} ({#Arch}, {#Variant})
AppPublisher=Jotaro2025
AppPublisherURL=https://github.com/Jotaro2025/PowerToys-Run-Snip
AppSupportURL=https://github.com/Jotaro2025/PowerToys-Run-Snip/issues
AppUpdatesURL=https://github.com/Jotaro2025/PowerToys-Run-Snip/releases
VersionInfoVersion={#AppVersion}
VersionInfoProductName=Snip for PowerToys Run
VersionInfoDescription=Snip PowerToys Run plugin installer ({#Arch}, {#Variant})
DefaultDirName={localappdata}\Microsoft\PowerToys\PowerToys Run\Plugins\{#PluginFolder}
DisableDirPage=yes
UsePreviousAppDir=no
DisableProgramGroupPage=yes
DisableReadyPage=no
UninstallDisplayName=Snip for PowerToys Run ({#Arch})
PrivilegesRequired=lowest
ArchitecturesAllowed={#ArchIdent}
ArchitecturesInstallIn64BitMode={#ArchIdent}
; PowerToys is closed/restarted by [Code] with explicit consent; don't let Restart Manager do it.
CloseApplications=no
RestartApplications=no
OutputDir={#OutputDir}
OutputBaseFilename=Snip-Setup-{#AppVersion}-{#Arch}-{#Variant}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
SetupLogging=yes
ShowLanguageDialog=auto

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesesimplified"; MessagesFile: "Languages\ChineseSimplified.isl"

[CustomMessages]
english.PtRunningPrompt=PowerToys is running. It must be closed so the plugin files can be replaced.%n%nClose PowerToys now? It will be restarted afterwards.
chinesesimplified.PtRunningPrompt=PowerToys 正在运行，需要先关闭才能替换插件文件。%n%n现在关闭 PowerToys 吗？完成后会自动重新启动。
english.PtCloseFailed=PowerToys could not be closed. Please exit it from the tray icon and run setup again.
chinesesimplified.PtCloseFailed=无法关闭 PowerToys。请从托盘图标退出 PowerToys 后重新运行安装程序。
english.PtCancelled=Setup cancelled: PowerToys must be closed first.
chinesesimplified.PtCancelled=已取消：需要先关闭 PowerToys。
english.UninstRunningPrompt=PowerToys is running and must be closed to remove the plugin.%n%nClose PowerToys now? It will be restarted afterwards.
chinesesimplified.UninstRunningPrompt=PowerToys 正在运行，卸载插件前需要先关闭。%n%n现在关闭 PowerToys 吗？完成后会自动重新启动。
english.DeleteDataPrompt=Also delete Snip data?%n%n%1%n%nThis includes downloaded OCR files and HyMT translation models. Choose No to keep them (recommended if you plan to reinstall).
chinesesimplified.DeleteDataPrompt=是否同时删除 Snip 数据？%n%n%1%n%n其中包括已下载的 OCR 文件和 HyMT 翻译模型。选「否」则保留（打算重新安装时推荐）。
english.Arm64OcrNote=Note for ARM64: screenshot, annotation, pin and save work natively, but WeChat OCR (text extraction / image translate) ships only x64 native code and is not available inside an ARM64 PowerToys process.
chinesesimplified.Arm64OcrNote=ARM64 提示：截屏、标注、钉图、保存均可原生使用；但微信 OCR（文字提取 / 图片翻译）只有 x64 原生组件，在 ARM64 版 PowerToys 进程中不可用。

[InstallDelete]
; Upgrade: wipe the previous plugin folder first so stale DLLs never linger.
; Only {app} (...\Plugins\Snip) is touched; user data lives under {localappdata}\{#PluginDataSubdir}
; and in PowerToys Run settings.json, both left intact.
Type: filesandordirs; Name: "{app}\*"

[Files]
#if Variant == "slim"
Source: "{#StageDir}\*"; DestDir: "{app}"; Excludes: "*.pdb,\wco_data"; Flags: ignoreversion recursesubdirs createallsubdirs
#else
Source: "{#StageDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs
#endif

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
const
  PtExeName = 'PowerToys.exe';
  PtLauncherExeName = 'PowerToys.PowerLauncher.exe';
  UninstallRoot = 'Software\Microsoft\Windows\CurrentVersion\Uninstall';

var
  PtExePath: String;        // best detected PowerToys.exe (version check / restart fallback)
  PtRunningExePath: String; // path of the running instance, if WMI reported one
  PtWasRunning: Boolean;

// ---------------------------------------------------------------------------------------
// Version helpers
// ---------------------------------------------------------------------------------------

function NextVersionPart(var S: String): Word;
var
  P: Integer;
  Part: String;
begin
  P := Pos('.', S);
  if P > 0 then
  begin
    Part := Copy(S, 1, P - 1);
    Delete(S, 1, P);
  end
  else
  begin
    Part := S;
    S := '';
  end;
  Result := StrToIntDef(Trim(Part), 0);
end;

function ParseVersion(S: String): Int64;
var
  A, B, C, D: Word;
begin
  S := Trim(S);
  if (Length(S) > 0) and ((S[1] = 'v') or (S[1] = 'V')) then
    Delete(S, 1, 1);
  A := NextVersionPart(S);
  B := NextVersionPart(S);
  C := NextVersionPart(S);
  D := NextVersionPart(S);
  Result := PackVersionComponents(A, B, C, D);
end;

function VersionToStr(V: Int64): String;
var
  A, B, C, D: Word;
begin
  UnpackVersionComponents(V, A, B, C, D);
  Result := Format('%d.%d.%d', [A, B, C]);
end;

// ---------------------------------------------------------------------------------------
// PowerToys detection: per-user / machine install folders, then Uninstall registry.
// ---------------------------------------------------------------------------------------

procedure ConsiderExe(const Path: String; var BestPath: String; var BestVer: Int64);
var
  V: Int64;
begin
  if (Path = '') or not FileExists(Path) then
    Exit;
  if not GetPackedVersion(Path, V) then
    V := 0;
  Log(Format('PowerToys candidate: %s (version %s)', [Path, VersionToStr(V)]));
  if (BestPath = '') or (ComparePackedVersion(V, BestVer) > 0) then
  begin
    BestPath := Path;
    BestVer := V;
  end;
end;

procedure ScanUninstallKey(RootKey: Integer; var BestPath: String; var BestVer: Int64;
  var RegVer: Int64);
var
  Names: TArrayOfString;
  I: Integer;
  Key, DisplayName, Location, DisplayVersion: String;
  V: Int64;
begin
  if not RegGetSubkeyNames(RootKey, UninstallRoot, Names) then
    Exit;
  for I := 0 to GetArrayLength(Names) - 1 do
  begin
    Key := UninstallRoot + '\' + Names[I];
    if not RegQueryStringValue(RootKey, Key, 'DisplayName', DisplayName) then
      Continue;
    if Pos('powertoys', Lowercase(DisplayName)) <> 1 then
      Continue;
    if RegQueryStringValue(RootKey, Key, 'InstallLocation', Location) and (Location <> '') then
      ConsiderExe(AddBackslash(RemoveQuotes(Location)) + PtExeName, BestPath, BestVer);
    if RegQueryStringValue(RootKey, Key, 'DisplayVersion', DisplayVersion) then
    begin
      V := ParseVersion(DisplayVersion);
      Log(Format('PowerToys uninstall entry: %s %s', [DisplayName, DisplayVersion]));
      if ComparePackedVersion(V, RegVer) > 0 then
        RegVer := V;
    end;
  end;
end;

// Returns the highest PowerToys version found (0 = not installed) and sets its exe path.
function DetectPowerToys(var ExePath: String): Int64;
var
  BestVer, RegVer: Int64;
begin
  ExePath := '';
  BestVer := 0;
  RegVer := 0;
  ConsiderExe(ExpandConstant('{localappdata}\PowerToys\') + PtExeName, ExePath, BestVer);
  ConsiderExe(ExpandConstant('{commonpf64}\PowerToys\') + PtExeName, ExePath, BestVer);
  ScanUninstallKey(HKCU, ExePath, BestVer, RegVer);
  ScanUninstallKey(HKLM64, ExePath, BestVer, RegVer);
  ScanUninstallKey(HKLM32, ExePath, BestVer, RegVer);
  // Take the newest of exe file versions and registry DisplayVersion: a leftover exe of an old
  // portable copy must not block an up-to-date install that the registry knows about.
  if ComparePackedVersion(RegVer, BestVer) > 0 then
    BestVer := RegVer;
  Result := BestVer;
end;

// ---------------------------------------------------------------------------------------
// Running PowerToys (WMI) + close / restart
// ---------------------------------------------------------------------------------------

function QueryRunning(const ExeName: String; var FoundPath: String): Boolean;
var
  Locator, Service, Items, Item: Variant;
begin
  Result := False;
  FoundPath := '';
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Service := Locator.ConnectServer('.', 'root\CIMV2');
    Items := Service.ExecQuery('SELECT ExecutablePath FROM Win32_Process WHERE Name = ''' + ExeName + '''');
    if Items.Count > 0 then
    begin
      Result := True;
      try
        Item := Items.ItemIndex(0);
        if not VarIsNull(Item.ExecutablePath) then
          FoundPath := Item.ExecutablePath;
      except
        FoundPath := '';
      end;
    end;
  except
    Log('WMI process query failed: ' + GetExceptionMessage);
  end;
end;

function IsPowerToysRunning(var RunningPath: String): Boolean;
var
  LauncherPath: String;
begin
  Result := QueryRunning(PtExeName, RunningPath);
  if QueryRunning(PtLauncherExeName, LauncherPath) then
    Result := True;
end;

procedure TaskKill(const Args: String);
var
  Code: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), Args, '', SW_HIDE, ewWaitUntilTerminated, Code);
  Log(Format('taskkill %s -> %d', [Args, Code]));
end;

function WaitUntilClosed(TimeoutMs: Integer): Boolean;
var
  Waited: Integer;
  Dummy: String;
begin
  Waited := 0;
  while IsPowerToysRunning(Dummy) and (Waited < TimeoutMs) do
  begin
    Sleep(250);
    Waited := Waited + 250;
  end;
  Result := not IsPowerToysRunning(Dummy);
end;

// Graceful first (close request to the process tree), then force.
function ClosePowerToys(): Boolean;
begin
  TaskKill('/IM ' + PtExeName + ' /T');
  TaskKill('/IM ' + PtLauncherExeName + ' /T');
  Result := WaitUntilClosed(4000);
  if not Result then
  begin
    TaskKill('/IM ' + PtExeName + ' /T /F');
    TaskKill('/IM ' + PtLauncherExeName + ' /T /F');
    Result := WaitUntilClosed(6000);
  end;
end;

// Returns '' on success (or not running); otherwise the reason to abort with.
function EnsurePowerToysClosed(const Prompt: String): String;
begin
  Result := '';
  PtWasRunning := IsPowerToysRunning(PtRunningExePath);
  if not PtWasRunning then
    Exit;
  Log('PowerToys is running: ' + PtRunningExePath);
  if SuppressibleMsgBox(Prompt, mbConfirmation, MB_OKCANCEL, IDOK) <> IDOK then
  begin
    PtWasRunning := False;
    Result := CustomMessage('PtCancelled');
    Exit;
  end;
  if not ClosePowerToys() then
    Result := CustomMessage('PtCloseFailed');
end;

procedure RestartPowerToysIfNeeded();
var
  Path: String;
  Code: Integer;
begin
  if not PtWasRunning then
    Exit;
  PtWasRunning := False;
  Path := PtRunningExePath;
  if (Path = '') or not FileExists(Path) then
    Path := PtExePath;
  if (Path = '') or not FileExists(Path) then
  begin
    Log('PowerToys was running but no executable path to restart.');
    Exit;
  end;
  Log('Restarting PowerToys: ' + Path);
  if not Exec(Path, '', ExtractFileDir(Path), SW_SHOWNORMAL, ewNoWait, Code) then
    Log(Format('Restart failed: %d', [Code]));
end;

// ---------------------------------------------------------------------------------------
// Setup events
// ---------------------------------------------------------------------------------------

function InitializeSetup(): Boolean;
var
  Found, MinVer: Int64;
  Msg: String;
begin
  Result := True;
  MinVer := ParseVersion('{#MinPowerToysVersion}');
  Found := DetectPowerToys(PtExePath);
  Log(Format('PowerToys detected: %s at "%s" (minimum {#MinPowerToysVersion})', [VersionToStr(Found), PtExePath]));
  if (Found = 0) or (ComparePackedVersion(Found, MinVer) < 0) then
  begin
    if Found = 0 then
      Msg := '未检测到 PowerToys。Snip 是 PowerToys Run 插件，需要先安装 PowerToys {#MinPowerToysVersion} 或更高版本。' + #13#10 +
             'PowerToys was not found. Snip is a PowerToys Run plugin and requires PowerToys {#MinPowerToysVersion} or newer.'
    else
      Msg := Format('检测到 PowerToys %s，版本过旧；Snip 需要 PowerToys {#MinPowerToysVersion} 或更高版本（.NET 10 宿主）。', [VersionToStr(Found)]) + #13#10 +
             Format('PowerToys %s is too old; Snip requires PowerToys {#MinPowerToysVersion} or newer (.NET 10 host).', [VersionToStr(Found)]);
    Msg := Msg + #13#10#13#10 +
      '下载 / Download: https://github.com/microsoft/PowerToys/releases' + #13#10 +
      '或使用 winget / or via winget:' + #13#10 +
      '    winget install Microsoft.PowerToys' + #13#10#13#10 +
      '安装 PowerToys 后请重新运行本安装程序。 / Install PowerToys, then run this setup again.';
    SuppressibleMsgBox(Msg, mbCriticalError, MB_OK, IDOK);
    Result := False;
    Exit;
  end;
#if Arch == "arm64"
  SuppressibleMsgBox(CustomMessage('Arm64OcrNote'), mbInformation, MB_OK, IDOK);
#endif
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := EnsurePowerToysClosed(CustomMessage('PtRunningPrompt'));
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    RestartPowerToysIfNeeded();
end;

// ---------------------------------------------------------------------------------------
// Uninstall events
// ---------------------------------------------------------------------------------------

function InitializeUninstall(): Boolean;
var
  Err: String;
begin
  Result := True;
  DetectPowerToys(PtExePath);
  Err := EnsurePowerToysClosed(CustomMessage('UninstRunningPrompt'));
  if Err <> '' then
  begin
    SuppressibleMsgBox(Err, mbError, MB_OK, IDOK);
    Result := False;
  end;
end;

function HasSwitch(const Name: String): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), Name) = 0 then
    begin
      Result := True;
      Exit;
    end;
end;

function WantsDeleteData(const DataDir: String): Boolean;
begin
  // /DELETEDATA forces removal; a silent uninstall otherwise keeps data.
  if HasSwitch('/DELETEDATA') then
  begin
    Result := True;
    Exit;
  end;
  if UninstallSilent then
  begin
    Result := False;
    Exit;
  end;
  Result := MsgBox(FmtMessage(CustomMessage('DeleteDataPrompt'), [DataDir]),
    mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{localappdata}\{#PluginDataSubdir}');
    Log('Plugin data folder: ' + DataDir);
    if DirExists(DataDir) and WantsDeleteData(DataDir) then
    begin
      Log('Deleting plugin data: ' + DataDir);
      DelTree(DataDir, True, True, True);
    end;
    RestartPowerToysIfNeeded();
  end;
end;
