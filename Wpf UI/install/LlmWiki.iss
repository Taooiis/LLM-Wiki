; Inno Setup 5 脚本 — LLM Wiki 安装包（WPF UI 版）
; 用法：ISCC LlmWiki.iss
;
; 全 WPF 化版本：无 WebView2 运行时依赖，仅 .NET 10.0 桌面运行时（随自包含发布）

#define MyAppName = "LLM Wiki"
#define MyAppVersion = "1.0.0"
#define MyAppPublisher = "LLM Wiki Project"
#define MyAppURL = "https://github.com/nashsu/llm_wiki"
#define MyAppExeName = "LLM Wiki.exe"
#define MyAppIcon = "..\wpfui-icon.ico"
#define MyBuildDir = "..\bin\Release\net10.0-windows\publish"

[Setup]
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppURL={#MyAppURL}
AppMutex=Local\LLMWiki.SingleInstance
DefaultDirName={autopf}\LLM Wiki
DefaultGroupName=LLM Wiki
OutputBaseName=LLM Wiki Setup
Compression=lzma
SolidCompression=yes
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
SetupIconFile={#MyAppIcon}
WizardStyle=modern
LicenseFile: ""
WindowVisible=no

[Languages]
Name: "chinese"; MessagesFile: "chinese.isl"
Name: "english"; MessagesFile: "english.isl"

[Files]
; 自包含发布：包含 .NET 运行时 + 所有依赖
Source: "{#MyBuildDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{autoprograms}\LLM Wiki"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\{#MyAppIcon}"
Name: "{autodesktop}\LLM Wiki"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\{#MyAppIcon}"; Tasks: desktopIcon

[Tasks]
Name: "desktopIcon"; Description: "创建桌面快捷方式"; GroupDescription: "额外选项:"; Flags: unchecked

[Registry]
; 记录安装版本（供更新检查）
Root: HKCU; Subkey: "Software\LLMWiki\Install"; ValueType: string; ValueName: "Version"; ValueData: "{#MyAppVersion}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\LLMWiki\Install"; ValueType: string; ValueName: "InstallDir"; ValueData: "{app}"; Flags: uninsdeletekey

; 文件关联：.md/.txt/.docx/.xlsx → 双击打开 LLM Wiki
Name: "{autodesktop}"; Flags: unchecked
Root: HKCU; Subkey: "Software\Classes\.md"; ValueType: string; ValueName: ""; ValueData: "LLMWiki.File"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\.txt"; ValueType: string; ValueName: ""; ValueData: "LLMWiki.File"
Root: HKCU; Subkey: "Software\Classes\.docx"; ValueType: string; ValueName: ""; ValueData: "LLMWiki.File"
Root: HKCU; Subkey: "Software\Classes\.xlsx"; ValueType: string; ValueName: ""; ValueData: "LLMWiki.File"
Root: HKCU; Subkey: "Software\Classes\LLMWiki.File\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppIcon}"
Root: HKCU; Subkey: "Software\Classes\LLMWiki.File\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""

[Run]
; 安装后首次运行
Filename: "{app}\{#MyAppExeName}"; Description: "启动 LLM Wiki"; Flags: nowait postinstall skipifsilent; WorkingDir: "{app}"

[UninstallDelete]
Type: filesandordirs; Name: "{app}\wikis"

[Code]
// 检查是否已安装新版本，若版本更高则跳过安装
function InitializeSetup(): Boolean;
var
  InstalledVersion: String;
begin
  Result := True;
  
  // 读取已安装版本
  if RegQueryString(HKCU, 'Software\LLMWiki\Install', 'Version', InstalledVersion) then
  begin
    if CompareStr(InstalledVersion, '{#MyAppVersion}') >= 0 then
    begin
      // 已安装相同或更新版本
      case MsgBox('本地已安装 LLM Wiki v' + InstalledVersion + '，当前安装包版本 v{#MyAppVersion}。是否覆盖安装（会保留知识库数据）？', mbConfirmation, MB_YESNO) of
        IDYES: Result := True;
        IDNO:  Result := False;
      end;
    end;
  end;
end;

// 安装后弹出使用说明
function InitializeUninstall(): Boolean;
begin
  Result := True;
  if MsgBox('确定要卸载 LLM Wiki？（知识库文件位于 %APPDATA%\LLM Wiki\wikis，不会被删除）', mbConfirmation, MB_YESNO) = IDNO then
    Result := False;
end;
