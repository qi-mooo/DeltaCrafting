; DeltaCrafter installer definition for Inno Setup 6.7.3.
; Build through scripts\build-installer.ps1 so the payload, compiler version,
; and repository-owned Simplified Chinese language file are all verified.

#ifndef MyAppVersion
  #error MyAppVersion must be provided by build-installer.ps1
#endif
#ifndef MyFileVersion
  #error MyFileVersion must be provided by build-installer.ps1
#endif
#ifndef PayloadDir
  #error PayloadDir must be provided by build-installer.ps1
#endif
#ifndef ChineseIsl
  #error ChineseIsl must be provided by build-installer.ps1
#endif

#define MyAppName "三角洲特勤助手"
#define MyAppEnglishName "DeltaCrafter"
#define MyAppExeName "DeltaCrafter.exe"
#define MyAppPublisher "DeltaCrafter contributors"
#define MyDataDir "{localappdata}\DeltaCrafter"
#define MyAppDir MyDataDir + "\app"
#define MyAutostartTask "DeltaCrafter-AutoStart"

[Setup]
; AppId is the upgrade/uninstall identity and must remain stable after v0.1.0.
AppId={{A9C6D3F2-4E7B-4C1D-8B0A-5E2F9C7D4A61}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppEnglishName}
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
WizardStyle=modern
SetupIconFile=..\src\DeltaCrafter.App\Assets\AppIcon.ico
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/max
SolidCompression=yes
OutputDir=..\artifacts
OutputBaseFilename={#MyAppEnglishName}-Setup-{#MyAppVersion}
ShowLanguageDialog=no
; The tray process hides on window close, so [Code] owns explicit termination.
CloseApplications=no
VersionInfoVersion={#MyFileVersion}
VersionInfoProductName={#MyAppName}
VersionInfoDescription={#MyAppName} 安装程序

[Languages]
Name: "chinesesimplified"; MessagesFile: "{#ChineseIsl}"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
; Payload is the verified self-installing launcher (plus documents and firmware) from
; build-release.ps1. The launcher extracts the full client to %LocalAppData%\DeltaCrafter\app.
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[InstallDelete]
; Upgrades from the pre-launcher layout: the loose client payload now lives in %LocalAppData%\DeltaCrafter\app,
; so remove the old copy instead of leaving hundreds of MB of dead files in {app}.
Type: files; Name: "{app}\*.dll"; Check: IsLegacyLayout
Type: files; Name: "{app}\*.winmd"; Check: IsLegacyLayout
Type: files; Name: "{app}\*.pri"; Check: IsLegacyLayout
Type: files; Name: "{app}\*.xbf"; Check: IsLegacyLayout
Type: files; Name: "{app}\*.pdb"; Check: IsLegacyLayout
Type: files; Name: "{app}\DeltaCrafter.deps.json"; Check: IsLegacyLayout
Type: files; Name: "{app}\DeltaCrafter.runtimeconfig.json"; Check: IsLegacyLayout
Type: filesandordirs; Name: "{app}\Assets"; Check: IsLegacyLayout
Type: filesandordirs; Name: "{app}\Data"; Check: IsLegacyLayout
Type: filesandordirs; Name: "{app}\Microsoft.UI.Xaml"; Check: IsLegacyLayout

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; Pre-extract the client so the first launch does not wait on unpacking.
Filename: "{app}\{#MyAppExeName}"; Parameters: "--prepare"; StatusMsg: "{cm:PreparingClient}"; Flags: runhidden waituntilterminated
; postinstall defaults to the original non-elevated user token. The app manifest requires
; elevation, so force Setup's current elevated token; otherwise Windows returns error 740.
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchAfterInstall}"; Flags: nowait postinstall skipifsilent runascurrentuser
; In-app updater installs with /SILENT /AutoLaunch=1 and expects an automatic restart.
; Gated on the explicit flag so ordinary silent installs (e.g. CI smoke tests) are unchanged.
Filename: "{app}\{#MyAppExeName}"; Flags: nowait; Check: ShouldAutoLaunch

[CustomMessages]
PreparingClient=正在解压客户端...
LaunchAfterInstall=立即运行 {#MyAppName}
RemoveDataPrompt=是否同时删除运行数据与设置?%n%n包括:制造计划、应用设置、运行日志与诊断截图%n(%1)%n%n选择「否」保留数据,重新安装后可直接继续使用。
StopAppFailed=无法结束正在运行的 {#MyAppName}(taskkill 退出码 %1)。%n%n请手动退出程序后重试。
RemoveAutostartFailed=无法删除「开机自启」计划任务(schtasks 退出码 %1)。%n%n卸载已停止,请修正该任务后重试。
RemoveDataFailed={#MyAppName} 已卸载,但未能完整删除本机数据:%n%n%1%n%n请关闭占用这些文件的程序后手动删除。

[Code]
var
  LastToolExitCode: Integer;
  DeleteUserDataRequested: Boolean;
  LegacyLayout: Boolean;

function IsLegacyLayout(): Boolean;
begin
  { Captured in PrepareToInstall, because the first [InstallDelete] entry removes the marker. }
  Result := LegacyLayout;
end;

function ShouldAutoLaunch(): Boolean;
begin
  { The in-app updater passes /AutoLaunch=1; only then restart the app after a
    silent upgrade. PrepareToInstall already force-stops the old instance, so the
    new one registers cleanly as the single instance. }
  Result := ExpandConstant('{param:AutoLaunch|0}') = '1';
end;

function StopAppProcess(): Boolean;
var
  Started: Boolean;
begin
  LastToolExitCode := -1;
  Started := Exec(ExpandConstant('{sys}\taskkill.exe'),
    '/F /IM {#MyAppExeName}', '', SW_HIDE, ewWaitUntilTerminated,
    LastToolExitCode);
  if not Started then
  begin
    Log('Failed to start taskkill.exe.');
    Result := False;
    exit;
  end;

  { taskkill returns 128 when no matching process exists. }
  if (LastToolExitCode <> 0) and (LastToolExitCode <> 128) then
  begin
    Log(Format('taskkill failed with exit code %d.', [LastToolExitCode]));
    Result := False;
    exit;
  end;

  if LastToolExitCode = 0 then
    Sleep(400);
  Result := True;
end;

function DeleteAutostartTask(): Boolean;
var
  Started: Boolean;
begin
  { The task has a stable root-level name; the task file is the authoritative
    existence check so schtasks query errors are not mistaken for absence. }
  if not FileExists(ExpandConstant('{sys}\Tasks\{#MyAutostartTask}')) then
  begin
    LastToolExitCode := 0;
    Result := True;
    exit;
  end;

  LastToolExitCode := -1;
  Started := Exec(ExpandConstant('{sys}\schtasks.exe'),
    '/Delete /F /TN {#MyAutostartTask}', '', SW_HIDE,
    ewWaitUntilTerminated, LastToolExitCode);
  Result := Started and (LastToolExitCode = 0);
  if not Result then
    Log(Format('schtasks delete failed with exit code %d.', [LastToolExitCode]));
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  { The multi-file layout shipped the managed entry assembly beside the apphost. }
  LegacyLayout := FileExists(ExpandConstant('{app}\DeltaCrafter.dll'));
  if StopAppProcess() then
    Result := ''
  else
    Result := FmtMessage(CustomMessage('StopAppFailed'), [IntToStr(LastToolExitCode)]);
end;

function InitializeUninstall(): Boolean;
begin
  Result := StopAppProcess();
  if not Result then
    SuppressibleMsgBox(
      FmtMessage(CustomMessage('StopAppFailed'), [IntToStr(LastToolExitCode)]),
      mbError, MB_OK, IDOK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    if not DeleteAutostartTask() then
    begin
      SuppressibleMsgBox(
        FmtMessage(CustomMessage('RemoveAutostartFailed'), [IntToStr(LastToolExitCode)]),
        mbError, MB_OK, IDOK);
      Abort;
    end;

    { Silent uninstall always preserves data and never opens a custom prompt. }
    if UninstallSilent() then
      DeleteUserDataRequested := False
    else
      DeleteUserDataRequested :=
        SuppressibleMsgBox(
          FmtMessage(CustomMessage('RemoveDataPrompt'), [ExpandConstant('{#MyDataDir}')]),
          mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES;
  end
  else if CurUninstallStep = usPostUninstall then
  begin
    { The extracted client is not user data; always remove it even when data is kept. }
    if not DeleteUserDataRequested then
    begin
      if not DelTree(ExpandConstant('{#MyAppDir}'), True, True, True) then
        Log('Failed to remove extracted client directory.');
      exit;
    end;
    if not DelTree(ExpandConstant('{#MyDataDir}'), True, True, True) then
      SuppressibleMsgBox(
        FmtMessage(CustomMessage('RemoveDataFailed'), [ExpandConstant('{#MyDataDir}')]),
        mbError, MB_OK, IDOK);
  end;
end;
