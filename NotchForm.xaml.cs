using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using Microsoft.Win32;

namespace BveVehicleEditor
{
    /// <summary>
    /// ノッチのファイル（Notch.txt）の入力フォーム
    /// 段階1：[Power]・[Brake] の表のファイル（進段パラメーター・引張力・電流）のパスを出し、「参照」で選ぶと下のテキストを書き換える
    /// 下のテキストエディタが元になり、テキストが変わると少しあとに表示し直す
    /// </summary>
    public partial class NotchForm : UserControl
    {
        /// <summary>
        /// 表のファイル 1 つ分の行
        /// </summary>
        /// <param name="Section">セクション名（Power / Brake）</param>
        /// <param name="Key">キー（Params など）</param>
        /// <param name="Label">画面の名前</param>
        /// <param name="PathBox">パスを出す欄</param>
        /// <param name="BrowseButton">参照ボタン</param>
        /// <param name="OpenButton">表のウィンドウを開くボタン</param>
        private sealed record TableRow(string Section, string Key, string Label, TextBox PathBox, Button BrowseButton, Button OpenButton);

        // 表のファイルの 1 行目のヘッダー（0.01・1.00・2.00・2.01 がある。番号は比べない）
        private const string PerformanceTableHeader = "BveTs Vehicle Performance Table";

        // セクションと画面の名前
        private static (string Section, string Label)[] Sections => [("Power", Loc.T("Nf.001")), ("Brake", Loc.T("Nf.002"))];

        // 表のファイルのキーと画面の名前（Plan.md の Notch.txt の説明の順）
        private static (string Key, string Label, string Description)[] TableKeys =>
        [
            ("Params", Loc.T("Nf.003"), Loc.T("Nf.004")),
            ("Force", Loc.T("Nf.005"), Loc.T("Nf.006")),
            ("MaxForce", Loc.T("Nf.007"), Loc.T("Nf.008")),
            ("Current", Loc.T("Nf.009"), Loc.T("Nf.010")),
            ("MaxCurrent", Loc.T("Nf.011"), Loc.T("Nf.012")),
            ("NoLoadCurrent", Loc.T("Nf.013"), Loc.T("Nf.014")),
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

        // ノッチのファイルのフルパスを読む（Attach でもらう。参照ボタンでファイルが替わることがあるので、そのつど読む）
        private Func<string?>? _getNotchPath;

        // パラメーターのテキストを読む（Attach でもらう。式から作るウィンドウで使う）
        private Func<string>? _getParamText;

        // テキストを打つたびに読み直すと重いので、打ち終わって少し待ってから1回だけ表示し直す
        private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };

        // 表のファイルの行（[Power] の 6 つ、[Brake] の 6 つ）
        private readonly List<TableRow> _rows = [];

        public NotchForm()
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

        // セクションの枠の中に、表のファイルの行（名前・パスの欄・参照ボタン）を作る
        private void BuildRows(Grid grid, string section)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "NotchLabel" });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            foreach ((string key, string label, string description) in TableKeys)
            {
                int row = grid.RowDefinitions.Count;
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                TextBlock name = new() { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 8, 2), ToolTip = description };
                // パスは下のテキストか「参照」で変える（この欄では書き換えない）
                TextBox box = new() { IsReadOnly = true, Margin = new Thickness(0, 2, 0, 2) };
                // 幅 480 でも「開く」まで入るように、ボタンの左右の余白を少し詰める
                Thickness buttonPadding = new(8, 4, 8, 5);
                Button browse = new() { Content = Loc.T("Nf.015"), Margin = new Thickness(6, 2, 0, 2), Padding = buttonPadding, Tag = (section, key) };
                browse.Click += BrowseButton_Click;
                // 表のファイルを別のウィンドウで、表の形で見る
                Button open = new() { Content = Loc.T("Nf.016"), Margin = new Thickness(4, 2, 0, 2), Padding = buttonPadding, Tag = (section, key), ToolTip = Loc.T("Nf.017") };
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
        /// <param name="editor">ノッチのテキストエディタ</param>
        /// <param name="getNotchPath">ノッチのファイルのフルパスを読む（相対パスの基準）。読み込めていなければ null</param>
        /// <param name="getParamText">パラメーターのテキストを読む（引張力を式から作るときに [Dynamics] とノッチ数を読む）</param>
        public void Attach(TextEditor editor, Func<string?> getNotchPath, Func<string> getParamText)
        {
            _editor = editor;
            _getNotchPath = getNotchPath;
            _getParamText = getParamText;
            editor.TextChanged += (s, e) =>
            {
                _refreshTimer.Stop();
                _refreshTimer.Start();
            };
            // 参照ボタンでノッチのファイルが替わったあとにタブを開いたとき、見つかるかを調べ直す
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

            string? folder = NotchFolder();
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
                row.PathBox.ToolTip = path == null ? Loc.T("Nf.018") : problem ?? FullPathOf(folder, path) ?? path;
                if (problem == null) row.PathBox.ClearValue(ForegroundProperty);
                else row.PathBox.SetResourceReference(ForegroundProperty, "SystemFillColorCautionBrush");
                // 開けるのは、パスが書かれていてファイルが見つかるときだけ
                row.OpenButton.IsEnabled = folder != null && path != null && problem == null;
                if (problem != null) warnings.Add($"[{row.Section}] {row.Label}（{row.Key} = {path}）… {problem}");
            }

            // 知らせ：読み込めていない・同じセクション（[Power]・[Brake]）が 2 回以上書かれている
            List<string> notices = [];
            if (folder == null && _editor.Text.Length > 0) notices.Add(Loc.T("Nf.019"));
            notices.AddRange(BveTextParser.DescribeDuplicateSections(lines));
            TxtNotchNotice.Text = string.Join("\n", notices);
            TxtNotchNotice.Visibility = notices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            // 公式の一覧にないセクション・キー（書き間違いかもしれない）
            warnings.AddRange(BveTextParser.FindUnknownNames(lines, KnownKeys));
            TxtNotchWarnings.Text = string.Join("\n", warnings.Select(warning => "⚠ " + warning));
            TxtNotchWarnings.Visibility = warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // ノッチのファイルがあるフォルダー（相対パスの基準）。読み込めていなければ null
        private string? NotchFolder()
        {
            string? path = _getNotchPath?.Invoke();
            return string.IsNullOrEmpty(path) ? null : Path.GetDirectoryName(path);
        }

        // パスに問題があれば理由（使えない文字・見つからないなど）。問題なければ null
        private static string? FindProblem(string folder, string path)
        {
            string? pathProblem = VehicleFileParser.FindPathProblem(path, folder);
            if (pathProblem != null) return pathProblem;

            string? fullPath = FullPathOf(folder, path);
            return fullPath != null && File.Exists(fullPath) ? null : Loc.T("Nf.020");
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

        // 参照ボタン：表のファイルを選んで、Notch.txt からの相対パスを下のテキストに書き込む（なければ足す。Ctrl+Z で戻せる）
        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            if (_editor == null || sender is not Button { Tag: (string section, string key) }) return;
            string? folder = NotchFolder();
            if (folder == null) return;

            TableRow row = _rows.First(item => item.Section == section && item.Key == key);
            string? current = FullPathOf(folder, row.PathBox.Text);
            string? initialFolder = current == null ? null : Path.GetDirectoryName(current);
            OpenFileDialog dialog = new()
            {
                Filter = Loc.T("Nf.021"),
                Title = Loc.F("Nf.022", section, row.Label),
                InitialDirectory = initialFolder != null && Directory.Exists(initialFolder) ? initialFolder : folder,
                CheckFileExists = true,
            };
            if (dialog.ShowDialog() != true) return;

            string relativePath = Path.GetRelativePath(folder, dialog.FileName);
            string? problem = VehicleFileParser.FindPathProblem(relativePath, folder);
            if (problem != null)
            {
                MessageBox.Show(Loc.F("Nf.023", relativePath, problem), Loc.T("Nf.024"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            WritePath(section, key, relativePath);
        }

        // 開くボタン：表のファイルを別のウィンドウで、表の形で見る（閉じるまでメインのウィンドウは触れない）
        private void OpenButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: (string section, string key) }) return;
            string? folder = NotchFolder();
            if (folder == null) return;

            TableRow row = _rows.First(item => item.Section == section && item.Key == key);
            string? fullPath = FullPathOf(folder, row.PathBox.Text);
            if (fullPath == null) return;

            string sectionLabel = Sections.First(item => item.Section == section).Label;
            string firstColumnName = key == "Params" ? Loc.T("Nf.025") : Loc.T("Nf.026");
            TableWindow window;
            try
            {
                // 進段パラメーターの 1 列目は「BreakerDelay_On」などの項目名なので、数字のチェックをせず、単位と説明を出す
                window = new(fullPath, Loc.F("Nf.027", section, sectionLabel, row.Label), PerformanceTableHeader, firstColumnName, key == "Params", GraphValueName(key));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                OperationHistory.Add($"表のウィンドウを開けない: {ex.GetType().Name}: {ex.Message}");
                MessageBox.Show(Loc.F("Nf.028", ErrorText.Describe(ex)), Loc.T("Nf.029"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            OperationHistory.Add($"表のウィンドウを開く: [{section}] {key} = {row.PathBox.Text}");
            window.Owner = Window.GetWindow(this);
            // 式から作れるのは、力行（段階5）とブレーキ（段階6）の引張力・電流（空車・満車）と無負荷電流（進段パラメーターは対象の外）
            TractionCalculator.TableKind? kind = GenerateKind(key);
            if (kind != null && _getParamText != null) window.EnableGenerate(kind.Value, _getParamText, GenerateTargets(folder, section), section == "Brake");
            // 抵抗制御で作れるのは、力行とブレーキの引張力・電流（空車・満車）。進段パラメーターから段の数と限流値を読む（ブレーキは ResistanceBrakeWindow）
            if (kind is TractionCalculator.TableKind.Force or TractionCalculator.TableKind.MaxForce or TractionCalculator.TableKind.Current or TractionCalculator.TableKind.MaxCurrent)
                window.EnableResistance(kind.Value, GenerateTargets(folder, section), ParamsPathOf(folder, section), section == "Brake");
            window.ShowDialog();
        }

        // まとめて作るときに書く、そのセクション（Power / Brake）の表のファイルのフルパス（パスに問題がなく、ファイルが見つかるものだけ）
        private Dictionary<TractionCalculator.TableKind, string> GenerateTargets(string folder, string section)
        {
            Dictionary<TractionCalculator.TableKind, string> targets = [];
            foreach (TableRow row in _rows.Where(item => item.Section == section))
            {
                TractionCalculator.TableKind? kind = GenerateKind(row.Key);
                string path = row.PathBox.Text;
                if (kind == null || path.Length == 0 || FindProblem(folder, path) != null) continue;
                string? fullPath = FullPathOf(folder, path);
                if (fullPath != null) targets[kind.Value] = fullPath;
            }
            return targets;
        }

        // そのセクション（Power / Brake）の進段パラメーターのフルパス（パスに問題がなく、ファイルが見つかるときだけ。なければ null）
        private string? ParamsPathOf(string folder, string section)
        {
            TableRow? row = _rows.FirstOrDefault(item => item.Section == section && item.Key == "Params");
            string path = row?.PathBox.Text ?? string.Empty;
            if (path.Length == 0 || FindProblem(folder, path) != null) return null;
            return FullPathOf(folder, path);
        }

        // 式から作る表の種類（ノッチのファイルのキーから。進段パラメーターなど作れない表は null）
        private static TractionCalculator.TableKind? GenerateKind(string key)
        {
            return key switch
            {
                "Force" => TractionCalculator.TableKind.Force,
                "MaxForce" => TractionCalculator.TableKind.MaxForce,
                "Current" => TractionCalculator.TableKind.Current,
                "MaxCurrent" => TractionCalculator.TableKind.MaxCurrent,
                "NoLoadCurrent" => TractionCalculator.TableKind.NoLoadCurrent,
                _ => null,
            };
        }

        // 表のウィンドウのグラフの縦の軸の名前（進段パラメーターは速度の表ではないので、グラフを出さない null）
        private static string? GraphValueName(string key)
        {
            return key switch
            {
                "Force" or "MaxForce" => Loc.T("Nf.030"),
                "Current" or "MaxCurrent" or "NoLoadCurrent" => Loc.T("Nf.031"),
                _ => null,
            };
        }

        // 下のテキストの [セクション] の「キー = パス」を書き換える（なければ足す）
        private void WritePath(string section, string key, string relativePath)
        {
            if (_editor == null) return;

            EditorTextHelper.ReplaceChangedText(_editor, BveTextParser.SetValue(_editor.Text, section, key, relativePath));
            OperationHistory.Add($"ノッチ入力: [{section}] {key} = {relativePath}");
        }
    }
}
