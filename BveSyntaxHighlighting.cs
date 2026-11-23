using System.IO;
using System.Xml;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;

namespace BveVehicleEditor
{
    /// <summary>
    /// テキストエディタ（AvalonEdit）の色分け。BVE のテキストファイル共通（ヘッダー・セクション・キー・数字・コメント）
    /// 色分けの決まり（xshd）はここに文字で持つ（別ファイルにすると .csproj の書き換えが要るため）
    /// ライト用とダーク用の2つを作っておき、テーマに合わせて使い分ける
    /// </summary>
    internal static class BveSyntaxHighlighting
    {
        // 色分けの決まり（xshd）。{Header} などはライト／ダークの色に置き換える
        // ・コメント：; か # から行の最後まで
        // ・ヘッダー：行の先頭の「BveTs ～」「Version ～」（: の後ろの文字コード指定も含む）
        // ・セクション：[Cab] など
        // ・キー：= の左側
        private const string XshdTemplate = """
            <SyntaxDefinition name="BVE" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
              <Color name="Comment" foreground="{Comment}" />
              <Color name="Header" foreground="{Header}" fontWeight="bold" />
              <Color name="Section" foreground="{Section}" fontWeight="bold" />
              <Color name="Key" foreground="{Key}" />
              <Color name="Operator" foreground="{Operator}" />
              <Color name="Number" foreground="{Number}" />
              <RuleSet ignoreCase="true">
                <Span color="Comment">
                  <Begin>[;\#]</Begin>
                </Span>
                <Rule color="Header">^\s*(BveTs|Version)\b[^;\#\r\n]*</Rule>
                <Rule color="Section">^\s*\[[^\]\r\n]*\]</Rule>
                <Rule color="Key">^\s*[^=\[;\#\r\n]+?(?=\s*=)</Rule>
                <Rule color="Operator">=</Rule>
                <Rule color="Number">\b-?\d+(\.\d+)?\b</Rule>
              </RuleSet>
            </SyntaxDefinition>
            """;

        // 色は Visual Studio Code の標準の配色（Light+ / Dark+）を土台に、役割の似ているものを当てはめた
        // どの色も背景との見やすさ（コントラスト比）が目安の 4.5 以上

        // ライト用の色（白っぽい背景で読みやすい色）
        private static readonly Dictionary<string, string> LightColors = new()
        {
            ["Comment"] = "#008000",
            ["Header"] = "#AF00DB",
            ["Section"] = "#0000FF",
            ["Key"] = "#A31515",
            // 白い背景との見やすさ（コントラスト比）が目安の 4.5 以上になるよう、少し濃い灰色（#808080 は 3.9 だった）
            ["Operator"] = "#707070",
            ["Number"] = "#098658",
        };

        // ダーク用の色（黒っぽい背景で読みやすい色）
        private static readonly Dictionary<string, string> DarkColors = new()
        {
            // 暗い背景との見やすさが目安の 4.5 以上になるよう、少し明るい緑（#6A9955 は 4.2 だった。明るくしすぎると数字の色と見分けにくい）
            ["Comment"] = "#76A862",
            ["Header"] = "#C586C0",
            ["Section"] = "#569CD6",
            ["Key"] = "#9CDCFE",
            ["Operator"] = "#A0A0A0",
            ["Number"] = "#B5CEA8",
        };

        // 一度作ったらとっておく（毎回作ると遅いため）
        private static IHighlightingDefinition? _light;
        private static IHighlightingDefinition? _dark;

        /// <summary>
        /// テーマに合った色分けを返す
        /// </summary>
        /// <param name="isDark">ダークテーマか</param>
        public static IHighlightingDefinition Get(bool isDark)
        {
            if (isDark) return _dark ??= Create(DarkColors);
            return _light ??= Create(LightColors);
        }

        // 色を入れた xshd を読み込んで、色分けを作る
        private static IHighlightingDefinition Create(Dictionary<string, string> colors)
        {
            string xshd = XshdTemplate;
            foreach ((string name, string color) in colors)
            {
                xshd = xshd.Replace("{" + name + "}", color);
            }

            using XmlReader reader = XmlReader.Create(new StringReader(xshd));
            return HighlightingLoader.Load(reader, HighlightingManager.Instance);
        }
    }
}
