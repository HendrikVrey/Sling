; Sling - Inno Setup script
;
; Per-user, no elevation, no UAC prompt. Everything this writes lives under
; HKEY_CURRENT_USER and %LOCALAPPDATA%. Nothing here needs administrative rights, and
; nothing here affects another account on the machine.
;
; Build:  iscc installer\Sling.iss /DAppVersion=1.2.3
; Expects a published payload at:
;   publish\win-x64\Sling.exe
;   publish\win-arm64\Sling.exe
;
; One installer carries both architectures. It is a few megabytes larger than two
; downloads and it removes the only question a user cannot reliably answer about their
; own machine.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

; VersionInfoVersion must be strictly numeric (x.y.z[.w]); AppVersion may carry a
; pre-release suffix, and the tag glob v* lets one through. The workflow passes both.
#ifndef NumericVersion
  #define NumericVersion AppVersion
#endif

#define AppName        "Sling"
#define AppPublisher   "Hendrik Vrey"
#define AppUrl         "https://github.com/HendrikVrey/Sling"
#define AppExeName     "Sling.exe"

[Setup]
; Never change AppId. It is how Windows recognises an existing installation, and a new
; one turns every upgrade into a second entry in Apps & features. It is also deliberately
; different from Etch's, so installing one is never mistaken for upgrading the other.
AppId={{7C4E1B2A-9F63-4D18-A5E7-2B90C6F4A831}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#NumericVersion}

; Per-user throughout. PrivilegesRequired=lowest is what stops the UAC prompt; the
; install directory and the uninstall entry follow from it.
; No PrivilegesRequiredOverridesAllowed. Setting it to "dialog" would offer an "Install
; for all users (requires admin)" option, and taking it elevates Setup - at which point
; {localappdata} and every Root: HKCU below resolve against the ADMIN's account. The
; payload would land in another profile, the associations in another user's registry, and
; the invoking user would get shortcuts pointing at a directory they cannot see. Per-user
; is the whole posture here; it must not be an option that can be clicked away.
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes

; x64compatible matches Arm64 Windows too (it runs x64 under emulation), so this pair
; allows both and the [Files] entries below pick the right payload with IsArm64.
; Installing in 64-bit mode is not cosmetic: in 32-bit mode Windows would redirect
; Software\Classes writes into Wow6432Node, and the application - which is 64-bit - reads
; the unredirected path, so what this wizard wrote would be invisible to it.
ArchitecturesAllowed=x64compatible or arm64
ArchitecturesInstallIn64BitMode=x64compatible or arm64

OutputDir=..\dist
OutputBaseFilename=Sling-Setup
SetupIconFile=..\assets\sling.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
LicenseFile=..\LICENSE

; Sling holds the open document and, unlike Etch, does not save it continuously, so an
; upgrade over a running copy would fail on a locked file and put unsaved work at risk.
; The Restart Manager notices and asks; Sling's own close path asks about the document.
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
; Unchecked by default. Visual Studio 2022, Rider and the VS Code REST Client all read
; .http files, so a machine with Sling on it very likely has one of them holding the
; default already, and taking that over on a Next-Next-Finish is not a choice the user
; made. The ProgIDs below are registered either way, so Sling appears in Windows' own
; "Open with" list without this box being ticked.
Name: "assoc"; \
  Description: "Make {#AppName} the default for .http and .rest files"; \
  Flags: unchecked
Name: "desktopicon"; \
  Description: "{cm:CreateDesktopIcon}"; \
  Flags: unchecked

[Files]
; Excludes the symbols: they are useful to keep in the release assets, not to ship inside
; every installation.
Source: "..\publish\win-x64\*"; DestDir: "{app}"; \
  Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs; \
  Check: not IsArm64
Source: "..\publish\win-arm64\*"; DestDir: "{app}"; \
  Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs; \
  Check: IsArm64
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Registry]
; ---------------------------------------------------------------------------
; The ProgIDs. Written ALWAYS, whether or not the association task is ticked.
;
; This is the half that cannot fail at install time: registering a ProgID and listing it
; under the extension's OpenWithProgids puts Sling in Windows' own "Open with" submenu
; whatever the user has chosen as their default. On a machine where Visual Studio owns
; .http - the common case - that is the whole of what most users want, and it happens
; without displacing anything.
;
; The open command passes the file as %1, and App.OnStartup is what reads it. If that
; ever stops being true this association becomes a lie: Sling would launch and show a
; different document from the one that was double-clicked. src/Sling.App/App.xaml.cs
; carries the mirror-image comment. If you change one, change both.
; ---------------------------------------------------------------------------
Root: HKCU; Subkey: "Software\Classes\Sling.http"; ValueType: string; ValueName: ""; ValueData: "HTTP request file (Sling)"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Sling.http\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExeName},0"
Root: HKCU; Subkey: "Software\Classes\Sling.http\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExeName}"" ""%1"""

Root: HKCU; Subkey: "Software\Classes\Sling.rest"; ValueType: string; ValueName: ""; ValueData: "HTTP request file (Sling)"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Sling.rest\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExeName},0"
Root: HKCU; Subkey: "Software\Classes\Sling.rest\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExeName}"" ""%1"""

; ---------------------------------------------------------------------------
; OpenWithProgids. Also always.
;
; uninsdeletevalue, and NEVER uninsdeletekey. This key is a shared list that every
; application able to open the type adds itself to; deleting the key on uninstall would
; take Visual Studio's and the REST Client's entries with it.
; ---------------------------------------------------------------------------
Root: HKCU; Subkey: "Software\Classes\.http\OpenWithProgids"; ValueType: string; ValueName: "Sling.http"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.rest\OpenWithProgids"; ValueType: string; ValueName: "Sling.rest"; ValueData: ""; Flags: uninsdeletevalue

; There is deliberately no "Open with Sling" verb on the "any file" class. Etch has one
; because Etch opens anything; Sling reads one format, and an entry on every context menu
; in Explorer that would produce an unparseable document is noise rather than an
; affordance.

; The extension DEFAULTS are deliberately absent from this section. They are written in
; [Code] instead, because setting one has to preserve whatever it displaced - see
; RegisterDefault below.

[Code]

const
  SHCNE_ASSOCCHANGED = $08000000;
  SHCNF_IDLIST       = $0000;

  { Nothing inside Sling reads this. Unlike Etch there is no settings panel that
    withdraws an association, so it has exactly one reader: UnregisterDefault, at
    uninstall. }
  DisplacedValueName = 'SlingPreviousProgId';

procedure SHChangeNotify(wEventId: Integer; uFlags: Cardinal; dwItem1, dwItem2: Integer);
  external 'SHChangeNotify@shell32.dll stdcall';

function ExtensionCount: Integer;
begin
  Result := 2;
end;

function ExtensionAt(Index: Integer): String;
begin
  if Index = 0 then
    Result := '.http'
  else
    Result := '.rest';
end;

function ProgIdFor(Extension: String): String;
begin
  { '.http' becomes 'Sling.http' - the dot already in the extension is the separator. }
  Result := 'Sling' + Extension;
end;

{
  Points an extension at Sling's ProgID, keeping whatever it displaced.

  The stash is what makes uninstalling safe. Without it, removing Sling from a machine
  where Visual Studio had owned .http would delete the extension's default outright,
  losing an association the user had before Sling was ever installed.

  Never writes UserChoice. That key is hash-protected, writing it is unsupported, and it
  is a thing malware does. On a machine that already has a user-chosen default for the
  extension this procedure therefore correctly has no visible effect, and the change is
  finished in Settings, Apps, Default apps. The README says so rather than leaving a user
  to conclude the installer failed.
}
procedure RegisterDefault(Extension: String);
var
  ExtensionKey, ProgId, Displaced, AlreadyStashed: String;
begin
  ExtensionKey := 'Software\Classes\' + Extension;
  ProgId := ProgIdFor(Extension);

  if not RegQueryStringValue(HKCU, ExtensionKey, '', Displaced) then
    Displaced := '';

  { Only when it names something else, and only when there is not already one recorded -
    reinstalling must not overwrite the original with Sling's own ProgID and turn the
    restore into a no-op. }
  if (Displaced <> '') and (CompareText(Displaced, ProgId) <> 0) then
    if not RegQueryStringValue(HKCU, 'Software\Classes\' + ProgId, DisplacedValueName, AlreadyStashed) then
      RegWriteStringValue(HKCU, 'Software\Classes\' + ProgId, DisplacedValueName, Displaced);

  RegWriteStringValue(HKCU, ExtensionKey, '', ProgId);
end;

{
  Puts back what Sling displaced, or removes the default if there was nothing.

  The guard that matters most: the value is touched only while it still names Sling. If
  the user has since chosen another client, that choice is theirs and uninstalling Sling
  must not undo it.
}
procedure UnregisterDefault(Extension: String);
var
  ExtensionKey, ProgId, Current, Displaced: String;
begin
  ExtensionKey := 'Software\Classes\' + Extension;
  ProgId := ProgIdFor(Extension);

  if not RegQueryStringValue(HKCU, ExtensionKey, '', Current) then
    Exit;

  if CompareText(Current, ProgId) <> 0 then
    Exit;

  if RegQueryStringValue(HKCU, 'Software\Classes\' + ProgId, DisplacedValueName, Displaced)
     and (Displaced <> '') then
    RegWriteStringValue(HKCU, ExtensionKey, '', Displaced)
  else
    RegDeleteValue(HKCU, ExtensionKey, '');
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  I: Integer;
begin
  if CurStep <> ssPostInstall then
    Exit;

  if WizardIsTaskSelected('assoc') then
    for I := 0 to ExtensionCount - 1 do
      RegisterDefault(ExtensionAt(I));

  { Without this, Explorer keeps showing the old icon and the old handler until it is
    restarted, which looks exactly like the installer not having worked. }
  SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, 0, 0);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  I: Integer;
begin
  { usUninstall, before the [Registry] entries are removed: UnregisterDefault has to read
    the stashed value off Sling's ProgID key, and uninsdeletekey is about to take that key
    away. }
  if CurUninstallStep <> usUninstall then
    Exit;

  for I := 0 to ExtensionCount - 1 do
    UnregisterDefault(ExtensionAt(I));
end;

procedure DeinitializeUninstall();
begin
  SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, 0, 0);
end;
