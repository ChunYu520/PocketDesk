; PocketDesk installer script (Inno Setup 6)
; Build:
;   ISCC.exe /DBuildArch=arm64 PocketDesk.iss
;   ISCC.exe /DBuildArch=x86 PocketDesk.iss
;   ISCC.exe /DBuildArch=x64 PocketDesk.iss

#define AppName "PocketDesk"
#define AppVersion "1.0.0"
#define AppPublisher "PocketDesk"
#define AppExeName "PocketDesk.exe"

#ifndef BuildArch
  #define BuildArch "x64"
#endif

#if BuildArch == "arm64"
  #define PublishDir "..\bin\Release\net10.0-windows10.0.26100.0\win-arm64\publish"
  #define ArchAllowed "arm64"
  #define ArchLabel "ARM64"
#elif BuildArch == "x86"
  #define PublishDir "..\bin\Release\net10.0-windows10.0.26100.0\win-x86\publish"
  #define ArchAllowed "x86compatible"
  #define ArchLabel "x86"
#else
  #define PublishDir "..\bin\Release\net10.0-windows10.0.26100.0\win-x64\publish"
  #define ArchAllowed "x64compatible"
  #define ArchLabel "x64"
#endif

#if BuildArch == "x86"
  #define Install64Mode ""
#else
  #define Install64Mode ArchLabel
#endif

[Setup]
AppId={{8B1DDF58-46F9-4F6A-8A70-AEA80CB3BFF9}
AppName={#AppName}
AppVerName={#AppName} {#AppVersion} ({#ArchLabel})
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={localappdata}\Programs\PocketDesk
DefaultGroupName=PocketDesk
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\..\Pack
OutputBaseFilename=PocketDesk-{#AppVersion}-{#ArchLabel}-Setup
SetupIconFile=..\Assets\AppIcon.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName} {#AppVersion} ({#ArchLabel})
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed={#ArchAllowed}
#if BuildArch != "x86"
ArchitecturesInstallIn64BitMode={#Install64Mode}
#endif
CloseApplications=no
RestartApplications=no
VersionInfoVersion=1.0.0.0
VersionInfoCompany={#AppPublisher}
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} installer ({#ArchLabel})
SetupLogging=yes
UsePreviousAppDir=yes
UsePreviousTasks=yes

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务："; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\PocketDesk"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"
Name: "{group}\卸载 PocketDesk"; Filename: "{uninstallexe}"
Name: "{userdesktop}\PocketDesk"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "启动 PocketDesk"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\{#AppExeName}"; Parameters: "--uninstall={code:GetUninstallMode}"; Flags: runhidden waituntilterminated; RunOnceId: "PocketDeskData"

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
var
  FUninstallMode: String;

procedure KillRunningApp;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM {#AppExeName} /F /T', '',
    SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
    KillRunningApp;
end;

function IsSilentUninstall(): Boolean;
var
  Index: Integer;
begin
  Result := False;
  for Index := 1 to ParamCount do
  begin
    if (CompareText(ParamStr(Index), '/SILENT') = 0) or
       (CompareText(ParamStr(Index), '/VERYSILENT') = 0) then
    begin
      Result := True;
      Exit;
    end;
  end;
end;

function InitializeUninstall(): Boolean;
var
  Answer: Integer;
begin
  Result := False;
  KillRunningApp;

  if IsSilentUninstall then
  begin
    FUninstallMode := 'keep';
    Result := True;
    Exit;
  end;

  Answer := MsgBox('卸载前是否把普通收纳盒中的文件还原到原位置？' + #13#10 + #13#10 +
    '是：还原文件并删除 PocketDesk 数据' + #13#10 +
    '否：保留数据目录（推荐，之后可恢复）' + #13#10 +
    '取消：停止卸载', mbConfirmation, MB_YESNOCANCEL);
  if Answer = IDCANCEL then
    Exit;

  if Answer = IDYES then
    FUninstallMode := 'restore'
  else
  begin
    Answer := MsgBox('是否同时删除 PocketDesk 的全部数据（数据库、日志、收纳盒文件）？' + #13#10 + #13#10 +
      '选择“否”将保留数据目录。', mbConfirmation, MB_YESNO);
    if Answer = IDYES then
      FUninstallMode := 'delete'
    else
      FUninstallMode := 'keep';
  end;

  Result := True;
end;

function GetUninstallMode(Param: String): String;
begin
  Result := FUninstallMode;
end;
