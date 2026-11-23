using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;
using ICSharpCode.AvalonEdit;

namespace BveVehicleEditor
{
    /// <summary>
    /// 「ヘルプ → バージョン情報…」のウィンドウ（リンクを押せるように、MessageBox ではなくウィンドウにした。ユーザーの要望）
    /// アプリの名前・バージョン・開発者・GitHub のプロフィールへのリンクと、.NET・部品の版（動いているときに読み取る）
    /// </summary>
    public partial class AboutWindow : Window
    {
        /// <summary>アプリの名前</summary>
        internal const string AppDisplayName = "BVE Vehicle Editor";

        /// <summary>開発者の名前（ユーザーが決めた）</summary>
        internal const string DeveloperName = "テツドウ";

        /// <summary>開発者の GitHub のプロフィール（ユーザーが決めた）</summary>
        internal const string GitHubUser = "ijocn0930-lang";

        public AboutWindow()
        {
            InitializeComponent();
            TxtAppName.Text = AppDisplayName;
            TxtVersion.Text = Loc.F("Ab.002", VersionOf(typeof(AboutWindow)));
            RunDeveloper.Text = DeveloperName;
            RunGitHub.Text = GitHubUser;
            LinkGitHub.NavigateUri = new Uri($"https://github.com/{GitHubUser}");
            TxtComponents.Text = Loc.F("Ab.003", Environment.Version, VersionOf(typeof(TextEditor)), VersionOf(typeof(OxyPlot.PlotModel)));
        }

        /// <summary>
        /// 読み込んでいる DLL の版（3 つ目まで。アプリは .csproj に書いていないので 1.0.0）
        /// </summary>
        internal static string VersionOf(Type type) => type.Assembly.GetName().Version is Version v ? $"{v.Major}.{v.Minor}.{v.Build}" : Loc.T("Ab.004");

        // リンクを押したら、ブラウザーで開く（開けなければ日本語の理由を出す）
        private void Link_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            e.Handled = true;
            try
            {
                Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
                OperationHistory.Add($"リンクを開く: {e.Uri.AbsoluteUri}");
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                OperationHistory.Add($"リンクを開けない: {e.Uri.AbsoluteUri}（{ex.GetType().Name}: {ex.Message}）");
                MessageBox.Show(Loc.F("Ab.005", ErrorText.Describe(ex), e.Uri.AbsoluteUri), Loc.T("Ab.006"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // 「ライセンスと免責事項…」：LicenseWindow を開く
        private void BtnLicense_Click(object sender, RoutedEventArgs e)
        {
            OperationHistory.Add("ライセンスと免責事項を開く");
            new LicenseWindow { Owner = this }.ShowDialog();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
