# ProcreateViewer for Windows

Procreate の `.procreate` ファイルを Windows で開くビューアです。レイヤーごとの表示切替、原寸表示、
PNG / レイヤー付き PSD の書き出しができ、エクスプローラにサムネイルも表示します。

A viewer for Procreate (`.procreate`) files on Windows: layers and groups, full-resolution view,
PNG and layered PSD export, Explorer thumbnails.

## ダウンロード / Download

**[Releases](https://github.com/whitechip4/ProcreateViewerForWindows/releases)** から
`ProcreateViewer-Setup-x.y.z.exe` をダウンロードしてください。

## インストール / Install

1. `ProcreateViewer-Setup-x.y.z.exe` を実行します。署名していないため Windows の SmartScreen が
   「発行元不明」と警告する場合は「詳細情報」→「実行」で進めてください。
2. インストーラが `.procreate` の関連付けとエクスプローラのサムネイル表示を設定します。
3. あとは `.procreate` ファイルをダブルクリックするだけです。アンインストールは「設定 → アプリ」から。

Run the installer (allow it through SmartScreen with "More info → Run anyway"). It associates
`.procreate` files and registers Explorer thumbnails; uninstall from Settings → Apps.

ポータブル版（`ProcreateViewer-x.y.z-portable.zip`）は展開して `ProcreateViewer.exe` を起動するだけで使えます。
関連付けは `ProcreateViewer.exe --register`、サムネイルは管理者で `register.cmd`。

動作環境: Windows 10 / 11（64 bit）。追加のランタイムは不要です。

## 使い方 / Usage

- 左の一覧の目のアイコンで表示 / 非表示、グループは折りたためます。「選択レイヤーのみ」で 1 枚だけ表示。
- ホイールでズーム、ドラッグでスクロール、ダブルクリックでフィット。「100%」で原寸。
- 「表示をPNG保存」「全レイヤーをPNG書き出し」「PSD書き出し」で書き出し。
