using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using Microsoft.Win32;

namespace BveVehicleEditor
{
    /// <summary>
    /// モーター音のファイル（MotorNoise.txt）の入力フォーム
    /// 段階1：[Power]・[Brake] の音量（Volume）・ピッチ（Frequency）の表のファイルのパスを出し、「参照」で選ぶと下のテキストを書き換える。「開く」で表のウィンドウを開く
    /// 下のテキストエディタが元になり、テキストが変わると少しあとに表示し直す（ノッチのタブの NotchForm と同じ作り）
    /// </summary>
    public partial class MotorNoiseForm : UserControl
    {
        /// <summary>
        /// 表のファイル 1 つ分の行
        /// </summary>
        /// <param name="Section">セクション名（Power / Brake）</param>
        /// <param name="Key">キー（Volume / Frequency）</param>
        /// <param name="Label">画面の名前</param>
        /// <param name="PathBox">パスを出す欄</param>
        /// <param name="BrowseButton">参照ボタン</param>
        /// <param name="OpenButton">表のウィンドウを開くボタン</param>
        private sealed record TableRow(string Section, string Key, string Label, TextBox PathBox, Button BrowseButton, Button OpenButton);

        // 表のファイルの 1 行目のヘッダー（Bvets Motor Noise Table 0.01・BveTs Motor Noise Table 1.00 がある。大文字・小文字と番号は比べない）
        private const string MotorNoiseTableHeader = "BveTs Motor Noise Table";

        // セクションと画面の名前
        private static (string Section, string Label)[] Sections => [("Power", Loc.T("Mn.001")), ("Brake", Loc.T("Mn.002"))];

        // 表のファイルのキーと画面の名前・説明・グラフの縦の軸の名前
        private static (string Key, string Label, string Description, string ValueName)[] TableKeys =>
        [
            ("Volume", Loc.T("Mn.003"), Loc.T("Mn.004"), Loc.T("Mn.005")),
            ("Frequency", Loc.T("Mn.006"), Loc.T("Mn.007"), Loc.T("Mn.008")),
        ];

        // 公式の一覧：セクション → 書けるキー（"" はセクションの前。何も書けない）。一覧にない名前は書き間違いかもしれないので警告する（BveTextParser.FindUnknownNames）
        private static Dictionary<string, string[]> KnownKeys => new()
        {
            [""] = [],
            ["Power"] = [.. TableKeys.Select(item => item.Key)],
            ["Brake"] = [.. TableKeys.Select(item => item.Key)],
        };

        // 元になるテキストエディタ
        private TextEditor? _editor;

        // モーター音のファイルのフルパスを読む（Attach でもらう。参照ボタンでファイルが替わることがあるので、そのつど読む）
        private Func<string?>? _getMotorNoisePath;

        // サウンドのテキストを読む（Attach でもらう。開くたびに今のテキストを読む）
        private Func<string?>? _getSoundText;

        // テキストを打つたびに読み直すと重いので、打ち終わって少し待ってから1回だけ表示し直す
        private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };

        // 表のファイルの行（[Power] の 2 つ、[Brake] の 2 つ）
        private readonly List<TableRow> _rows = [];

        public MotorNoiseForm()
        {
            InitializeComponent();
            _refreshTimer.Tick += (s, e) =>
            {
                _refreshTimer.Stop();
                Refresh();
            };

            foreach ((string section, _) in Sections)
            {
                BuildRows(section == "Power" ? GridPower : GridBrake, section);
            }
        }

        /// <summary>
        /// 言語を切り替えたあと、表のファイルの行（名前・ボタン）を新しい言語で作り直して、表示し直す
        /// </summary>
        public void ApplyLanguage()
        {
            _rows.Clear();
            foreach (Grid grid in new[] { GridPower, GridBrake })
            {
                grid.Children.Clear();
                grid.RowDefinitions.Clear();
                grid.ColumnDefinitions.Clear();
            }
            foreach ((string section, _) in Sections)
            {
                BuildRows(section == "Power" ? GridPower : GridBrake, section);
            }
            Refresh();
        }

        // セクションの枠の中に、表のファイルの行（名前・パスの欄・参照ボタン・開くボタン）を作る
        private void BuildRows(Grid grid, string section)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "MotorLabel" });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            foreach ((string key, string label, string description, _) in TableKeys)
            {
                int row = grid.RowDefinitions.Count;
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                TextBlock name = new() { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 8, 2), ToolTip = description };
                // パスは下のテキストか「参照」で変える（この欄では書き換えない）
                TextBox box = new() { IsReadOnly = true, Margin = new Thickness(0, 2, 0, 2) };
                // 幅 480 でも「開く」まで入るように、ボタンの左右の余白を少し詰める（ノッチのタブと同じ）
                Thickness buttonPadding = new(8, 4, 8, 5);
                Button browse = new() { Content = Loc.T("Mn.009"), Margin = new Thickness(6, 2, 0, 2), Padding = buttonPadding, Tag = (section, key) };
                browse.Click += BrowseButton_Click;
                Button open = new() { Content = Loc.T("Mn.010"), Margin = new Thickness(4, 2, 0, 2), Padding = buttonPadding, Tag = (section, key), ToolTip = Loc.T("Mn.011") };
                open.Click += OpenButton_Click;

                Grid.SetRow(name, row);
                Grid.SetRow(box, row);
                Grid.SetColumn(box, 1);
                Grid.SetRow(browse, row);
                Grid.SetColumn(browse, 2);
                Grid.SetRow(open, row);
                Grid.SetColumn(open, 3);
                grid.Children.Add(name);
                grid.Children.Add(box);
                grid.Children.Add(browse);
                grid.Children.Add(open);
                _rows.Add(new(section, key, label, box, browse, open));
            }
        }

        /// <summary>
        /// 元になるテキストエディタをつなぐ。テキストが変わったら表示し直す
        /// </summary>
        /// <param name="editor">モーター音のテキストエディタ</param>
        /// <param name="getMotorNoisePath">モーター音のファイルのフルパスを読む（相対パスの基準）。読み込めていなければ null</param>
        /// <param name="getSoundText">サウンドのテキストを読む（表の音の列が [Motor] にあるかを調べる）。サウンドのファイルを読み込めていなければ null</param>
        public void Attach(TextEditor editor, Func<string?> getMotorNoisePath, Func<string?> getSoundText)
        {
            _editor = editor;
            _getMotorNoisePath = getMotorNoisePath;
            _getSoundText = getSoundText;
            editor.TextChanged += (s, e) =>
            {
                _refreshTimer.Stop();
                _refreshTimer.Start();
            };
            // 参照ボタンでモーター音のファイルが替わったあとにタブを開いたとき、見つかるかを調べ直す
            IsVisibleChanged += (s, e) =>
            {
                if (IsVisible) Refresh();
            };
            Refresh();
        }

        /// <summary>
        /// テキストを読み直して、表のファイルのパスと、見つかるかを表示する
        /// </summary>
        public void Refresh()
        {
            if (_editor == null) return;

            string? folder = MotorNoiseFolder();
            List<BveTextLine> lines = BveTextParser.Parse(_editor.Text);
            List<string> warnings = [];

            foreach (TableRow row in _rows)
            {
                row.BrowseButton.IsEnabled = folder != null;
                string? path = lines.LastOrDefault(line => line.Kind == BveLineKind.KeyValue
                    && string.Equals(line.Section, row.Section, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(line.Key, row.Key, StringComparison.OrdinalIgnoreCase))?.Value;

                row.PathBox.Text = path ?? string.Empty;
                string? problem = path == null || folder == null ? null : FindProblem(folder, path);
                row.PathBox.ToolTip = path == null ? Loc.T("Mn.012") : problem ?? FullPathOf(folder, path) ?? path;
                if (problem == null) row.PathBox.ClearValue(ForegroundProperty);
                else row.PathBox.SetResourceReference(ForegroundProperty, "SystemFillColorCautionBrush");
                // 開けるのは、パスが書かれていてファイルが見つかるときだけ
                row.OpenButton.IsEnabled = folder != null && path != null && problem == null;
                if (problem != null) warnings.Add($"[{row.Section}] {row.Label}（{row.Key} = {path}）… {problem}");
            }

            // 知らせ：読み込めていない・同じセクション（[Power]・[Brake]）が 2 回以上書かれている
            List<string> notices = [];
            if (folder == null && _editor.Text.Length > 0) notices.Add(Loc.T("Mn.013"));
            notices.AddRange(BveTextParser.DescribeDuplicateSections(lines));
            TxtMotorNotice.Text = string.Join("\n", notices);
            TxtMotorNotice.Visibility = notices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            // 公式の一覧にないセクション・キー（書き間違いかもしれない）
            warnings.AddRange(BveTextParser.FindUnknownNames(lines, KnownKeys));
            TxtMotorWarnings.Text = string.Join("\n", warnings.Select(warning => "⚠ " + warning));
            TxtMotorWarnings.Visibility = warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // モーター音のファイルがあるフォルダー（相対パスの基準）。読み込めていなければ null
        private string? MotorNoiseFolder()
        {
            string? path = _getMotorNoisePath?.Invoke();
            return string.IsNullOrEmpty(path) ? null : Path.GetDirectoryName(path);
        }

        // パスに問題があれば理由（使えない文字・見つからないなど）。問題なければ null
        private static string? FindProblem(string folder, string path)
        {
            string? pathProblem = VehicleFileParser.FindPathProblem(path, folder);
            if (pathProblem != null) return pathProblem;

            string? fullPath = FullPathOf(folder, path);
            return fullPath != null && File.Exists(fullPath) ? null : Loc.T("Mn.014");
        }

        // フォルダーと相対パスからフルパスを作る。作れなければ null
        private static string? FullPathOf(string? folder, string path)
        {
            if (folder == null) return null;
            try
            {
                return Path.GetFullPath(Path.Combine(folder, path));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return null;
            }
        }

        // 参照ボタン：表のファイルを選んで、MotorNoise.txt からの相対パスを下のテキストに書き込む（なければ足す。Ctrl+Z で戻せる）
        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            if (_editor == null || sender is not Button { Tag: (string section, string key) }) return;
            string? folder = MotorNoiseFolder();
            if (folder == null) return;

            TableRow row = _rows.First(item => item.Section == section && item.Key == key);
            string? current = FullPathOf(folder, row.PathBox.Text);
            string? initialFolder = current == null ? null : Path.GetDirectoryName(current);
            OpenFileDialog dialog = new()
            {
                Filter = Loc.T("Mn.015"),
                Title = Loc.F("Mn.016", section, row.Label),
                InitialDirectory = initialFolder != null && Directory.Exists(initialFolder) ? initialFolder : folder,
                CheckFileExists = true,
            };
            if (dialog.ShowDialog() != true) return;

            string relativePath = Path.GetRelativePath(folder, dialog.FileName);
            string? problem = VehicleFileParser.FindPathProblem(relativePath, folder);
            if (problem != null)
            {
                MessageBox.Show(Loc.F("Mn.017", relativePath, problem), Loc.T("Mn.018"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            EditorTextHelper.ReplaceChangedText(_editor, BveTextParser.SetValue(_editor.Text, section, key, relativePath));
            OperationHistory.Add($"モーター音入力: [{section}] {key} = {relativePath}");
        }

        /// <summary>
        /// サウンドのテキストの [Motor] に書かれている番号（キーは空白を除いて整数として読む）。テキストがなければ null
        /// </summary>
        internal static HashSet<int>? ReadMotorNumbers(string? soundText)
        {
            if (string.IsNullOrEmpty(soundText)) return null;
            HashSet<int> numbers = [];
            foreach (BveTextLine line in BveTextParser.Parse(soundText))
            {
                if (line.Kind != BveLineKind.KeyValue || !string.Equals(line.Section, "Motor", StringComparison.OrdinalIgnoreCase)) continue;
                string key = string.Concat(line.Key!.Where(c => !char.IsWhiteSpace(c)));
                if (int.TryParse(key, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int number)) numbers.Add(number);
            }
            return numbers;
        }

        // 開くボタン：表のファイルを別のウィンドウで、表の形で見る（閉じるまでメインのウィンドウは触れない）
        private void OpenButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: (string section, string key) }) return;
            string? folder = MotorNoiseFolder();
            if (folder == null) return;

            TableRow row = _rows.First(item => item.Section == section && item.Key == key);
            string? fullPath = FullPathOf(folder, row.PathBox.Text);
            if (fullPath == null) return;

            string sectionLabel = Sections.First(item => item.Section == section).Label;
            string valueName = TableKeys.First(item => item.Key == key).ValueName;
            TableWindow window;
            try
            {
                // 列は音の番号（Motor0.wav が 0）なので、「音 0」から数える
                window = new(fullPath, Loc.F("Mn.019", section, sectionLabel, row.Label), MotorNoiseTableHeader, Loc.T("Mn.020"), false, valueName, Loc.T("Mn.021"), 0);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                OperationHistory.Add($"表のウィンドウを開けない: {ex.GetType().Name}: {ex.Message}");
                MessageBox.Show(Loc.F("Mn.022", ErrorText.Describe(ex)), Loc.T("Mn.023"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            OperationHistory.Add($"表のウィンドウを開く: モーター音 [{section}] {key} = {row.PathBox.Text}");
            window.Owner = Window.GetWindow(this);
            // 音量は 0～1、ピッチはマイナスにしない、を調べ、音の列が Sound.txt の [Motor] にあるかも調べる（TableWindow.MotorNoise.cs）
            window.EnableMotorNoiseChecks(key == "Volume", ReadMotorNumbers(_getSoundText?.Invoke()));
            window.ShowDialog();
        }
    }
}
