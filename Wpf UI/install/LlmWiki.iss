; Inno Setup 6 脚本 — LLM Wiki 安装包（WPF UI 版）
; 用法：ISCC LlmWiki.iss

#define MyAppName = "LLM Wiki"
#ifndef MyAppVersion
  #define MyAppVersion = "1.0.0"
#endif
#define MyAppPublisher = "小周他在想办法"
#define MyAppURL = "https://github.com/Taooiis/LLM-Wiki"
#define MyAppExeName = "LLM Wiki.exe"
#define MyAppIcon = "..\wpfui-icon.ico"
#define MyBuildDir = "..\bin\Release\net10.0-windows\publish"

[Setup]
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppMutex=Local\LLMWiki.SingleInstance
DefaultDirName={autopf}\LLM Wiki
DefaultGroupName=LLM Wiki
OutputBaseFilename=LLM Wiki Setup
Compression=lzma
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
SetupIconFile={#MyAppIcon}
WizardStyle=modern

[Languages]
Name: "chinese"; MessagesFile: "ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
; 自包含发布：包含 .NET 运行时 + 所有依赖
Source: "{#MyBuildDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{autoprograms}\LLM Wiki"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\LLM Wiki"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\{#MyAppExeName}"; Tasks: desktopIcon

[Tasks]
Name: "desktopIcon"; Description: "创建桌面快捷方式"; GroupDescription: "额外选项:"; Flags: unchecked

[Registry]
; 记录安装版本（供更新检查）
Root: HKCU; Subkey: "Software\LLMWiki\Install"; ValueType: string; ValueName: "Version"; ValueData: "{#MyAppVersion}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\LLMWiki\Install"; ValueType: string; ValueName: "InstallDir"; ValueData: "{app}"; Flags: uninsdeletekey

; 文件关联：.md/.txt/.docx/.xlsx → 双击打开 LLM Wiki
Root: HKCU; Subkey: "Software\Classes\.md"; ValueType: string; ValueName: ""; ValueData: "LLMWiki.File"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\.txt"; ValueType: string; ValueName: ""; ValueData: "LLMWiki.File"
Root: HKCU; Subkey: "Software\Classes\.docx"; ValueType: string; ValueName: ""; ValueData: "LLMWiki.File"
Root: HKCU; Subkey: "Software\Classes\.xlsx"; ValueType: string; ValueName: ""; ValueData: "LLMWiki.File"
Root: HKCU; Subkey: "Software\Classes\LLMWiki.File\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName}"
Root: HKCU; Subkey: "Software\Classes\LLMWiki.File\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""

[Run]
; 安装后首次运行
Filename: "{app}\{#MyAppExeName}"; Description: "启动 LLM Wiki"; Flags: nowait postinstall skipifsilent; WorkingDir: "{app}"

[UninstallDelete]
Type: filesandordirs; Name: "{app}\wikis"

[Code]
// 检查是否已安装：安装过就问是否覆盖（保留知识库数据）
function InitializeSetup(): Boolean;
var
  InstalledVersion: String;
begin
  Result := True;

  if RegQueryStringValue(HKCU, 'Software\LLMWiki\Install', 'Version', InstalledVersion) then
  begin
    if CompareStr(InstalledVersion, '{#MyAppVersion}') >= 0 then
    begin
      case MsgBox('本地已安装 LLM Wiki v' + InstalledVersion + '，当前安装包版本 v{#MyAppVersion}。是否覆盖安装（会保留知识库数据）？', mbConfirmation, MB_YESNO) of
        IDYES: Result := True;
        IDNO:  Result := False;
      end;
    end;
  end;
end;

// 卸载前确认；知识库数据不在安装目录，保留
function InitializeUninstall(): Boolean;
begin
  Result := True;
  if MsgBox('确定要卸载 LLM Wiki？（知识库文件位于 %APPDATA%\LLM Wiki\wikis，不会被删除）', mbConfirmation, MB_YESNO) = IDNO then
    Result := False;
end;
