; Inno Setup script for the installer. Build it with tools\Build-Installer.ps1 (it passes the defines below).
;
; The installer is per user by default (no administrator rights, installs under %LOCALAPPDATA%\Programs), puts a
; shortcut in the Start menu, and can be uninstalled from "Apps". The user's data (index, history, AI model) is
; in %LOCALAPPDATA%\EmbeddingSearchViewer and is only removed if the user agrees when uninstalling.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef NumericVersion
  #define NumericVersion "0.0.0.0"
#endif
#ifndef AppDir
  #error AppDir (the folder with EmbeddingSearchViewer.App.exe) is required.
#endif
#ifndef DocsDir
  #error DocsDir (the folder with README.md, LICENSE, THIRD-PARTY-NOTICES.md and licenses) is required.
#endif
#ifndef OutputDir
  #define OutputDir "."
#endif

#define AppName "EmbeddingSearchViewer"
#define AppExe "EmbeddingSearchViewer.App.exe"

[Setup]
; Never change this GUID: it is how an upgrade finds the installed version.
AppId={{6F0B3B9C-2C1D-4F5B-9B38-5E1B7B0C9D21}
AppName={#AppName}
AppVersion={#AppVersion}
VersionInfoVersion={#NumericVersion}
AppPublisher=SoraKumo
AppPublisherURL=https://github.com/SoraKumo001/embedding-search-viewer
AppSupportURL=https://github.com/SoraKumo001/embedding-search-viewer/issues
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename={#AppName}-{#AppVersion}-win-x64-setup
LicenseFile={#DocsDir}\LICENSE
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes

[Languages]
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#AppDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#DocsDir}\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#DocsDir}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#DocsDir}\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#DocsDir}\licenses\*"; DestDir: "{app}\licenses"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[CustomMessages]
japanese.RemoveDataPrompt=インデックス、検索履歴、ダウンロードした AI モデル（約 1.8 GB）も削除しますか？%n%n%1%n%n「いいえ」を選ぶと、残します（元の画像・PDF・動画は、どちらを選んでも、削除しません）。
english.RemoveDataPrompt=Also delete the index, the search history and the downloaded AI model (about 1.8 GB)?%n%n%1%n%nChoose "No" to keep them. Your original images, PDFs and videos are never deleted.

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep <> usPostUninstall then Exit;

  DataDir := ExpandConstant('{localappdata}\{#AppName}');
  if (not UninstallSilent) and DirExists(DataDir) then
  begin
    // The default answer is "No": the AI model takes a long time to download again.
    if MsgBox(FmtMessage(CustomMessage('RemoveDataPrompt'), [DataDir]), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      DelTree(DataDir, True, True, True);
  end;
end;
