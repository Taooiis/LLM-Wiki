; Inno Setup 脚本 - LLM Wiki 安装包
; 用法：ISCC LlmWiki.iss

[Setup]
AppName=LLM Wiki
AppVersion=1.0.0
AppPublisher=LLM Wiki 项目
AppCopyright=© 2026 LLM Wiki 项目
AppMutex=Local\LlmWiki.App.SingleInstance
DefaultDirName={autopf}\LLM Wiki
DefaultGroupName=LLM Wiki
OutputBaseName=LLM Wiki
Compression=lzma
SolidCompression=yes
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
SetupIconFile=code\icons\app.ico
WizardStyle=modern

[Files]
Source: "code\bin\Release\net10.0-windows\Win-X64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{autoprograms}\LLM Wiki"; Filename: "{app}\LlmWiki.App.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\LLM Wiki"; Filename: "{app}\LlmWiki.App.exe"; WorkingDir: "{app}"; Tasks: desktopIcon

[Tasks]
Name: desktopIcon; Description: 创建桌面快捷方式; GroupDescription: 额外选项:;
Name: startMenu; Description: 创建开始菜单快捷方式; GroupDescription: 额外选项:;

[Registry]
Root: HKCU; Subkey: "Software\LLMWiki\Install"; ValueType: string; ValueName: "Version"; ValueData: "{#AppVersion}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\.md"; ValueType: string; ValueName: "". ValueData: "LLMWiki.File"; Flags: noregistrycheck
Root: HKCU; Subkey: "Software\Classes\.txt"; ValueType: string; ValueName: "". ValueData: "LLMWiki.File"; Flags: noregistrycheck
Root: HKCU; Subkey: "Software\Classes\.docx"; ValueType: string; ValueName: "". ValueData: "LLMWiki.File"; Flags: noregistrycheck
Root: HKCU; Subkey: "Software\Classes\.xlsx"; ValueType: string; ValueName: "". ValueData: "LLMWiki.File"; Flags: noregistrycheck

[Types]
Name: "full"; Description: "完整安装"; Flags: isfavorite
Name: "compact"; Description: "最小安装"; Flags: isdefault

[Components]
Name: "app"; Description: "LLM Wiki 本体"; Types: full compact
Name: "fileassoc"; Description: "文件关联"; Types: full

[Code]
function InitializeSetup(): Boolean;
begin
  Result := True;
  
  // 检查 WebView2 运行时
  if Not RegKeyExists(HKEY_LOCAL_MACHINE, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}') then
  begin
    if MsgBox('未检测到 Microsoft Edge WebView2 运行时。'+#13#10+
              '安装程序将尝试下载并安装它。'+#13#10#13#10+
              '是否继续下载安装？', mbConfirmation, MB_YESNO) = IDNO then
      Result := False;
  end;
end;