using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace BveVehicleEditor
{
    /// <summary>
    /// 「ファイル → バックアップの設定…」のウィンドウ
    /// ・保存の前に控えを残すか・1 つのファイルにつき残す数（1～100）
    /// ・自動保存するか・間隔（1～60 分）
    /// OK なら Selected～ に入れて閉じる（当てはめるのは MainWindow）
    /// </summary>
    public partial class BackupWindow : Window
    {
        /// <summary>OK で選んだ「控えを残すか」</summary>
        public bool SelectedEnabled { get; private set; }

        /// <summary>OK で選んだ残す数</summary>
        public int SelectedMaxCount { get; private set; }

        /// <summary>OK で選んだ「自動保存するか」</summary>
        public bool SelectedAutoSaveEnabled { get; private set; }

        /// <summary>OK で選んだ自動保存の間隔（分）</summary>
        public int SelectedAutoSaveMinutes { get; private set; }

        /// <summary>
        /// ウィンドウを作る
        /// </summary>
        /// <param name="enabled">今の「控えを残すか」</param>
        /// <param name="maxCount">今の残す数</param>
        /// <param name="autoSaveEnabled">今の「自動保存するか」</param>
        /// <param name="autoSaveMinutes">今の自動保存の間隔（分）</param>
        public BackupWindow(bool enabled, int maxCount, bool autoSaveEnabled, int autoSaveMinutes)
        {
            InitializeComponent();
            TxtFolder.Text = BackupManager.FolderPath;
            TxtAutoSaveFolder.Text = AutoSaveManager.FolderPath;
            ChkEnabled.IsChecked = enabled;
            TxtMaxCount.Text = maxCount.ToString(CultureInfo.InvariantCulture);
            ChkAutoSave.IsChecked = autoSaveEnabled;
            TxtAutoSaveMinutes.Text = autoSaveMinutes.ToString(CultureInfo.InvariantCulture);
            ShowProblem();
        }

        // チェックか数を変えたら、問題を調べ直す（InitializeComponent の途中で呼ばれたときは、ShowProblem が何もしない）
        private void Input_Changed(object sender, RoutedEventArgs e)
        {
            ShowProblem();
        }

        // 欄の整数を読む。範囲の外・整数でなければ null
        private static int? ReadNumber(TextBox box, int min, int max)
        {
            if (!int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)) return null;
            return value >= min && value <= max ? value : null;
        }

        private int? ReadMaxCount() => ReadNumber(TxtMaxCount, BackupManager.MinCount, BackupManager.MaxCountLimit);

        private int? ReadAutoSaveMinutes() => ReadNumber(TxtAutoSaveMinutes, AutoSaveManager.MinMinutes, AutoSaveManager.MaxMinutes);

        // 使わないほうの数の欄は灰色にし、使う欄の数がおかしければ理由を出して OK を押せなくする
        private void ShowProblem()
        {
            if (TxtProblem == null || BtnOk == null || PanelCount == null || PanelAutoSave == null || ChkAutoSave == null || TxtAutoSaveMinutes == null) return;
            bool enabled = ChkEnabled.IsChecked == true;
            bool autoSave = ChkAutoSave.IsChecked == true;
            PanelCount.IsEnabled = enabled;
            PanelAutoSave.IsEnabled = autoSave;

            List<string> problems = [];
            if (enabled && ReadMaxCount() == null) problems.Add(Loc.F("Bk.001", BackupManager.MinCount, BackupManager.MaxCountLimit, TxtMaxCount.Text));
            if (autoSave && ReadAutoSaveMinutes() == null) problems.Add(Loc.F("Bk.002", AutoSaveManager.MinMinutes, AutoSaveManager.MaxMinutes, TxtAutoSaveMinutes.Text));
            TxtProblem.Text = string.Join("\n", problems.Select(problem => "⚠ " + problem));
            TxtProblem.Visibility = problems.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            BtnOk.IsEnabled = problems.Count == 0;
        }

        // 初めに戻す：控えを残す・10 個・自動保存する・5 分ごとにする（OK で当てはめる）
        private void BtnDefault_Click(object sender, RoutedEventArgs e)
        {
            ChkEnabled.IsChecked = BackupManager.DefaultEnabled;
            TxtMaxCount.Text = BackupManager.DefaultMaxCount.ToString(CultureInfo.InvariantCulture);
            ChkAutoSave.IsChecked = AutoSaveManager.DefaultEnabled;
            TxtAutoSaveMinutes.Text = AutoSaveManager.DefaultMinutes.ToString(CultureInfo.InvariantCulture);
            ShowProblem();
        }

        // OK：使わないほうの数がおかしければ、今の数のままにする
        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            SelectedEnabled = ChkEnabled.IsChecked == true;
            SelectedMaxCount = ReadMaxCount() ?? BackupManager.MaxCount;
            SelectedAutoSaveEnabled = ChkAutoSave.IsChecked == true;
            SelectedAutoSaveMinutes = ReadAutoSaveMinutes() ?? AutoSaveManager.Minutes;
            DialogResult = true;
        }
    }
}
