; Yuyue (语阅) Windows Setup Script — Inno Setup 6
; Build via: .\windows\package-release.ps1 -Version 0.1.0 -BuildInstaller

#ifndef MyAppVersion
  #define MyAppVersion "0.1.0"
#endif
#ifndef MyArch
  #define MyArch "x64"
#endif
#ifndef StageDir
  #define StageDir "dist\Yuyue-0.1.0-windows-x64"
#endif
#ifndef OutDir
  #define OutDir "dist"
#endif

[Setup]
AppId={{9A6B5C2E-7D41-4F8B-A123-7C8E91F02B44}
AppName=语阅 Yuyue
AppVersion={#MyAppVersion}
AppPublisher=Yuyue
DefaultDirName={autopf}\Yuyue
DefaultGroupName=语阅
AllowNoIcons=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir={#OutDir}
OutputBaseFilename=Yuyue-{#MyAppVersion}-windows-{#MyArch}-Setup
SetupIconFile=Yuyue\Assets\AppIcon.ico
UninstallDisplayIcon={app}\Yuyue.exe
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ChangesAssociations=yes
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Default.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式:"
Name: "assoc_epub"; Description: "关联 .epub 电子书文件"; GroupDescription: "文件关联:"
Name: "assoc_mobi"; Description: "关联 .mobi / .azw3 电子书文件"; GroupDescription: "文件关联:"
Name: "assoc_m4b"; Description: "关联 .m4b 有声书文件"; GroupDescription: "文件关联:"; Flags: unchecked

[Files]
Source: "{#StageDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\语阅"; Filename: "{app}\Yuyue.exe"
Name: "{group}\卸载语阅"; Filename: "{uninstallexe}"
Name: "{autodesktop}\语阅"; Filename: "{app}\Yuyue.exe"; Tasks: desktopicon

[Registry]
; URL protocol yuyue:// (OAuth callbacks & deep links)
Root: HKA; Subkey: "Software\Classes\yuyue"; ValueType: string; ValueName: ""; ValueData: "URL:Yuyue Protocol"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\yuyue"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\yuyue\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\Yuyue.exe"" ""%1"""

; File associations (.epub / .mobi / .azw3 / .m4b)
Root: HKA; Subkey: "Software\Classes\Yuyue.Book"; ValueType: string; ValueName: ""; ValueData: "语阅电子书"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\Yuyue.Book\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\Yuyue.exe"",0"
Root: HKA; Subkey: "Software\Classes\Yuyue.Book\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\Yuyue.exe"" ""%1"""

Root: HKA; Subkey: "Software\Classes\.epub\OpenWithProgids"; ValueType: string; ValueName: "Yuyue.Book"; ValueData: ""; Tasks: assoc_epub; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\.mobi\OpenWithProgids"; ValueType: string; ValueName: "Yuyue.Book"; ValueData: ""; Tasks: assoc_mobi; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\.azw3\OpenWithProgids"; ValueType: string; ValueName: "Yuyue.Book"; ValueData: ""; Tasks: assoc_mobi; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\.m4b\OpenWithProgids"; ValueType: string; ValueName: "Yuyue.Book"; ValueData: ""; Tasks: assoc_m4b; Flags: uninsdeletevalue

[Run]
Filename: "{app}\Yuyue.exe"; Description: "启动语阅"; Flags: nowait postinstall skipifsilent
