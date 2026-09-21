using System;
using System.Collections.Generic;
using System.Globalization;

namespace ProcreateViewer
{
    /// <summary>UI strings in Japanese and English.  The language comes from the "Language" preference
    /// ("ja" / "en"); when it is unset the OS display language decides (Japanese -> ja, anything else -> en).
    /// Strings are looked up by their English text, so a missing entry simply shows English.</summary>
    public static class L
    {
        private static string lang;   // "ja" or "en"

        public static string Current
        {
            get
            {
                if (lang == null)
                {
                    string pref = Prefs.Get("Language", "");
                    if (pref == "ja" || pref == "en") lang = pref;
                    else lang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ja" ? "ja" : "en";
                }
                return lang;
            }
        }

        public static bool IsJapanese { get { return Current == "ja"; } }

        /// <summary>Switch language for this process and remember it for the next start.</summary>
        public static void Set(string code) { Set(code, true); }

        public static void Set(string code, bool persist)
        {
            lang = code == "ja" ? "ja" : "en";
            if (persist) Prefs.Set("Language", lang);
        }

        /// <summary>Translate an English UI string; returns the input unchanged in English mode or when unknown.</summary>
        public static string T(string en)
        {
            if (!IsJapanese) return en;
            string s;
            return ja.TryGetValue(en, out s) ? s : en;
        }

        public static string F(string en, params object[] args) { return string.Format(T(en), args); }

        private static readonly Dictionary<string, string> ja = new Dictionary<string, string>
        {
            // toolbar
            { "Open…", "開く…" },
            { "Composite", "合成表示" },
            { "Selected layer only", "選択レイヤーのみ" },
            { "Show all", "全て表示" },
            { "Hide all", "全て非表示" },
            { "As saved", "保存時の状態" },
            { "Fit", "フィット" },
            { "Full size", "フルサイズ" },
            { "Composite at full resolution (off: a lighter preview with the long side at 2048 px)", "原寸で合成して表示する（オフにすると長辺 2048px のプレビューで軽く表示）" },
            { "Save view as PNG…", "表示をPNG保存…" },
            { "Export all layers as PNG…", "全レイヤーをPNG書き出し…" },
            { "Export PSD…", "PSD書き出し…" },
            { "Language", "言語" },
            { "Japanese", "日本語" },
            { "English", "English" },

            // status bar
            { "Open a file or drop a .procreate here", "ファイルを開くか、ここに .procreate をドロップしてください" },
            { "Opening… {0}", "開いています… {0}" },
            { "Cannot open: {0}", "開けません: {0}" },
            { "Cannot open", "開けません" },
            { "Decoding layers…", "レイヤー展開中…" },
            { "Decoding layers {0}/{1}", "レイヤー展開中 {0}/{1}" },
            { "Load error: {0}", "読み込みエラー: {0}" },
            { "Full size (cache {0} MB)", "フルサイズ (キャッシュ {0} MB)" },
            { "Preview {0}%", "プレビュー {0}%" },
            { "   view {0:0.#}%", "   表示 {0:0.#}%" },
            { "{0}   {1}x{2} px   {3} dpi   {4} layers   orientation {5}{6}{7}   {8}{9}   load {10} ms / render {11} ms", "{0}   {1}x{2} px   {3} dpi   レイヤー {4}   向き {5}{6}{7}   {8}{9}   読込 {10} ms / 描画 {11} ms" },
            { " flip H", " 左右反転" },
            { " flip V", " 上下反転" },
            { "Compositing at full size…", "フルサイズで合成中…" },
            { "Render error: {0}", "描画エラー: {0}" },
            { "Saved: {0}", "保存しました: {0}" },
            { "Exporting {0}/{1}", "書き出し中 {0}/{1}" },
            { "Exported {0} files: {1}", "{0} 枚を書き出しました: {1}" },
            { "Export error: {0}", "書き出しエラー: {0}" },
            { "Exporting PSD {0}/{1}", "PSD 書き出し中 {0}/{1}" },
            { "PSD written: {0} ({1} MB, {2:0.0} s)", "PSD を書き出しました: {0} ({1} MB, {2:0.0} 秒)" },
            { "PSD export error: {0}", "PSD 書き出しエラー: {0}" },

            // dialogs
            { "Output folder (composite.png and one PNG per layer are written into a folder with this name)", "書き出し先フォルダ（この名前のフォルダに composite.png と各レイヤーの PNG を作ります）" },
            { "Folder", "フォルダ" },
            { "Procreate Viewer - error", "Procreate Viewer - エラー" },
            { "PSD is limited to 30000 px (PSB would be required)", "PSD は 30000px までです（PSB が必要）" },

            // layer list
            { "Group", "グループ" },
            { "Layer", "レイヤー" },
            { "{0}   [Group {1}%]", "{0}   [グループ {1}%]" },

            // updates
            { "Version {0} is available. Click to update", "新しいバージョン {0} があります。クリックで更新" },
            { "Downloading {0}\u2026 {1}", "{0} をダウンロード中… {1}" },
            { "Update failed: {0}", "更新に失敗しました: {0}" },

            // Explorer context menu
            { "Open with Procreate Viewer", "Procreate Viewer で開く" },
            { "Export layers as PNG", "レイヤーを PNG で書き出し" },
        };
    }
}
