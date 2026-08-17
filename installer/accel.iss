; Accel installer script (Inno Setup 6, https://jrsoftware.org/isinfo.php).
;
; Built by publish.ps1 via ISCC.exe, always AFTER `dotnet publish` has produced the self-contained
; single-file win-x64 build - this script only packages that existing output, it never builds the
; app itself. MyAppVersion is passed in from the command line (publish.ps1 reads it out of
; accel.csproj's <Version>, the single source of truth for Accel's own version - see
; App/Controls/AppVersionInfo.cs) rather than hand-maintained here, so the two can never drift.
;
; AppId is a fixed, generated-once GUID - Inno Setup uses it to recognize "this is the same app"
; across versions for upgrade/uninstall purposes; it must never change once released.
#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif
#define MyAppName "Accel"
#define MyAppPublisher "Accel"
#define MyAppExeName "accel.exe"
#define PublishDir "..\bin\Release\net8.0-windows\win-x64\publish"

[Setup]
AppId={{608061A1-FB14-49A3-837C-4C895D592225}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=Accel-Setup-{#MyAppVersion}
SetupIconFile=..\App\accel.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
WizardStyle=modern
; accel.exe is already a self-contained, self-extracting single file (~180 MB) - no .NET runtime
; prerequisite step is needed here.

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
; The published exe and its one runtime-loaded asset folder (WebView2's terminal panel - see
; TerminalView.xaml.cs's SetVirtualHostNameToFolderMapping, and accel.csproj's comment on why these
; are loose Content files rather than embedded resources). Debug symbols (.pdb), the WebView2 XML
; doc comments, and the build-time global.json/web.config are deliberately not packaged - none of
; them are read at runtime by a self-contained exe.
Source: "{#PublishDir}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishDir}\wwwroot\xterm\*"; DestDir: "{app}\wwwroot\xterm"; Flags: ignoreversion recursesubdirs createallsubdirs
; The default (empty-array) root-folders fallback - see CLAUDE_ENV.md's "Folder Config Search
; Order". onlyifdoesntexist so upgrading an existing install never clobbers a user's own edits to
; this fallback file (their real per-user config normally lives under
; %USERPROFILE%\.claude\accel-folders.json instead, which this installer never touches).
Source: "{#PublishDir}\folder.json"; DestDir: "{app}"; Flags: onlyifdoesntexist

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}\wwwroot"
