# TNDrop GitHub README アニメーション画像 実装計画

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development. Task A 単独。

**Goal:** GitHub の README.md 冒頭に、README.html のモックアップと同じ見た目の「動くイメージ」(アニメ画像) を掲載する (ユーザー承認: 方式 A = 実ページの連続撮影からアニメ画像化)。

## Global Constraints (継承)

- 画像はリポジトリ内 (`docs/images/`) に置き、README.md からは相対パスで参照 (外部ホスティングなし)
- アプリ本体・インストーラー・バージョンには触らない (docs のみ)。テストは不変 (423)
- 主張には実行証拠必須 / コミット末尾 Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
- 実プロファイル・実クリップボード禁止

### Task A: アニメ画像の生成 + README.md 更新

- 撮影対象: assets/README.html のモックアップ図版のうち (1) ヒーロー (シェルフのスライドイン)、(2) グループ化→解除、(3) クリック貼り付け (またはコピー→棚に積まれる) の 2〜3 点。図版要素だけをクロップして撮る (ページ全体ではない)。
- 手順: headless Edge (`msedge --headless --screenshot`) で一定間隔のフレームを取得 → 各フレームを図版の矩形でクロップ → Pillow (uv run python) でアニメ GIF (必要なら WebP も) に結合。ループ 1 周分 (README.html の各アニメの周期に合わせる)。フレームレート 10〜15fps、1 枚あたり目安 ≤1.5MB (サイズ実測を報告)。
- アニメの位相合わせ: README.html のアニメは負の animation-delay 注入や `Animation.currentTime` 相当の手段で、フレーム i の時刻を決定的に指定して撮る (壁時計任せで撮ると周期がずれる)。
- 出力: `docs/images/hero.gif` 等、ASCII ファイル名。README.md の冒頭 (1 行説明の直後) にヒーロー GIF、機能セクション付近に残りを配置。alt テキスト (日本語) を付ける。ダーク背景の画像なので README 上で浮かないよう、画像幅は最大 720px 程度に指定 (`<img width=...>`)。
- 検証: 生成した GIF を Pillow で読み戻しフレーム数・寸法・ループ設定を出力 / README.md を GitHub 互換 Markdown としてレンダリング確認 (相対パスが正しいか、`git ls-files` で画像が追跡されるか)。
- コミット (docs のみ)。**プッシュはコントローラー担当。**
