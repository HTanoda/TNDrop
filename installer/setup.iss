; -- setup.iss --
; TNDrop インストーラー (Inno Setup 7)
; ユーザー単位 (管理者権限不要) インストール。本番機はオフライン x64 Windows のため、
; [Files] は dotnet publish の self-contained 出力フォルダ (dist\publish) を丸ごと同梱する。
#define MyAppName       "TNDrop"
#define MyAppVersion    "1.2.0"
#define MyAppPublisher  "HIROKI TANODA (TND)"
#define MyAppCopyright  "Copyright (c) 2026 HIROKI TANODA (TND). All rights reserved."
#define MyAppExeName    "TNDrop.exe"
#define MyAppURL        ""

[Setup]
; AppId はこのプロジェクト専用に新規生成した GUID で固定する。
; 一度配布したら絶対に変更しないこと (変更すると旧版が孤立し二重インストールになる)。
AppId={{84441471-E396-4814-9466-C42BD443880E}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppCopyright={#MyAppCopyright}
AppVerName={#MyAppName} {#MyAppVersion}
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=TNDrop-Setup-{#MyAppVersion}
SetupIconFile=..\assets\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2
SolidCompression=yes
; 32-bit インストーラー本体を維持する (確定方針)。x64compatible で 64-bit モードインストールは可能。
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; ユーザー単位・管理者権限不要インストール (確定方針)
PrivilegesRequired=lowest
; アプリが保持する単一インスタンス Mutex と同じ名前を指定する。
; インストール/アンインストール前に実行中の TNDrop 検出時、Inno Setup が標準の
; 「実行中のアプリを閉じてください」ダイアログを表示し、閉じるまで先へ進めない。
AppMutex=Local\TNDrop_SingleInstance
WizardStyle=modern

[Languages]
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
; dotnet publish (Release, win-x64, self-contained) の出力フォルダを丸ごと同梱する。
; ランタイム込みで完結させる (本番機オフライン・ランタイム未インストール前提)。
Source: "..\dist\publish\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Run]
; インストール直後の自動起動は「起動する」チェックボックス経由のみ (postinstall)。
; 自動起動 (Windows ログイン時) の登録はアプリ内部の設定 (HKCU Run) が担当するため、
; インストーラー側では Run セクションに常駐起動用のエントリを一切追加しない。
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

; アンインストール時、利用者のクリップボード履歴 (%APPDATA%\TNDrop) は削除しない。
; [Files] が %APPDATA% 配下に一切書き込んでいないため、既定の動作のまま何もしなくてよい
; (Inno Setup のアンインストーラーは自分がインストールしたファイルしか削除しない)。
; 誤って削除するコードを将来追加しないこと。
