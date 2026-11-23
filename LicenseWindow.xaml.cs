using System.Windows;

namespace BveVehicleEditor
{
    /// <summary>
    /// 「ヘルプ → バージョン情報…」の「ライセンスと免責事項…」から開くウィンドウ
    /// 免責事項（Strings の Lic.010。言語で切り替わる）・このアプリのライセンス（MIT）・部品のライセンスを見せる
    /// ライセンスの文は英語が正式なので訳さない。中身はプロジェクトの LICENSE・THIRD-PARTY-NOTICES.txt と同じにする（直すときは両方直す）
    /// </summary>
    public partial class LicenseWindow : Window
    {
        // MIT License の本文（著作権の行より下）。画面ではウィンドウの幅で折り返すので、段落の中は改行しない（LICENSE ファイルは 80 文字ごとに改行している）
        private const string MitBody =
            "Permission is hereby granted, free of charge, to any person obtaining a copy " +
            "of this software and associated documentation files (the \"Software\"), to deal " +
            "in the Software without restriction, including without limitation the rights " +
            "to use, copy, modify, merge, publish, distribute, sublicense, and/or sell " +
            "copies of the Software, and to permit persons to whom the Software is " +
            "furnished to do so, subject to the following conditions:\n\n" +
            "The above copyright notice and this permission notice shall be included in all " +
            "copies or substantial portions of the Software.\n\n" +
            "THE SOFTWARE IS PROVIDED \"AS IS\", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR " +
            "IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, " +
            "FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE " +
            "AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER " +
            "LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, " +
            "OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE " +
            "SOFTWARE.";

        // このアプリの著作権の表示（ユーザーが決めた。開発者の名前は AboutWindow.DeveloperName）
        private static string AppCopyright => $"Copyright (c) 2026 {AboutWindow.DeveloperName}";

        // 使っている部品（名前と版・場所・著作権の表示。どれも MIT License。NuGet の情報で確かめた）
        private static readonly (string Name, string Url, string Copyright)[] Components =
        [
            ("AvalonEdit 6.3.1", "https://github.com/icsharpcode/AvalonEdit", "Copyright (c) 2000-2025 AlphaSierraPapa for the SharpDevelop Team"),
            ("OxyPlot.Core / OxyPlot.Wpf / OxyPlot.Wpf.Shared 2.2.0", "https://github.com/oxyplot/oxyplot", "Copyright (c) 2014-2022 OxyPlot Contributors"),
            ("System.Text.Encoding.CodePages 11.0.0", "https://github.com/dotnet/runtime", "Copyright (c) .NET Foundation and Contributors / (c) Microsoft Corporation"),
        ];

        public LicenseWindow()
        {
            InitializeComponent();
            TxtAppLicense.Text = $"MIT License\n\n{AppCopyright}\n\n{MitBody}";
            TxtThirdParty.Text = BuildThirdPartyText();
        }

        // 部品のライセンスの文（部品ごとに名前・場所・MIT License の全文）
        private static string BuildThirdPartyText()
        {
            string line = new('=', 60);
            IEnumerable<string> parts = Components.Select(component =>
                $"{line}\n{component.Name}\n{component.Url}\n{line}\n\nMIT License\n\n{component.Copyright}\n\n{MitBody}");
            return "BVE Vehicle Editor uses the following third-party components.\n"
                + "Each component is licensed under the MIT License shown below.\n\n"
                + string.Join("\n\n", parts);
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
