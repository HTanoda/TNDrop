# TNDrop プロジェクト固有メモ

全体規約は `~/.claude/CLAUDE.md` (業務アプリ開発ガイド) に従う。本ファイルは §9 の確定値のみ記す。

- プロジェクト名: TNDrop (画面端シェルフ型クリップボードマネージャー)
- 想定利用者: 庁内職員 (オフライン・イントラ環境、管理者権限なしの一般 PC)
- 採用ランタイム: .NET 10 (LTS) self-contained / win-x64 (本番機はランタイム未導入)
- 採用 UI 形態: WPF デスクトップ常駐 (トレイ + 画面端シェルフ)。WinForms は NotifyIcon のみ
- 採用 DB / データ保存方式: `%APPDATA%\TNDrop\` — items.dat (DPAPI 暗号化 JSON + .bak)、blobs\ (画像 PNG)、settings.json、logs\
- 配布方式: Inno Setup 7、ユーザー単位 (`PrivilegesRequired=lowest`)、`build.ps1` で test→publish→ISCC。AppId は installer/setup.iss の GUID を変更しないこと
- NuGet 方針: アプリ本体は System.Security.Cryptography.ProtectedData のみ。テストは xunit 系 + Xunit.StaFact
- 設計書: docs/superpowers/specs/2026-08-20-tndrop-v1-design.md (v1)、docs/superpowers/specs/2026-08-22-tndrop-v1.2.1-installer-close-design.md (v1.2.1)、docs/superpowers/specs/2026-08-22-tndrop-v1.3-design.md (v1.3)、docs/superpowers/specs/2026-08-22-tndrop-v1.3.1-readme-design.md (v1.3.1)、docs/superpowers/specs/2026-08-24-tndrop-v1.5-indicator-visibility-design.md (v1.5) / 実装計画: docs/superpowers/plans/2026-08-20-tndrop-v1.md (v1)、docs/superpowers/plans/2026-08-21-tndrop-v1.1.md (v1.1)、docs/superpowers/plans/2026-08-21-tndrop-v1.2.md (v1.2)、docs/superpowers/plans/2026-08-22-tndrop-v1.2.1.md (v1.2.1)、docs/superpowers/plans/2026-08-22-tndrop-v1.3.md (v1.3)、docs/superpowers/plans/2026-08-22-tndrop-v1.3.1.md (v1.3.1)、docs/superpowers/plans/2026-08-24-tndrop-v1.5-indicator-visibility.md (v1.5)
- リリース前必須: docs/manual-test-checklist.md の未実施項目 (特に [必須] 非管理者アカウントでのインストール、実マウスのドラッグ&ドロップ、スリープ復帰、フルスクリーン) を実機で消化すること
