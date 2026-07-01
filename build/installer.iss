; Inno Setup 脚本 —— 把 self-contained 发布文件夹（dist\app）打包成 dist\install.exe。
; 由 build\pack.ps1 调用（先 publish 再 iscc）。图标为仓库根 icon.png 转出的 build\icon.ico。
; 编译：ISCC.exe build\installer.iss  （通常在 "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"）

#define AppDisplayName "Re:Zero 命运全书"
#define AppVersionStr "1.0"
#define AppExeName "Re0Agent.App.exe"

[Setup]
AppName={#AppDisplayName}
AppVersion={#AppVersionStr}
AppPublisher=Re0Agent
DefaultDirName={autopf}\Re0Agent
DefaultGroupName=Re0Agent
DisableProgramGroupPage=yes
UninstallDisplayName={#AppDisplayName}
UninstallDisplayIcon={app}\{#AppExeName}
SetupIconFile=icon.ico
OutputDir=..\dist
OutputBaseFilename=install
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
PrivilegesRequired=admin

[Languages]
; 用内置英文向导框架（各机器都有），界面上的任务/快捷方式名仍是中文（见下）。
; 若你的 Inno Setup 装了简中语言包，可改为 MessagesFile: "compiler:Languages\ChineseSimplified.isl"。
Name: "default"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加图标:"

[Files]
; 递归拷贝整个 publish 文件夹（exe + 自带 .NET 运行时 + wwwroot + 依赖）。
Source: "..\dist\app\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\{#AppDisplayName}"; Filename: "{app}\{#AppExeName}"; IconFilename: "{app}\{#AppExeName}"
Name: "{group}\卸载 {#AppDisplayName}"; Filename: "{uninstallexe}"
Name: "{userdesktop}\{#AppDisplayName}"; Filename: "{app}\{#AppExeName}"; IconFilename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "立即启动 {#AppDisplayName}"; Flags: nowait postinstall skipifsilent
