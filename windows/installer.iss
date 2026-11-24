; Yuyue (语阅) Windows Setup Script — Inno Setup 6 (UTF-8 with BOM)
; Build via: .\windows\package-release.ps1 -Version 0.1.0 -BuildNumber 1 -BuildInstaller

#ifndef MyAppVersion
  #define MyAppVersion "0.1.2"
#endif
#ifndef MyBuildNumber
  #define MyBuildNumber "4"
#endif
#ifndef MyArch
  #define MyArch "x64"
#endif
#ifndef StageDir
  #define StageDir "dist\Yuyue-0.1.2-windows-x64"
#endif
#ifndef OutDir
  #define OutDir "dist"
#endif

[Setup]
AppId={{9A6B5C2E-7D41-4F8B-A123-7C8E91F02B44}
AppName=语阅
AppVerName=语阅 {#MyAppVersion}
AppVersion={#MyAppVersion}
VersionInfoVersion={#MyAppVersion}.{#MyBuildNumber}
AppPublisher=Yuyue
AppPublisherURL=https://github.com/inokoru/yuyue
AppSupportURL=https://github.com/inokoru/yuyue/issues
DefaultDirName={autopf}\Yuyue
DefaultGroupName=语阅
UninstallDisplayName=语阅 (Yuyue)
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
#if MyArch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
CloseApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式 / Create a desktop shortcut"; GroupDescription: "快捷方式 / Shortcuts:"
Name: "assoc_epub"; Description: "关联 .epub 电子书文件 / Register .epub ebooks"; GroupDescription: "文件关联 / File associations:"
Name: "assoc_mobi"; Description: "关联 .mobi / .azw3 电子书文件 / Register .mobi & .azw3 ebooks"; GroupDescription: "文件关联 / File associations:"
Name: "assoc_pdf"; Description: "在「打开方式」中添加 .pdf 支持 / Add to Open With for .pdf"; GroupDescription: "文件关联 / File associations:"; Flags: unchecked
Name: "assoc_m4b"; Description: "关联 .m4b 有声书文件 / Register .m4b audiobooks"; GroupDescription: "文件关联 / File associations:"; Flags: unchecked

[Files]
Source: "{#StageDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\语阅"; Filename: "{app}\Yuyue.exe"
Name: "{group}\卸载语阅"; Filename: "{uninstallexe}"
Name: "{autodesktop}\语阅"; Filename: "{app}\Yuyue.exe"; Tasks: desktopicon

[Registry]
; URL protocol yuyue:// (deep links & callbacks)
Root: HKA; Subkey: "Software\Classes\yuyue"; ValueType: string; ValueName: ""; ValueData: "URL:Yuyue Protocol"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\yuyue"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\yuyue\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\Yuyue.exe"" ""%1"""

; File associations (.epub / .mobi / .azw3 / .pdf / .m4b)
Root: HKA; Subkey: "Software\Classes\Yuyue.Book"; ValueType: string; ValueName: ""; ValueData: "语阅电子书"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\Yuyue.Book\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\Yuyue.exe"",0"
Root: HKA; Subkey: "Software\Classes\Yuyue.Book\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\Yuyue.exe"" ""%1"""

Root: HKA; Subkey: "Software\Classes\.epub\OpenWithProgids"; ValueType: string; ValueName: "Yuyue.Book"; ValueData: ""; Tasks: assoc_epub; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\.mobi\OpenWithProgids"; ValueType: string; ValueName: "Yuyue.Book"; ValueData: ""; Tasks: assoc_mobi; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\.azw3\OpenWithProgids"; ValueType: string; ValueName: "Yuyue.Book"; ValueData: ""; Tasks: assoc_mobi; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\.pdf\OpenWithProgids"; ValueType: string; ValueName: "Yuyue.Book"; ValueData: ""; Tasks: assoc_pdf; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\.m4b\OpenWithProgids"; ValueType: string; ValueName: "Yuyue.Book"; ValueData: ""; Tasks: assoc_m4b; Flags: uninsdeletevalue

[Run]
Filename: "{app}\Yuyue.exe"; Description: "启动语阅 / Launch Yuyue"; Flags: nowait postinstall skipifsilent

[Code]
function IsWebView2Installed(): Boolean;
var
  Ver: String;
begin
  Result :=
    (RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Ver) and (Ver <> '') and (Ver <> '0.0.0.0')) or
    (RegQueryStringValue(HKLM, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Ver) and (Ver <> '') and (Ver <> '0.0.0.0')) or
    (RegQueryStringValue(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Ver) and (Ver <> '') and (Ver <> '0.0.0.0'));
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ErrCode: Integer;
begin
  if (CurStep = ssPostInstall) and (not WizardSilent) and (not IsWebView2Installed()) then
  begin
    if MsgBox(
      '未检测到 Microsoft Edge WebView2 运行时（Windows 11 默认已带，部分 Windows 10 需安装一次）。'#13#10 +
      '是否立即打开微软官方页面下载安装？',
      mbConfirmation, MB_YESNO) = IDYES then
    begin
      ShellExec('open', 'https://go.microsoft.com/fwlink/p/?LinkId=2124703', '', '', SW_SHOWNORMAL, ewNoWait, ErrCode);
    end;
  end;
end;
