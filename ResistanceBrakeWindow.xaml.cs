using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using OxyPlot;
using static BveVehicleEditor.ResistanceCalculator;
using TableKind = BveVehicleEditor.TractionCalculator.TableKind;

namespace BveVehicleEditor
{
    /// <summary>
    /// 抵抗制御の発電ブレーキ（列が段ごと）の表（引張力・電流の空車と満車）を式から作るウィンドウ
    /// 表のウィンドウの「抵抗制御で作る…」から、ブレーキの表のときに開く（力行は ResistanceWindow。欄と計算がまったく違うので、画面を分けた）
    /// 限流値は進段パラメーターから、限流値の力・段 1 と最後の段の速度は今の表から読んで、最初の値にする。満車の表は空車と同じ値（力行と同じ。ユーザーの判断）
    /// 「表に入れる」で確認してから、開いている表は GeneratedRows で渡し、ほかの表はファイルに直接書く（PerformanceTableWriter）。入れた値はアプリを閉じるまで覚える
    /// 計算は ResistanceCalculator.Brake.cs、グラフは ChartBuilder
    /// </summary>
    public partial class ResistanceBrakeWindow : Window
    {
        /// <summary>
        /// 覚えておく値（アプリを閉じるまで）。Key（進段パラメーターの場所）が同じときだけ使う
        /// </summary>
        private sealed record Memory(string Key, Dictionary<string, string> Fields, HashSet<TableKind> CheckedKinds);

        // 作る表の並び
        private static readonly TableKind[] AllKinds = [TableKind.Force, TableKind.Current, TableKind.MaxForce, TableKind.MaxCurrent];

        // 最初に入れておく値（今の表や進段パラメーターから読めないとき）
        private const int DefaultSteps = 14;
        private const double DefaultLimit = 300;
        private const double DefaultRatedForce = 35000;
        private const double DefaultFirstSpeed = 110;
        private const double DefaultLastSpeed = 15;
        private const double DefaultSaturation = 1;
        private const double DefaultMaxSpeed = 120;
        private const double DefaultSpeedStep = 1;

        // 前に開いたときに入れた値（力行の画面とは別に覚える）
        private static Memory? _memory;

        private readonly TableKind _kind;
        private readonly IReadOnlyDictionary<TableKind, string> _targets;
        private readonly string _memoryKey;

        // 進段パラメーターから読んだ段の並び（限流値だけ使う。読めなければ null）
        private readonly StepLayout? _layout;

        // 今の表を読み取った結果（引張力と電流の両方が読めなければ null）
        private readonly BrakeTableAnalysis? _table;

        // 今の表の段の数（列の数）
        private readonly int _tableColumns;

        private readonly Dictionary<TableKind, CheckBox> _checks = [];
        private TableKind _previewKind;
        private readonly DispatcherTimer _recalculateTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
        private bool _isLoading = true;

        /// <summary>
        /// 「表に入れる」で作った、開いている表の行。閉じただけ、または開いている表をチェックしていなければ null
        /// </summary>
        public List<string[]>? GeneratedRows { get; private set; }

        /// <summary>
        /// ウィンドウを作る
        /// </summary>
        /// <param name="kind">開いている表の種類（ブレーキの引張力・電流の空車か満車）</param>
        /// <param name="openedRows">開いている表の今の行（コメントの行を除く。1 列目が速度）</param>
        /// <param name="targets">表の種類ごとのファイルのフルパス（[Brake] の表）</param>
        /// <param name="paramsPath">[Brake] の進段パラメーターのフルパス（なければ null）</param>
        internal ResistanceBrakeWindow(TableKind kind, IReadOnlyList<string[]> openedRows, IReadOnlyDictionary<TableKind, string> targets, string? paramsPath)
        {
            InitializeComponent();
            _kind = kind;
            _targets = targets;
            _memoryKey = paramsPath ?? string.Empty;
            _previewKind = ResistanceWindow.IsForceKind(kind) ? TableKind.Force : TableKind.Current;
            _recalculateTimer.Tick += (s, e) =>
            {
                _recalculateTimer.Stop();
                Recalculate();
            };

            // 今の表：開いている表は画面の今の行、もう 1 つはファイルから読む
            (List<double> Speeds, List<double[]> Values) opened = ParseRows(openedRows);
            _tableColumns = opened.Values.Count > 0 ? opened.Values[0].Length : 0;
            (List<double> Speeds, List<double[]> Values)? other = ResistanceWindow.ReadPairedTable(kind, targets);
            (List<double> Speeds, List<double[]> Values)? force = ResistanceWindow.IsForceKind(kind) ? opened : other;
            (List<double> Speeds, List<double[]> Values)? current = ResistanceWindow.IsForceKind(kind) ? other : opened;

            // 進段パラメーター（限流値）
            (_layout, _) = ResistanceWindow.ReadLayout(paramsPath, _tableColumns);
            TxtLayoutNote.Text = _layout != null
                ? Loc.F("Rb.001", Path.GetFileName(paramsPath))
                : Loc.T("Rb.002");
            if (force != null && current != null && _layout != null && force.Value.Values.Count > 0)
                _table = AnalyzeBrakeTable(force.Value.Speeds, force.Value.Values, current.Value.Values, _layout.Limits);

            Title = Loc.F("Rb.003", TractionCalculator.KindName(kind));
            TxtKind.Text = Loc.F("Rb.004", TractionCalculator.KindName(kind));
            PlotPreview.Controller = ChartBuilder.CreateController();
            BuildTargets();

            if (_memory != null && _memory.Key == _memoryKey) Restore(_memory);
            else FillDefaults(opened.Speeds);

            BtnFit.IsEnabled = _table != null;
            BtnFit.ToolTip = _table != null
                ? Loc.T("Rb.005")
                : Loc.T("Rb.006");

            _isLoading = false;
            Recalculate();
        }

        // 「作る表」のチェックと「グラフで見る表」
        private void BuildTargets()
        {
            foreach (TableKind kind in AllKinds)
            {
                bool isOpened = kind == _kind;
                bool hasFile = _targets.TryGetValue(kind, out string? path);
                string same = kind is TableKind.MaxForce or TableKind.MaxCurrent ? Loc.T("Rb.007") : string.Empty;
                CheckBox check = new()
                {
                    Content = isOpened ? Loc.F("Rb.008", TractionCalculator.KindName(kind), same) : hasFile ? $"{TractionCalculator.KindName(kind)}（{Path.GetFileName(path)}{same}）" : Loc.F("Rb.009", TractionCalculator.KindName(kind)),
                    IsEnabled = isOpened || hasFile,
                    IsChecked = isOpened,
                    Margin = new Thickness(0, 0, 14, 4),
                    ToolTip = isOpened ? Loc.T("Rb.010") : hasFile ? Loc.F("Rb.011", path) : Loc.T("Rb.012"),
                };
                check.Checked += (s, e) => { if (!_isLoading) Recalculate(); };
                check.Unchecked += (s, e) => { if (!_isLoading) Recalculate(); };
                _checks[kind] = check;
                PanelTargets.Children.Add(check);
            }
            CmbPreview.Items.Add(new ComboBoxItem { Content = Loc.T("Rb.013"), Tag = TableKind.Force });
            CmbPreview.Items.Add(new ComboBoxItem { Content = Loc.T("Rb.014"), Tag = TableKind.Current });
            CmbPreview.SelectedIndex = _previewKind == TableKind.Force ? 0 : 1;
        }

        private List<TableKind> CheckedKinds() => [.. AllKinds.Where(kind => _checks[kind].IsChecked == true)];

        // 覚える欄（名前は XAML の x:Name）
        private IEnumerable<TextBox> MemorizedBoxes =>
        [
            TxtSteps, TxtLimit, TxtRatedForce, TxtFirstSpeed, TxtLastSpeed, TxtSaturation, TxtSpeedDrop, TxtHighSaturation, TxtSwitchSpeeds, TxtMaxSpeed, TxtSpeedStep,
        ];

        // 進段パラメーター・今の表から読んだ値と、最初に入れておく値で欄を埋める
        private void FillDefaults(List<double> openedSpeeds)
        {
            int steps = _tableColumns > 0 ? _tableColumns : DefaultSteps;
            double limit = _table?.LimitCurrent ?? _layout?.Limits.FirstOrDefault(value => value > 0) ?? 0;
            if (limit <= 0) limit = DefaultLimit;
            List<double> read = _table != null ? [.. _table.SwitchSpeeds.Where(v => !double.IsNaN(v))] : [];

            TxtSteps.Text = steps.ToString(CultureInfo.InvariantCulture);
            TxtLimit.Text = TractionCalculator.Format(limit);
            TxtRatedForce.Text = TractionCalculator.Format(Math.Round(_table?.RatedForce ?? DefaultRatedForce));
            TxtFirstSpeed.Text = TractionCalculator.Format(read.Count > 0 ? Math.Round(read.Max(), 2) : DefaultFirstSpeed);
            TxtLastSpeed.Text = TractionCalculator.Format(read.Count > 0 ? Math.Round(read.Min(), 2) : DefaultLastSpeed);
            TxtSaturation.Text = TractionCalculator.Format(DefaultSaturation);
            TxtSpeedDrop.Text = "0";
            TxtHighSaturation.Text = "0";
            TxtSwitchSpeeds.Text = string.Empty;

            // 表の形は今の表に合わせる（刻みは行の間隔の中央値）
            List<double> gaps = [.. openedSpeeds.Zip(openedSpeeds.Skip(1), (a, b) => b - a).Where(gap => gap > 0).Order()];
            TxtMaxSpeed.Text = TractionCalculator.Format(openedSpeeds.Count > 0 ? openedSpeeds[^1] : DefaultMaxSpeed);
            TxtSpeedStep.Text = TractionCalculator.Format(Math.Round(gaps.Count > 0 ? gaps[gaps.Count / 2] : DefaultSpeedStep, 3));
        }

        private void Restore(Memory memory)
        {
            foreach (TextBox box in MemorizedBoxes)
            {
                if (memory.Fields.TryGetValue(box.Name, out string? text)) box.Text = text;
            }
            foreach (TableKind kind in AllKinds)
            {
                CheckBox check = _checks[kind];
                check.IsChecked = kind == _kind || (check.IsEnabled && memory.CheckedKinds.Contains(kind));
            }
        }

        private void Window_Closing(object? sender, CancelEventArgs e)
        {
            _memory = new(_memoryKey, MemorizedBoxes.ToDictionary(box => box.Name, box => box.Text), [.. CheckedKinds()]);
        }

        private void CmbPreview_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbPreview.SelectedItem is not ComboBoxItem { Tag: TableKind kind }) return;
            _previewKind = kind;
            if (_isLoading) return;
            Recalculate();
        }

        private void Input_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isLoading) return;
            _recalculateTimer.Stop();
            _recalculateTimer.Start();
        }

        /// <summary>
        /// 今の欄の値で計算する。問題があれば理由を出し、プレビューと「表に入れる」を止める。作れたら入力を返す（問題があれば null）
        /// </summary>
        private BrakeInput? Recalculate()
        {
            List<string> problems = [];
            BrakeInput? input = ReadInput(problems);
            if (CheckedKinds().Count == 0) problems.Add(Loc.T("Rb.015"));
            if (input != null) problems.AddRange(ValidateBrake(input).Where(problem => !problems.Contains(problem)));

            TxtProblems.Text = string.Join("\n", problems.Select(problem => "⚠ " + problem));
            TxtProblems.Visibility = problems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            BtnApply.IsEnabled = problems.Count == 0;
            if (input == null || problems.Count > 0)
            {
                TxtSummary.Text = string.Empty;
                ChartBuilder.Show(PlotPreview, ChartBuilder.CreateModel(TractionCalculator.ValueName(_previewKind), this));
                return null;
            }

            ShowSummary(input);
            DrawPreview(input);
            return input;
        }

        // 欄の値を読む。数字でない欄があれば理由を足して null
        private BrakeInput? ReadInput(List<string> problems)
        {
            double Read(TextBox box, string name)
            {
                if (double.TryParse(box.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)) return value;
                problems.Add(Loc.F("Rb.016", name, box.Text));
                return 0;
            }

            int steps = 0;
            if (!int.TryParse(TxtSteps.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out steps)) problems.Add(Loc.F("Rb.017", TxtSteps.Text));
            double limit = Read(TxtLimit, Loc.T("Rb.018"));
            double ratedForce = Read(TxtRatedForce, Loc.T("Rb.019"));
            double first = Read(TxtFirstSpeed, Loc.T("Rb.020"));
            double last = Read(TxtLastSpeed, Loc.T("Rb.021"));
            double saturation = Read(TxtSaturation, Loc.T("Rb.022"));
            // 前のバージョンで覚えた値には b・限流値より上の飽和がないので、空なら 0
            double drop = TxtSpeedDrop.Text.Trim().Length == 0 ? 0 : Read(TxtSpeedDrop, Loc.T("Rb.023"));
            double high = TxtHighSaturation.Text.Trim().Length == 0 ? 0 : Read(TxtHighSaturation, Loc.T("Rb.024"));
            double maxSpeed = Read(TxtMaxSpeed, Loc.T("Rb.025"));
            double step = Read(TxtSpeedStep, Loc.T("Rb.026"));

            List<double>? switchSpeeds = null;
            string[] parts = TxtSwitchSpeeds.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0)
            {
                switchSpeeds = [];
                foreach (string part in parts)
                {
                    if (double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out double speed)) switchSpeeds.Add(speed);
                    else problems.Add(Loc.F("Rb.027", part));
                }
            }
            if (step > 0 && maxSpeed / step >= 5001) problems.Add(Loc.T("Rb.028"));

            // 段ごとの限流値は、進段パラメーターの列の数と段の数が同じときだけ使う
            IReadOnlyList<double>? stepLimits = _layout != null && _layout.Limits.Count == steps ? _layout.Limits : null;
            return problems.Count > 0 ? null : new(limit, ratedForce, steps, first, last, saturation, maxSpeed, step, switchSpeeds, stepLimits, drop, high);
        }

        // 作る段のようす
        private void ShowSummary(BrakeInput input)
        {
            string how = input.StepSpeeds != null ? Loc.T("Rb.029") : Loc.F("Rb.030", input.FirstSpeed, input.LastSpeed);
            string limits = input.StepLimits != null && input.StepLimits.Distinct().Count() > 1 ? Loc.T("Rb.031") : string.Empty;
            double fade = input.LastSpeed * input.Saturation / (1 + input.Saturation);
            string text = Loc.F("Rb.032", input.Steps, how, limits, fade);
            int paramsColumns = _layout?.Limits.Count ?? 0;
            if (paramsColumns > 0 && paramsColumns != input.Steps) text += Loc.F("Rb.033", paramsColumns);
            else if (_tableColumns > 0 && _tableColumns != input.Steps) text += Loc.F("Rb.034", _tableColumns, input.Steps);
            TxtSummary.Text = text;
        }

        // プレビュー：段ごとの線と、全ノッチで速い速度から下がったときの線（式と今の表）
        private void DrawPreview(BrakeInput input)
        {
            PlotModel model = ChartBuilder.CreateModel(TractionCalculator.ValueName(_previewKind), this);
            List<double> speeds = TractionCalculator.Speeds(input.MaxSpeed, input.SpeedStep);
            bool isForce = _previewKind == TableKind.Force;
            double[] coefficients = BrakeCoefficients(input);
            for (int k = 0; k < input.Steps; k++)
            {
                double c = coefficients[k];
                IEnumerable<DataPoint> points = speeds.Select(speed =>
                {
                    (double x, double f) = BrakeStepAt(input, c, speed);
                    return new DataPoint(speed, isForce ? f : x * input.LimitCurrent);
                });
                ChartBuilder.AddLine(model, $"B{k + 1}", k, points);
            }

            (double[] force, double[] current) = BrakeEnvelope(input, speeds);
            double[] values = isForce ? force : current;
            ChartBuilder.AddEmphasisLine(model, Loc.T("Rb.035"), speeds.Select((speed, i) => new DataPoint(speed, values[i])), this);
            double top = values.DefaultIfEmpty(0).Max();
            if (_table != null)
            {
                IReadOnlyList<double> tableValues = isForce ? _table.EnvelopeForce : _table.EnvelopeCurrent;
                ChartBuilder.AddEmphasisLine(model, Loc.T("Rb.036"), _table.Speeds.Select((speed, i) => new DataPoint(speed, tableValues[i])), this, dashed: true);
                top = Math.Max(top, tableValues.Where(value => value < 1e7).DefaultIfEmpty(0).Max());
            }

            model.Axes[1].Minimum = 0;
            if (top > 0) model.Axes[1].Maximum = top * 1.3;
            ChartBuilder.Show(PlotPreview, model);
            ChartBuilder.ApplyTrackerStyle(PlotPreview);
        }

        // 今の表に合わせる：限流値・限流値の力・段 1 と最後の段の速度・飽和 s・段ごとの切り替わる速度を、今の表に一番合う値にする
        private void BtnFit_Click(object sender, RoutedEventArgs e)
        {
            if (_table == null) return;
            List<string> problems = [];
            BrakeInput? current = ReadInput(problems);
            if (current == null)
            {
                MessageBox.Show(Loc.F("Rb.037", string.Join("\n", problems)), Loc.T("Rb.038"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            Cursor = System.Windows.Input.Cursors.Wait;
            BrakeInput input = current with { StepSpeeds = null, StepLimits = _layout != null && _layout.Limits.Count == current.Steps ? _layout.Limits : null };
            (double saturation, double scale, bool useSpeeds, double drop, double high, double error) = FitBrake(input, _table);
            Cursor = null;
            BrakeInput fitted = WithBrakeTable(input, _table, saturation, scale, useSpeeds, drop, high);

            _isLoading = true;
            TxtLimit.Text = TractionCalculator.Format(fitted.LimitCurrent);
            TxtRatedForce.Text = TractionCalculator.Format(Math.Round(fitted.RatedForce));
            TxtFirstSpeed.Text = TractionCalculator.Format(fitted.FirstSpeed);
            TxtLastSpeed.Text = TractionCalculator.Format(fitted.LastSpeed);
            TxtSaturation.Text = TractionCalculator.Format(saturation);
            TxtSpeedDrop.Text = TractionCalculator.Format(drop);
            TxtHighSaturation.Text = TractionCalculator.Format(high);
            TxtSwitchSpeeds.Text = fitted.StepSpeeds != null ? string.Join(",", fitted.StepSpeeds.Select(TractionCalculator.Format)) : string.Empty;
            _isLoading = false;

            // 全ノッチの力・電流のずれ
            (double[] force, double[] cur) = BrakeEnvelope(fitted, _table.Speeds);
            double forceError = 0, currentError = 0;
            for (int i = 0; i < _table.Speeds.Count; i++)
            {
                forceError += Math.Abs(force[i] - _table.EnvelopeForce[i]) / Math.Max(_table.EnvelopeForce[i], 0.05 * fitted.RatedForce);
                currentError += Math.Abs(cur[i] - _table.EnvelopeCurrent[i]) / Math.Max(_table.EnvelopeCurrent[i], 0.05 * fitted.LimitCurrent);
            }
            int count = Math.Max(1, _table.Speeds.Count);
            string speedsNote = fitted.StepSpeeds != null ? Loc.T("Rb.039") : Loc.T("Rb.040");
            string scaleNote = Math.Abs(scale - 1) > 0.005 ? Loc.F("Rb.041", scale) : string.Empty;
            TxtFitResult.Text = Loc.F("Rb.042", forceError / count * 100, currentError / count * 100, speedsNote, scaleNote);
            OperationHistory.Add($"抵抗制御で作る（ブレーキ）: 今の表に合わせる s={saturation} b={drop} 上の飽和={high} 力の倍率={scale:0.###} 写す={useSpeeds}（ずれ {error:0.###}）");
            Recalculate();
        }

        // 表に入れる：チェックした表をまとめて作る（開いている表は GeneratedRows、ほかはファイルに直接書く）
        private void BtnApply_Click(object sender, RoutedEventArgs e)
        {
            BrakeInput? input = Recalculate();
            if (input == null) return;

            List<string[]> forceRows = BuildBrakeRows(input, TableKind.Force);
            List<string[]> currentRows = BuildBrakeRows(input, TableKind.Current);
            if (forceRows.Count == 0 || currentRows.Count == 0) return;
            Dictionary<TableKind, List<string[]>> built = CheckedKinds().ToDictionary(kind => kind, kind => ResistanceWindow.IsForceKind(kind) ? forceRows : currentRows);

            List<string> plan = [.. built.Keys.Select(kind => kind == _kind
                ? Loc.F("Rb.043", TractionCalculator.KindName(kind))
                : Loc.F("Rb.044", TractionCalculator.KindName(kind), Path.GetFileName(_targets[kind])))];
            MessageBoxResult result = MessageBox.Show(
                Loc.F("Rb.045", forceRows.Count, input.Steps, string.Join("\n", plan)),
                Loc.T("Rb.046"), MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;

            List<string> written = [];
            List<string> failed = [];
            foreach ((TableKind kind, List<string[]> rows) in built.Where(pair => pair.Key != _kind))
            {
                string path = _targets[kind];
                try
                {
                    string backup = PerformanceTableWriter.WriteRows(path, rows, ResistanceWindow.PerformanceTableHeader);
                    written.Add(Loc.F("Rb.047", TractionCalculator.KindName(kind), Path.GetFileName(path), Path.GetFileName(backup)));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    OperationHistory.Add($"抵抗制御で作る（ブレーキ）: 書けない {path}（{ex.GetType().Name}: {ex.Message}）");
                    failed.Add(Loc.F("Rb.048", TractionCalculator.KindName(kind), Path.GetFileName(path), ErrorText.Describe(ex)));
                }
            }

            if (written.Count > 0 || failed.Count > 0)
            {
                string message = written.Count > 0 ? Loc.F("Rb.049", string.Join("\n", written)) : string.Empty;
                if (failed.Count > 0) message += Loc.F("Rb.050", (message.Length > 0 ? "\n\n" : string.Empty), string.Join("\n", failed));
                MessageBox.Show(message, failed.Count > 0 ? Loc.T("Rb.051") : Loc.T("Rb.052"), MessageBoxButton.OK, failed.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
            }

            OperationHistory.Add($"抵抗制御で作る（ブレーキ）: {string.Join("・", built.Keys)}（{input.Steps} 段）");
            GeneratedRows = built.TryGetValue(_kind, out List<string[]>? opened) ? opened : null;
            DialogResult = true;
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
