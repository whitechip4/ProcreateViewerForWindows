# ProcreateViewer for Windows

Procreate の `.procreate` ファイルを Windows で開くビューアです。レイヤーごとの表示切替、原寸表示、
PNG / レイヤー付き PSD の書き出しができ、エクスプローラにサムネイルも表示します。UI は日本語 / 英語。

A viewer for Procreate (`.procreate`) files on Windows: layers and groups, full-resolution view,
PNG and layered PSD export, Explorer thumbnails. Japanese and English UI.
<img width="1266" height="833" alt="image" src="https://github.com/user-attachments/assets/536be1b3-8365-4ae1-89f8-78ad203d0862" />

## ダウンロード / Download

**[Releases](https://github.com/whitechip4/ProcreateViewerForWindows/releases)** から
`ProcreateViewer-Setup-x.y.z.exe` をダウンロードしてください。

Download `ProcreateViewer-Setup-x.y.z.exe` from
**[Releases](https://github.com/whitechip4/ProcreateViewerForWindows/releases)**.

## インストール / Install

1. `ProcreateViewer-Setup-x.y.z.exe` を実行します。署名していないため Windows の SmartScreen が
   「発行元不明」と警告する場合は「詳細情報」→「実行」で進めてください。
2. インストーラが `.procreate` の関連付けとエクスプローラのサムネイル表示を設定します。
3. あとは `.procreate` ファイルをダブルクリックするだけです。アンインストールは「設定 → アプリ」から。

1. Run `ProcreateViewer-Setup-x.y.z.exe`. The installer is unsigned, so if SmartScreen warns about an
   unknown publisher choose "More info" → "Run anyway".
2. The installer associates `.procreate` files and registers Explorer thumbnails.
3. Double-click any `.procreate` file. Uninstall from Settings → Apps.

ポータブル版（`ProcreateViewer-x.y.z-portable.zip`）は展開して `ProcreateViewer.exe` を起動するだけで使えます。
関連付けは `ProcreateViewer.exe --register`、サムネイルは管理者で `register.cmd`。

The portable build (`ProcreateViewer-x.y.z-portable.zip`) just needs unzipping; run `ProcreateViewer.exe`.
`ProcreateViewer.exe --register` adds the file association, `register.cmd` (as administrator) the thumbnails.

動作環境: Windows 10 / 11（64 bit）。追加のランタイムは不要です。

Requires Windows 10 / 11 (64-bit). No additional runtime is needed.

## 使い方 / Usage

- 左の一覧の目のアイコンで表示 / 非表示、グループは折りたためます。「選択レイヤーのみ」で 1 枚だけ表示。
- ホイールでズーム、ドラッグでスクロール、ダブルクリックでフィット。「100%」で原寸。
- 「表示をPNG保存」「全レイヤーをPNG書き出し」「PSD書き出し」で書き出し。
- 言語はツールバー右端のボタン（「English」/「日本語」）で切り替えます。初回は Windows の表示言語に従います。

- The eye icons in the layer list toggle visibility; groups can be collapsed. "Selected layer only" shows a single layer.
- Mouse wheel zooms, drag scrolls, double-click fits the window. "100%" shows actual pixels.
- "Save view as PNG", "Export all layers as PNG" and "Export PSD" write files.
- The button at the right end of the toolbar ("English" / "日本語") switches the UI language. The first start
  follows the Windows display language; `ProcreateViewer.exe --lang en` sets it from the command line.

## ライセンス / License

MIT License。LZO デコーダは [lzokay](https://github.com/jackoalan/lzokay)（MIT）の移植です。
Procreate は Savage Interactive Pty Ltd の商標です。本ソフトは非公式の独立したビューアで、同社とは関係ありません。

MIT License. The LZO decoder is a port of [lzokay](https://github.com/jackoalan/lzokay) (MIT).
Procreate is a trademark of Savage Interactive Pty Ltd; this is an independent, unofficial viewer not affiliated with them.
