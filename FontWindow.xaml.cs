using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;

namespace BveVehicleEditor
{
    /// <summary>
    /// テキストエリアのフォントの種類と大きさを選ぶウィンドウ（「表示 → フォント…」から開く）
    /// 上におすすめの等幅のフォント（この PC に入っているものだけ）、下にこの PC に入っているすべてのフォント。日本語の名前があれば日本語で出す
    /// OK なら SelectedFamily・SelectedSize に入れて閉じる（当てはめるのは MainWindow）
    /// </summary>
    public partial class FontWindow : Window
    {
        /// <summary>初めのフォント（XAML のテキストエディタと同じ）</summary>
        internal const string DefaultFamilyName = "Consolas";

        /// <summary>初めの大きさ</summary>
        internal const double DefaultSize = 13;

        /// <summary>大きさの範囲（Ctrl+マウスホイールと同じ）</summary>
        internal const double MinSize = 8;
        internal const double MaxSize = 40;

        // おすすめの等幅のフォント（この順に、入っているものだけ出す）
        private static readonly string[] RecommendedNames = ["Consolas", "Cascadia Mono", "Cascadia Code", "BIZ UDGothic", "MS Gothic", "Courier New", "Lucida Console"];

        // 見本の文字（BVE のテキストらしい行と、日本語のコメント）
        private const string PreviewText = "BveTs Vehicle Parameters 2.01:shift_jis\n\n[Cab]\nPowerNotches = 5        ; 力行ノッチ数\nBrakeNotches = 8        ; ブレーキノッチ数\nReverserText = 前, 切, 後\n0123456789 lI1| O0o";

        /// <summary>OK で選んだフォント（キャンセルなら null）</summary>
        public FontFamily? SelectedFamily { get; private set; }

        /// <summary>OK で選んだ大きさ</summary>
        public double SelectedSize { get; private set; }

        /// <summary>
        /// ウィンドウを作る
        /// </summary>
        /// <param name="currentFamily">今のテキストエリアのフォント</param>
        /// <param name="currentSize">今のテキストエリアの文字の大きさ</param>
        public FontWindow(FontFamily currentFamily, double currentSize)
        {
            InitializeComponent();
            TxtPreview.Text = PreviewText;
            BuildFontList();
            SelectFamily(currentFamily.Source);
            TxtSize.Text = currentSize.ToString(CultureInfo.InvariantCulture);
            ShowPreview();
        }

        // フォントの一覧を作る：見出し（選べない）・おすすめ・見出し・すべて
        private void BuildFontList()
        {
            List<FontFamily> all = [.. Fonts.SystemFontFamilies.OrderBy(DisplayName, StringComparer.CurrentCulture)];
            CmbFont.Items.Add(Heading(Loc.T("Fo.002")));
            foreach (string name in RecommendedNames)
            {
                FontFamily? family = all.FirstOrDefault(item => string.Equals(item.Source, name, StringComparison.OrdinalIgnoreCase));
                if (family != null) CmbFont.Items.Add(FontItem(family));
            }
            CmbFont.Items.Add(Heading(Loc.F("Fo.003", all.Count)));
            foreach (FontFamily family in all) CmbFont.Items.Add(FontItem(family));
        }

        // 選べない見出しの行
        private static ComboBoxItem Heading(string text) => new() { Content = text, IsEnabled = false };

        // フォントの行（名前はそのフォントで出す）
        private static ComboBoxItem FontItem(FontFamily family) => new() { Content = DisplayName(family), Tag = family, FontFamily = family };

        /// <summary>
        /// 画面に出す名前（日本語の名前があれば日本語。なければ英語の名前）
        /// </summary>
        internal static string DisplayName(FontFamily family)
        {
            return family.FamilyNames.TryGetValue(XmlLanguage.GetLanguage("ja-jp"), out string? japanese) ? japanese : family.Source;
        }

        /// <summary>
        /// そのフォントがこの PC に入っているか（覚えたフォントを消したときなどに使う）
        /// </summary>
        internal static bool IsInstalled(string familyName)
        {
            return Fonts.SystemFontFamilies.Any(family => string.Equals(family.Source, familyName, StringComparison.OrdinalIgnoreCase));
        }

        // 名前のフォントを選ぶ（おすすめにあればそちら。なければすべての中から。見つからなければ選ばない）
        private void SelectFamily(string familyName)
        {
            CmbFont.SelectedItem = CmbFont.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => item.Tag is FontFamily family && string.Equals(family.Source, familyName, StringComparison.OrdinalIgnoreCase));
        }

        // フォントか大きさを変えたら、見本を出し直す（InitializeComponent の途中で呼ばれたときは、ShowPreview が何もしない）
        private void Input_Changed(object sender, RoutedEventArgs e)
        {
            ShowPreview();
        }

        // 見本を選んだフォントと大きさで出し、問題があれば理由を出して OK を押せなくする
        private void ShowPreview()
        {
            if (TxtPreview == null || TxtSize == null || TxtProblem == null || BtnOk == null) return;
            FontFamily? family = (CmbFont.SelectedItem as ComboBoxItem)?.Tag as FontFamily;
            string? problem = family == null ? Loc.T("Fo.004") : null;
            if (!double.TryParse(TxtSize.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double size) || size < MinSize || size > MaxSize)
                problem ??= Loc.F("Fo.005", MinSize, MaxSize, TxtSize.Text);

            if (family != null) TxtPreview.FontFamily = family;
            if (problem == null) TxtPreview.FontSize = size;
            TxtProblem.Text = problem == null ? string.Empty : "⚠ " + problem;
            TxtProblem.Visibility = problem == null ? Visibility.Collapsed : Visibility.Visible;
            BtnOk.IsEnabled = problem == null;
        }

        // 初めに戻す：Consolas・13 にする（OK で当てはめる）
        private void BtnDefault_Click(object sender, RoutedEventArgs e)
        {
            SelectFamily(DefaultFamilyName);
            TxtSize.Text = DefaultSize.ToString(CultureInfo.InvariantCulture);
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            if ((CmbFont.SelectedItem as ComboBoxItem)?.Tag is not FontFamily family) return;
            if (!double.TryParse(TxtSize.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double size)) return;
            SelectedFamily = family;
            SelectedSize = size;
            DialogResult = true;
        }
    }
}
