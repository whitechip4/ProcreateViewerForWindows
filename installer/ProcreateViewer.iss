; Inno Setup script for ProcreateViewer.
; Build:  ISCC.exe /DAppVersion=1.0.0 installer\ProcreateViewer.iss   (after build.cmd and thumb\compile.cmd)
; Installs the viewer + the Explorer thumbnail handler, associates .procreate, and undoes all of it on uninstall.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#define AppName "ProcreateViewer"
#define ThumbClsid "{{7C1E7A6B-2F0D-4B39-9C55-5B1A2D3E4F60}"
#define ThumbHandlerKey "{{E357FCCD-A995-4576-B01F-234630154E96}"

[Setup]
AppId={{B7F3D6A2-5C41-4E0B-9A7D-2F6C1E8D9A10}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=whitechip4
AppPublisherURL=https://github.com/whitechip4/ProcreateViewerForWindows
AppSupportURL=https://github.com/whitechip4/ProcreateViewerForWindows/issues
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
UninstallDisplayIcon={app}\ProcreateViewer.exe
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
PrivilegesRequired=admin
ChangesAssociations=yes
OutputDir=output
OutputBaseFilename={#AppName}-Setup-{#AppVersion}
SetupIconFile=..\assets\app.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
LicenseFile=..\LICENSE

[Languages]
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
japanese.TaskAssoc=.procreate ファイルをこのビューアで開く
english.TaskAssoc=Open .procreate files with this viewer
japanese.TaskThumbs=エクスプローラにサムネイルを表示する
english.TaskThumbs=Show thumbnails in Explorer
japanese.TaskGroup=関連付け:
english.TaskGroup=Associations:
japanese.VerbOpen=Procreate Viewer で開く
english.VerbOpen=Open with Procreate Viewer
japanese.VerbExport=レイヤーを PNG で書き出し
english.VerbExport=Export layers as PNG
japanese.RegThumb=サムネイルハンドラを登録しています...
english.RegThumb=Registering the thumbnail handler...
japanese.Launch={#AppName} を起動
english.Launch=Launch {#AppName}

[Tasks]
Name: "assoc"; Description: "{cm:TaskAssoc}"; GroupDescription: "{cm:TaskGroup}"
Name: "thumbs"; Description: "{cm:TaskThumbs}"; GroupDescription: "{cm:TaskGroup}"

[Files]
Source: "..\bin\ProcreateViewer.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\thumb\ProcreateThumb.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\ProcreateViewer.exe"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"

[Registry]
; file type (HKLM\Software\Classes, seen through HKCR)
Root: HKLM; Subkey: "Software\Classes\.procreate"; ValueType: string; ValueName: ""; ValueData: "Procreate.Document"; Flags: uninsdeletevalue; Tasks: assoc
Root: HKLM; Subkey: "Software\Classes\.procreate"; ValueType: string; ValueName: "PerceivedType"; ValueData: "image"; Flags: uninsdeletevalue; Tasks: assoc
Root: HKLM; Subkey: "Software\Classes\Procreate.Document"; ValueType: string; ValueName: ""; ValueData: "Procreate Document"; Flags: uninsdeletekey; Tasks: assoc
Root: HKLM; Subkey: "Software\Classes\Procreate.Document\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\ProcreateViewer.exe"",0"; Tasks: assoc
Root: HKLM; Subkey: "Software\Classes\Procreate.Document\shell\open"; ValueType: string; ValueName: "MUIVerb"; ValueData: "{cm:VerbOpen}"; Tasks: assoc
Root: HKLM; Subkey: "Software\Classes\Procreate.Document\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\ProcreateViewer.exe"" ""%1"""; Tasks: assoc
Root: HKLM; Subkey: "Software\Classes\Procreate.Document\shell\exportpng"; ValueType: string; ValueName: "MUIVerb"; ValueData: "{cm:VerbExport}"; Tasks: assoc
Root: HKLM; Subkey: "Software\Classes\Procreate.Document\shell\exportpng\command"; ValueType: string; ValueName: ""; ValueData: """{app}\ProcreateViewer.exe"" --export ""%1"" ""%1_layers"""; Tasks: assoc
; Explorer thumbnail handler (the COM class itself is registered by RegAsm in [Run])
Root: HKLM; Subkey: "Software\Classes\.procreate\shellex\{#ThumbHandlerKey}"; ValueType: string; ValueName: ""; ValueData: "{#ThumbClsid}"; Flags: uninsdeletekey; Tasks: thumbs
Root: HKLM; Subkey: "Software\Classes\Procreate.Document\shellex\{#ThumbHandlerKey}"; ValueType: string; ValueName: ""; ValueData: "{#ThumbClsid}"; Flags: uninsdeletekey; Tasks: thumbs
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved"; ValueType: string; ValueName: "{#ThumbClsid}"; ValueData: "Procreate Thumbnail Provider"; Flags: uninsdeletevalue; Tasks: thumbs

[Run]
Filename: "{dotnet4064}\RegAsm.exe"; Parameters: "/codebase ""{app}\ProcreateThumb.dll"""; Flags: runhidden; StatusMsg: "{cm:RegThumb}"; Tasks: thumbs
Filename: "{app}\ProcreateViewer.exe"; Description: "{cm:Launch}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{dotnet4064}\RegAsm.exe"; Parameters: "/unregister ""{app}\ProcreateThumb.dll"""; Flags: runhidden; RunOnceId: "UnregThumb"
