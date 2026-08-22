; -- setup.iss --
; TNDrop インストーラー (Inno Setup 7)
; ユーザー単位 (管理者権限不要) インストール。本番機はオフライン x64 Windows のため、
; [Files] は dotnet publish の self-contained 出力フォルダ (dist\publish) を丸ごと同梱する。
#define MyAppName       "TNDrop"
#define MyAppVersion    "1.2.1"
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

[CustomMessages]
; 実行中の TNDrop を検出したときの確認ダイアログと、終了に失敗した場合のメッセージ。
; [Code] の InitializeSetup / InitializeUninstall から CustomMessage() 経由で参照する。
japanese.AppRunningInstallPrompt=TNDrop が実行中です。終了してインストールを続行しますか?
english.AppRunningInstallPrompt=TNDrop is currently running. Close it and continue with the installation?
japanese.AppRunningUninstallPrompt=TNDrop が実行中です。終了してアンインストールを続行しますか?
english.AppRunningUninstallPrompt=TNDrop is currently running. Close it and continue with the uninstallation?
japanese.AppCloseFailed=TNDrop を終了できませんでした。手動で終了してから再実行してください。
english.AppCloseFailed=TNDrop could not be closed. Please close it manually, then run this again.

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

[Code]
// TNDrop はタスクバーに現れないトレイ常駐アプリのため、実行中に検出しても Inno Setup 標準の
// 「閉じてください」ダイアログでは利用者が閉じ方に迷って行き止まりになる (v1.2.1)。
// そこでインストール/アンインストールの両方で: 実行中を検出したら確認ダイアログを出し、
// 承諾されたらインストーラー自身が閉じる (正常終了優先 + 強制終了フォールバック)。
// 上の AppMutex 行は安全網として残す ([Code] をすり抜けても標準ダイアログで止まり、
// 閉じずに上書きされる事故は起きない)。
const
  ShutdownEventName = 'Local\TNDrop_ShutdownRequest';
  SingleInstanceMutexName = 'Local\TNDrop_SingleInstance';
  EVENT_MODIFY_STATE = $0002;
  AppCloseMaxPollIterations = 20;
  AppClosePollIntervalMs = 250;

function OpenEventW(dwDesiredAccess: DWORD; bInheritHandle: BOOL; lpName: String): THandle;
  external 'OpenEventW@kernel32.dll stdcall';
function SetEvent(hEvent: THandle): BOOL;
  external 'SetEvent@kernel32.dll stdcall';
function CloseHandle(hObject: THandle): BOOL;
  external 'CloseHandle@kernel32.dll stdcall';

function IsTNDropRunning(): Boolean;
begin
  Result := CheckForMutexes(SingleInstanceMutexName);
end;

// 旧版 (v1.2.0 以前) にはイベントが存在しないため OpenEventW は 0 を返す。その場合は
// 何もせず戻り、呼び出し側の taskkill フォールバックに委ねる。
procedure SignalGracefulShutdown();
var
  hEvent: THandle;
begin
  hEvent := OpenEventW(EVENT_MODIFY_STATE, False, ShutdownEventName);
  if hEvent <> 0 then
  begin
    SetEvent(hEvent);
    CloseHandle(hEvent);
  end;
end;

// Mutex が消えるまで最大 MaxIterations 回、AppClosePollIntervalMs 間隔でポーリングする。
function WaitForTNDropToClose(MaxIterations: Integer): Boolean;
var
  i: Integer;
begin
  Result := not IsTNDropRunning();
  i := 0;
  while (not Result) and (i < MaxIterations) do
  begin
    Sleep(AppClosePollIntervalMs);
    Result := not IsTNDropRunning();
    i := i + 1;
  end;
end;

function ForceKillTNDrop(): Boolean;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM TNDrop.exe', '', SW_HIDE,
    ewWaitUntilTerminated, ResultCode);
  Result := WaitForTNDropToClose(AppCloseMaxPollIterations);
end;

// 正常終了 (イベント合図 → ポーリング) → 失敗したら taskkill フォールバック → 再ポーリング。
function TryCloseRunningApp(): Boolean;
begin
  SignalGracefulShutdown();
  Result := WaitForTNDropToClose(AppCloseMaxPollIterations);
  if not Result then
    Result := ForceKillTNDrop();
end;

// 実行中でなければ何もせず True。実行中なら確認 → はい以外/終了失敗で False (中止)。
function ConfirmAndCloseRunningApp(const PromptMessage: String): Boolean;
begin
  Result := True;
  if not IsTNDropRunning() then
    Exit;

  if MsgBox(PromptMessage, mbConfirmation, MB_YESNO) = IDNO then
  begin
    Result := False;
    Exit;
  end;

  if not TryCloseRunningApp() then
  begin
    MsgBox(CustomMessage('AppCloseFailed'), mbError, MB_OK);
    Result := False;
  end;
end;

function InitializeSetup(): Boolean;
begin
  Result := ConfirmAndCloseRunningApp(CustomMessage('AppRunningInstallPrompt'));
end;

function InitializeUninstall(): Boolean;
begin
  Result := ConfirmAndCloseRunningApp(CustomMessage('AppRunningUninstallPrompt'));
end;
