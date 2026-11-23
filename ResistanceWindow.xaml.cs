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
    /// 抵抗制御（直並列・弱め界磁。列が進段ごと）の力行の表（引張力・電流の空車と満車）を式から作るウィンドウ
    /// 表のウィンドウの「抵抗制御で作る…」から開く（今の「式から作る…」とは別のボタン。ユーザーの判断）。満車の表は空車と同じ値で作る（ユーザーの判断）
    /// 段の数と限流値は進段パラメーターから、全界磁の点と弱め界磁の界磁の割合は今の表から読んで、最初の値にする
    /// 「表に入れる」で確認してから、開いている表は GeneratedRows で渡し、ほかの表はファイルに直接書く（PerformanceTableWriter）。入れた値はアプリを閉じるまで覚える
    /// 計算は ResistanceCalculator、グラフは ChartBuilder
    /// </summary>
    public partial class ResistanceWindow : Window
    {
        /// <summary>
        /// 覚えておく値（アプリを閉じるまで）。Key（進段パラメーターの場所）が同じときだけ使う
        /// </summary>
        private sealed record Memory(string Key, Dictionary<string, string> Fields, HashSet<TableKind> CheckedKinds);

        // 作る表の並び（画面の「作る表」のチェックの並び）
        private static readonly TableKind[] AllKinds = [TableKind.Force, TableKind.Current, TableKind.MaxForce, TableKind.MaxCurrent];

        // 表のファイルの 1 行目のヘッダー（NotchForm の PerformanceTableHeader と同じ）
        internal const string PerformanceTableHeader = "BveTs Vehicle Performance Table";

        // 最初に入れておく値（今の表や進段パラメーターから読めないとき）
        private const int DefaultStartSteps = 2;
        private const double DefaultLimit = 300;
        private const double DefaultRatedSpeed = 40;
        private const double DefaultRatedForce = 45000;
        private const double DefaultMaxSpeed = 120;
        private const double DefaultSpeedStep = 1;
        private const double DefaultStrongestField = 0.9;
        private const double DefaultWeakestField = 0.4;
        private const double DefaultSeriesVoltage = 0.5;

        // 前に開いたときに入れた値
        private static Memory? _memory;

        // 開いている表の種類
        private readonly TableKind _kind;

        // 表の種類ごとのファイルのフルパス（ノッチのファイルに書かれていない・見つからない表は入れない）
        private readonly IReadOnlyDictionary<TableKind, string> _targets;

        // 覚えておくときの鍵（進段パラメーターのフルパス。なければ空）
        private readonly string _memoryKey;

        // 進段パラメーターから読んだ段の並び（読めなければ null）
        private readonly StepLayout? _layout;

        // 今の表（引張力と電流。「今の表に合わせる」とプレビューの比べる線に使う。読めなければ null）
        private readonly (List<double> Speeds, List<double[]> Values)? _tableForce;
        private readonly (List<double> Speeds, List<double[]> Values)? _tableCurrent;

        // 今の表の段の数（列の数）
        private readonly int _tableColumns;

        // 「作る表」のチェック
        private readonly Dictionary<TableKind, CheckBox> _checks = [];

        // グラフで見る表（引張力か電流）
        private TableKind _previewKind;

        // 打つたびに描き直すと重いので、打ち終わって少し待ってから 1 回だけ描き直す
        private readonly DispatcherTimer _recalculateTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };

        // 作り終わるまで（コンストラクターで欄に値を入れている間）は描き直さない
        private bool _isLoading = true;

        /// <summary>
        /// 「表に入れる」で作った、開いている表の行（1 列目が速度、2 列目から段ごとの値）。閉じただけ、または開いている表をチェックしていなければ null
        /// </summary>
        public List<string[]>? GeneratedRows { get; private set; }

        /// <summary>
        /// ウィンドウを作る
        /// </summary>
        /// <param name="kind">開いている表の種類（引張力・電流の空車か満車）</param>
        /// <param name="openedRows">開いている表の今の行（コメントの行を除く。1 列目が速度）</param>
        /// <param name="targets">表の種類ごとのファイルのフルパス（まとめて作るときに、ほかの表を書く場所）</param>
        /// <param name="paramsPath">進段パラメーターのフルパス（なければ null）</param>
        internal ResistanceWindow(TableKind kind, IReadOnlyList<string[]> openedRows, IReadOnlyDictionary<TableKind, string> targets, string? paramsPath)
        {
            InitializeComponent();
            _kind = kind;
            _targets = targets;
            _memoryKey = paramsPath ?? string.Empty;
            _previewKind = IsForceKind(kind) ? TableKind.Force : TableKind.Current;
            _recalculateTimer.Tick += (s, e) =>
            {
                _recalculateTimer.Stop();
                Recalculate();
            };

            // 今の表：開いている表は画面の今の行、もう 1 つ（引張力なら電流）はファイルから読む
            (List<double> Speeds, List<double[]> Values) opened = ParseRows(openedRows);
            _tableColumns = opened.Values.Count > 0 ? opened.Values[0].Length : 0;
            (List<double>, List<double[]>)? other = ReadPairedTable(kind, _targets);
            _tableForce = IsForceKind(kind) ? opened : other;
            _tableCurrent = IsForceKind(kind) ? other : opened;

            // 進段パラメーター（段の数と限流値）
            string layoutNote;
            (_layout, layoutNote) = ReadLayout(paramsPath, _tableColumns);
            TxtLayoutNote.Text = layoutNote;

            Title = Loc.F("Rw.001", TractionCalculator.KindName(kind));
            TxtKind.Text = Loc.F("Rw.002", TractionCalculator.KindName(kind));
            PlotPreview.Controller = ChartBuilder.CreateController();
            BuildTargets();

            // 同じ進段パラメーターで前に入れた値があれば、それから始める
            if (_memory != null && _memory.Key == _memoryKey) Restore(_memory);
            else FillDefaults(opened.Speeds);

            BtnFit.IsEnabled = _tableForce != null && _tableCurrent != null;
            BtnFit.ToolTip = BtnFit.IsEnabled
                ? Loc.T("Rw.003")
                : Loc.T("Rw.004");

            _isLoading = false;
            Recalculate();
        }

        // 引張力の表か（空車・満車）
        internal static bool IsForceKind(TableKind kind) => kind is TableKind.Force or TableKind.MaxForce;

        // 開いている表と組になる表（引張力なら電流、電流なら引張力）をファイルから読む。空車の表を先に探し、なければ満車
        internal static (List<double>, List<double[]>)? ReadPairedTable(TableKind kind, IReadOnlyDictionary<TableKind, string> targets)
        {
            TableKind[] candidates = IsForceKind(kind) ? [TableKind.Current, TableKind.MaxCurrent] : [TableKind.Force, TableKind.MaxForce];
            foreach (TableKind candidate in candidates)
            {
                if (!targets.TryGetValue(candidate, out string? path)) continue;
                try
                {
                    FileItem file = new(Path.GetFileName(path), PerformanceTableHeader);
                    file.Load(path);
                    if (!file.IsLoaded) continue;
                    IEnumerable<string[]> rows = file.TextContent.Replace("\r\n", "\n").Split('\n').Skip(1)
                        .Where(line => !line.TrimStart().StartsWith('#'))
                        .Select(line => line.Split(','));
                    (List<double> speeds, List<double[]> values) = ParseRows(rows);
                    if (values.Count > 0) return (speeds, values);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    OperationHistory.Add($"抵抗制御で作る: 表を読めない {path}（{ex.GetType().Name}: {ex.Message}）");
                }
            }
            return null;
        }

        // 進段パラメーターを読む。段の並びと、画面に出す説明を返す
        internal static (StepLayout?, string) ReadLayout(string? paramsPath, int columns)
        {
            if (paramsPath == null) return (null, Loc.T("Rw.005"));
            try
            {
                FileItem file = new(Path.GetFileName(paramsPath), PerformanceTableHeader);
                file.Load(paramsPath);
                if (!file.IsLoaded) return (null, Loc.T("Rw.006"));
                StepLayout layout = ReadStepLayout(file.TextContent, columns);
                string name = Path.GetFileName(paramsPath);
                return (layout, layout.FromHeader
                    ? Loc.F("Rw.007", name)
                    : Loc.F("Rw.008", name));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                OperationHistory.Add($"抵抗制御で作る: 進段パラメーターを読めない {paramsPath}（{ex.GetType().Name}: {ex.Message}）");
                return (null, Loc.F("Rw.009", ErrorText.Describe(ex)));
            }
        }

        // 「作る表」のチェックと「グラフで見る表」の選ぶ欄を作る（開いている表と、ファイルが見つかる表だけ選べる）
        private void BuildTargets()
        {
            foreach (TableKind kind in AllKinds)
            {
                bool isOpened = kind == _kind;
                bool hasFile = _targets.TryGetValue(kind, out string? path);
                string same = kind is TableKind.MaxForce or TableKind.MaxCurrent ? Loc.T("Rw.010") : string.Empty;
                CheckBox check = new()
                {
                    Content = isOpened ? Loc.F("Rw.011", TractionCalculator.KindName(kind), same) : hasFile ? $"{TractionCalculator.KindName(kind)}（{Path.GetFileName(path)}{same}）" : Loc.F("Rw.012", TractionCalculator.KindName(kind)),
                    IsEnabled = isOpened || hasFile,
                    IsChecked = isOpened,
                    Margin = new Thickness(0, 0, 14, 4),
                    ToolTip = isOpened ? Loc.T("Rw.013") : hasFile ? Loc.F("Rw.014", path) : Loc.T("Rw.015"),
                };
                check.Checked += (s, e) => { if (!_isLoading) Recalculate(); };
                check.Unchecked += (s, e) => { if (!_isLoading) Recalculate(); };
                _checks[kind] = check;
                PanelTargets.Children.Add(check);
            }
            CmbPreview.Items.Add(new ComboBoxItem { Content = Loc.T("Rw.016"), Tag = TableKind.Force });
            CmbPreview.Items.Add(new ComboBoxItem { Content = Loc.T("Rw.017"), Tag = TableKind.Current });
            CmbPreview.SelectedIndex = _previewKind == TableKind.Force ? 0 : 1;
        }

        // チェックしている表（並びは AllKinds の順）
        private List<TableKind> CheckedKinds() => [.. AllKinds.Where(kind => _checks[kind].IsChecked == true)];

        // 欄の一覧（覚えるとき・戻すときに使う。名前は XAML の x:Name）
        private IEnumerable<TextBox> MemorizedBoxes =>
        [
            TxtStart, TxtSeries, TxtParallel, TxtWeakCount, TxtLimit, TxtWeakLimit, TxtSeriesVoltage, TxtFields, TxtSwitchSpeeds,
            TxtRatedSpeed, TxtRatedForce, TxtSaturation, TxtInternal, TxtLoss, TxtWeakSaturation, TxtWeakResistance, TxtWeakForceExponent, TxtMaxSpeed, TxtSpeedStep,
        ];

        // 進段パラメーター・今の表から読んだ値と、最初に入れておく値で欄を埋める
        private void FillDefaults(List<double> openedSpeeds)
        {
            int series = _layout?.SeriesSteps ?? Math.Max(1, _tableColumns);
            int parallel = _layout?.ParallelSteps ?? 0;
            int weak = _layout?.WeakSteps ?? 0;
            double limit = _layout?.Limits.FirstOrDefault(value => value > 0) ?? 0;
            if (limit <= 0) limit = DefaultLimit;
            double weakLimit = weak > 0 && _layout != null && _layout.Limits.Count > 0 && _layout.Limits[^1] > 0 ? _layout.Limits[^1] : limit;

            TxtSeries.Text = series.ToString(CultureInfo.InvariantCulture);
            TxtParallel.Text = parallel.ToString(CultureInfo.InvariantCulture);
            TxtWeakCount.Text = weak.ToString(CultureInfo.InvariantCulture);
            TxtLimit.Text = TractionCalculator.Format(limit);
            TxtWeakLimit.Text = TractionCalculator.Format(weakLimit);

            MotorShape shape = MotorShape.Default;
            TxtSaturation.Text = TractionCalculator.Format(shape.Saturation);
            TxtInternal.Text = TractionCalculator.Format(shape.InternalResistance);
            TxtLoss.Text = TractionCalculator.Format(shape.Loss);

            // 全界磁の点と界磁の割合：今の表から読めればその値、読めなければ仮の値（界磁の割合は 90% から 40% まで同じ間隔）
            TableAnalysis? table = Analyze(weak, limit);
            TxtRatedSpeed.Text = TractionCalculator.Format(table?.RatedSpeed ?? DefaultRatedSpeed);
            TxtRatedForce.Text = TractionCalculator.Format(Math.Round(table?.RatedForce ?? DefaultRatedForce));
            IEnumerable<double> fields = table != null
                ? table.FluxRatios.Select(ratio => FieldFromFluxRatio(shape, ratio))
                : Enumerable.Range(0, weak).Select(i => weak == 1 ? DefaultWeakestField : DefaultStrongestField - (DefaultStrongestField - DefaultWeakestField) * i / (weak - 1));
            TxtFields.Text = FormatFields(fields);

            // 起動の段の数と直列の電圧の割合：今の表から読めればその値（起動の段は直列の段の数まで）
            int start = table != null ? Math.Clamp(TableStartSteps(table), 0, series) : Math.Min(DefaultStartSteps, series);
            TxtStart.Text = start.ToString(CultureInfo.InvariantCulture);
            double seriesVoltage = DefaultSeriesVoltage;
            if (table != null)
            {
                ResistanceInput probe = new(limit, table.RatedSpeed, table.RatedForce, start, series, parallel, [], weakLimit, shape, DefaultMaxSpeed, DefaultSpeedStep);
                seriesVoltage = TableSeriesVoltage(probe, table, shape) ?? DefaultSeriesVoltage;
            }
            TxtSeriesVoltage.Text = TractionCalculator.Format(seriesVoltage);

            // 表の形は今の表に合わせる
            double maxSpeed = openedSpeeds.Count > 0 ? openedSpeeds[^1] : DefaultMaxSpeed;
            // 刻みは行の間隔の中央値（間隔がそろっていない表もあるため。211 系は最初の 2 行が 0.3 km/h 差）
            List<double> gaps = [.. openedSpeeds.Zip(openedSpeeds.Skip(1), (a, b) => b - a).Where(gap => gap > 0).Order()];
            double step = gaps.Count > 0 ? gaps[gaps.Count / 2] : DefaultSpeedStep;
            TxtMaxSpeed.Text = TractionCalculator.Format(maxSpeed);
            TxtSpeedStep.Text = TractionCalculator.Format(Math.Round(step, 3));
        }

        // 界磁の割合を「80,65,50」の形にする
        private static string FormatFields(IEnumerable<double> fields) => string.Join(",", fields.Select(field => (field * 100).ToString("0.#", CultureInfo.InvariantCulture)));

        // 覚えていた値で欄とチェックを戻す（チェックは選べる表だけ。開いている表はいつもチェック）
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

        // 閉じるとき（表に入れたときも、閉じただけのときも）に、今の値を覚える
        private void Window_Closing(object? sender, CancelEventArgs e)
        {
            Dictionary<string, string> fields = MemorizedBoxes.ToDictionary(box => box.Name, box => box.Text);
            _memory = new(_memoryKey, fields, [.. CheckedKinds()]);
        }

        // 今の表を読み取る（弱め界磁の段の数と限流値で。引張力と電流の両方がなければ null）
        private TableAnalysis? Analyze(int weakSteps, double limit)
        {
            if (_tableForce == null || _tableCurrent == null) return null;
            int columns = _tableForce.Value.Values.Count > 0 ? _tableForce.Value.Values[0].Length : 0;
            IReadOnlyList<double> limits = _layout != null && _layout.Limits.Count == columns ? _layout.Limits : [.. Enumerable.Repeat(limit, columns)];
            return AnalyzeTable(_tableForce.Value.Speeds, _tableForce.Value.Values, _tableCurrent.Value.Values, limits, weakSteps);
        }

        // グラフで見る表を選んだら描き直す
        private void CmbPreview_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbPreview.SelectedItem is not ComboBoxItem { Tag: TableKind kind }) return;
            _previewKind = kind;
            if (_isLoading) return;
            Recalculate();
        }

        // 欄を打ったら、少しあとに描き直す
        private void Input_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isLoading) return;
            _recalculateTimer.Stop();
            _recalculateTimer.Start();
        }

        /// <summary>
        /// 今の欄の値で計算する。問題があれば理由を出し、プレビューと「表に入れる」を止める。作れたら入力を返す（問題があれば null）
        /// </summary>
        private ResistanceInput? Recalculate()
        {
            List<string> problems = [];
            ResistanceInput? input = ReadInput(problems);
            if (CheckedKinds().Count == 0) problems.Add(Loc.T("Rw.018"));
            if (input != null) problems.AddRange(Validate(input).Where(problem => !problems.Contains(problem)));

            TxtProblems.Text = string.Join("\n", problems.Select(problem => "⚠ " + problem));
            TxtProblems.Visibility = problems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            BtnApply.IsEnabled = problems.Count == 0;
            if (input == null || problems.Count > 0)
            {
                TxtSummary.Text = string.Empty;
                ChartBuilder.Show(PlotPreview, ChartBuilder.CreateModel(TractionCalculator.ValueName(_previewKind), this));
                return null;
            }

            StepPlan plan = BuildSteps(input);
            ShowSummary(input, plan);
            DrawPreview(input, plan);
            return input;
        }

        // 欄の値を読む。数字でない欄があれば理由を足して null
        private ResistanceInput? ReadInput(List<string> problems)
        {
            double Read(TextBox box, string name)
            {
                if (double.TryParse(box.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)) return value;
                problems.Add(Loc.F("Rw.019", name, box.Text));
                return 0;
            }
            int ReadCount(TextBox box, string name)
            {
                if (int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)) return value;
                problems.Add(Loc.F("Rw.020", name, box.Text));
                return 0;
            }

            int start = ReadCount(TxtStart, Loc.T("Rw.021"));
            int series = ReadCount(TxtSeries, Loc.T("Rw.022"));
            int parallel = ReadCount(TxtParallel, Loc.T("Rw.023"));
            int weak = ReadCount(TxtWeakCount, Loc.T("Rw.024"));
            double limit = Read(TxtLimit, Loc.T("Rw.025"));
            double weakLimit = Read(TxtWeakLimit, Loc.T("Rw.026"));
            double ratedSpeed = Read(TxtRatedSpeed, Loc.T("Rw.027"));
            double ratedForce = Read(TxtRatedForce, Loc.T("Rw.028"));
            // 弱め界磁用の s・r は空なら 0（全界磁と同じ）
            double ReadOptional(TextBox box, string name) => string.IsNullOrWhiteSpace(box.Text) ? 0 : Read(box, name);
            MotorShape shape = new(Read(TxtSaturation, Loc.T("Rw.029")), Read(TxtInternal, Loc.T("Rw.030")), Read(TxtLoss, Loc.T("Rw.031")),
                ReadOptional(TxtWeakSaturation, Loc.T("Rw.032")), ReadOptional(TxtWeakResistance, Loc.T("Rw.033")), ReadOptional(TxtWeakForceExponent, Loc.T("Rw.034")));
            double maxSpeed = Read(TxtMaxSpeed, Loc.T("Rw.035"));
            double step = Read(TxtSpeedStep, Loc.T("Rw.036"));
            double seriesVoltage = string.IsNullOrWhiteSpace(TxtSeriesVoltage.Text) ? DefaultSeriesVoltage : Read(TxtSeriesVoltage, Loc.T("Rw.037"));

            // 界磁の割合（%。「,」で区切る。空なら弱め界磁なし）
            List<double> fields = [];
            foreach (string part in TxtFields.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out double percent)) fields.Add(percent / 100);
                else problems.Add(Loc.F("Rw.038", part));
            }
            if (problems.Count == 0 && fields.Count != weak) problems.Add(Loc.F("Rw.039", fields.Count, weak));

            // 段ごとの切り替わる速度（km/h。「,」で区切る。空なら null で、区間の中は同じ跳ね上がりの倍率）
            List<double>? switchSpeeds = null;
            string[] speedParts = TxtSwitchSpeeds.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (speedParts.Length > 0)
            {
                switchSpeeds = [];
                foreach (string part in speedParts)
                {
                    if (double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out double speed)) switchSpeeds.Add(speed);
                    else problems.Add(Loc.F("Rw.040", part));
                }
            }
            if (step > 0 && maxSpeed / step >= 5001) problems.Add(Loc.T("Rw.041"));

            return problems.Count > 0 ? null : new(limit, ratedSpeed, ratedForce, start, series, parallel, fields, weakLimit, shape, maxSpeed, step, switchSpeeds, seriesVoltage);
        }

        // 作った段のようす（段の数・跳ね上がりの倍率・進段パラメーターや今の表の列の数との違い）
        private void ShowSummary(ResistanceInput input, StepPlan plan)
        {
            List<string> parts = [Loc.F("Rw.042", plan.Steps.Count)];
            if (plan.SeriesJump != null) parts.Add(Loc.F("Rw.043", (input.ParallelSteps > 0 ? Loc.T("Rw2.001") : Loc.T("Rw2.002")), plan.SeriesJump));
            if (plan.ParallelJump != null) parts.Add(Loc.F("Rw.044", plan.ParallelJump));
            string how = input.StepSpeeds != null ? Loc.T("Rw.045") : Loc.T("Rw.046");
            string text = string.Join(Loc.T("Rw.047"), parts) + Loc.F("Rw.048", how);

            int paramsColumns = _layout?.Limits.Count ?? 0;
            if (paramsColumns > 0 && paramsColumns != plan.Steps.Count) text += Loc.F("Rw.049", paramsColumns);
            else if (_tableColumns > 0 && _tableColumns != plan.Steps.Count) text += Loc.F("Rw.050", _tableColumns, plan.Steps.Count);
            TxtSummary.Text = text;
        }

        // プレビュー：段ごとの線と、全ノッチで走ったときの線（式と今の表）
        private void DrawPreview(ResistanceInput input, StepPlan plan)
        {
            PlotModel model = ChartBuilder.CreateModel(TractionCalculator.ValueName(_previewKind), this);
            List<double> speeds = TractionCalculator.Speeds(input.MaxSpeed, input.SpeedStep);
            bool isForce = _previewKind == TableKind.Force;

            List<string> names = StepNames(plan);
            for (int k = 0; k < plan.Steps.Count; k++)
            {
                StepInfo step = plan.Steps[k];
                IEnumerable<DataPoint> points = speeds.Select(speed =>
                {
                    double x = CurrentRatioAt(input, step, speed);
                    return new DataPoint(speed, isForce ? ForceAt(input, step, x, speed) : x * input.LimitCurrent);
                });
                ChartBuilder.AddLine(model, names[k], k, points);
            }

            // 全ノッチで走ったときの線（式）。縦の軸の上の端は、この線と今の表の線が見えるように決める（低い速度の段の大きな値で潰れないように）
            (double[] force, double[] current) = Envelope(input, plan, speeds);
            double[] values = isForce ? force : current;
            ChartBuilder.AddEmphasisLine(model, Loc.T("Rw.051"), speeds.Select((speed, i) => new DataPoint(speed, values[i])), this);
            double top = values.DefaultIfEmpty(0).Max();

            TableAnalysis? table = Analyze(input.FieldRatios.Count, input.LimitCurrent);
            if (table != null)
            {
                IReadOnlyList<double> tableValues = isForce ? table.EnvelopeForce : table.EnvelopeCurrent;
                ChartBuilder.AddEmphasisLine(model, Loc.T("Rw.052"), table.Speeds.Select((speed, i) => new DataPoint(speed, tableValues[i])), this, dashed: true);
                top = Math.Max(top, tableValues.Where(value => value < 1e7).DefaultIfEmpty(0).Max());
            }

            model.Axes[1].Minimum = 0;
            if (top > 0) model.Axes[1].Maximum = top * 1.3;
            ChartBuilder.Show(PlotPreview, model);
            ChartBuilder.ApplyTrackerStyle(PlotPreview);
        }

        // 段の名前（進段パラメーターの見出しと同じ形：S1・P1・WF1）
        private static List<string> StepNames(StepPlan plan)
        {
            int s = 0, p = 0, w = 0;
            return [.. plan.Steps.Select(step => step.Kind switch
            {
                "並列" => $"P{++p}",
                "弱め界磁" => $"WF{++w}",
                _ => $"S{++s}",
            })];
        }

        // 今の表に合わせる：全界磁の点・界磁の割合・飽和 s・中の抵抗 r・損失 L を、今の表に一番合う値にする
        private void BtnFit_Click(object sender, RoutedEventArgs e)
        {
            List<string> problems = [];
            int Count(TextBox box, string name)
            {
                if (int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) && value >= 0) return value;
                problems.Add(Loc.F("Rw.055", name));
                return 0;
            }
            double Number(TextBox box, string name)
            {
                if (double.TryParse(box.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && value > 0) return value;
                problems.Add(Loc.F("Rw.056", name));
                return 0;
            }

            int start = Count(TxtStart, Loc.T("Rw.057")), series = Count(TxtSeries, Loc.T("Rw.058")), parallel = Count(TxtParallel, Loc.T("Rw.059")), weak = Count(TxtWeakCount, Loc.T("Rw.060"));
            double limit = Number(TxtLimit, Loc.T("Rw.061")), weakLimit = Number(TxtWeakLimit, Loc.T("Rw.062"));
            double maxSpeed = Number(TxtMaxSpeed, Loc.T("Rw.063")), step = Number(TxtSpeedStep, Loc.T("Rw.064"));
            if (problems.Count > 0)
            {
                MessageBox.Show(Loc.F("Rw.065", string.Join("\n", problems)), Loc.T("Rw.066"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            TableAnalysis? table = Analyze(weak, limit);
            if (table == null)
            {
                MessageBox.Show(Loc.F("Rw.067", weak, limit), Loc.T("Rw.068"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            ResistanceInput input = new(limit, table.RatedSpeed, table.RatedForce, start, series, parallel, [], weakLimit, MotorShape.Default, maxSpeed, step);
            List<string> invalid = Validate(WithShape(input, table, MotorShape.Default));
            if (invalid.Count > 0)
            {
                MessageBox.Show(Loc.F("Rw.069", string.Join("\n", invalid)), Loc.T("Rw.070"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            Cursor = System.Windows.Input.Cursors.Wait;
            (MotorShape shape, double scale, bool useSpeeds, double error) = Fit(input, table);
            Cursor = null;
            ResistanceInput fitted = WithShape(input, table, shape, scale, useSpeeds);

            _isLoading = true;
            TxtRatedSpeed.Text = TractionCalculator.Format(Math.Round(fitted.RatedSpeed, 2));
            TxtRatedForce.Text = TractionCalculator.Format(Math.Round(fitted.RatedForce));
            TxtFields.Text = FormatFields(fitted.FieldRatios);
            TxtSaturation.Text = TractionCalculator.Format(shape.Saturation);
            TxtInternal.Text = TractionCalculator.Format(shape.InternalResistance);
            TxtLoss.Text = TractionCalculator.Format(shape.Loss);
            TxtSeriesVoltage.Text = TractionCalculator.Format(fitted.SeriesVoltage);
            TxtStart.Text = fitted.StartSteps.ToString(CultureInfo.InvariantCulture);
            TxtWeakSaturation.Text = shape.WeakSaturation > 0 ? TractionCalculator.Format(shape.WeakSaturation) : string.Empty;
            TxtWeakResistance.Text = shape.WeakResistance > 0 ? TractionCalculator.Format(shape.WeakResistance) : string.Empty;
            TxtWeakForceExponent.Text = shape.WeakForceExponent > 0 ? TractionCalculator.Format(shape.WeakForceExponent) : string.Empty;
            TxtSwitchSpeeds.Text = fitted.StepSpeeds != null ? string.Join(",", fitted.StepSpeeds.Select(TractionCalculator.Format)) : string.Empty;
            _isLoading = false;

            // 全ノッチで走ったときの、速度ごとの力・電流のずれと、到達時間のずれ（いちばん大きいもの）
            StepPlan plan = BuildSteps(fitted);
            (double[] force, double[] current) = Envelope(fitted, plan, table.Speeds);
            double forceError = 0, currentError = 0;
            for (int i = 0; i < table.Speeds.Count; i++)
            {
                forceError += Math.Abs(force[i] - table.EnvelopeForce[i]) / Math.Max(Math.Abs(table.EnvelopeForce[i]), 0.05 * fitted.RatedForce);
                currentError += Math.Abs(current[i] - table.EnvelopeCurrent[i]) / Math.Max(table.EnvelopeCurrent[i], 0.05 * fitted.LimitCurrent);
            }
            int count = Math.Max(1, table.Speeds.Count);
            List<double> times = TimeRatios(force, table, fitted.RatedForce);
            double timeError = times.Count > 0 ? times.Max(ratio => Math.Abs(ratio - 1)) : 0;
            // 全界磁の力の倍率の説明（ほぼ 1 なら書かない）
            string scaleNote = scale switch
            {
                > 1.005 => Loc.F("Rw.071", scale),
                < 0.995 => Loc.F("Rw.072", scale),
                _ => string.Empty,
            };
            string speedsNote = fitted.StepSpeeds != null
                ? Loc.T("Rw.073")
                : Loc.T("Rw.074");
            TxtFitResult.Text = Loc.F("Rw.075", forceError / count * 100, currentError / count * 100, timeError * 100, speedsNote, scaleNote);
            OperationHistory.Add($"抵抗制御で作る: 今の表に合わせる s={shape.Saturation} r={shape.InternalResistance} L={shape.Loss} 力の倍率={scale:0.###}（ずれ {error:0.###}）");
            Recalculate();
        }

        // 表に入れる：チェックした表をまとめて作る。作る表の一覧を見せて確認してから、開いている表は GeneratedRows で渡し、ほかの表はファイルに直接書く
        private void BtnApply_Click(object sender, RoutedEventArgs e)
        {
            ResistanceInput? input = Recalculate();
            if (input == null) return;

            List<string[]> forceRows = BuildRows(input, TableKind.Force);
            List<string[]> currentRows = BuildRows(input, TableKind.Current);
            if (forceRows.Count == 0 || currentRows.Count == 0) return;
            Dictionary<TableKind, List<string[]>> built = CheckedKinds().ToDictionary(kind => kind, kind => IsForceKind(kind) ? forceRows : currentRows);

            List<string> plan = [.. built.Keys.Select(kind => kind == _kind
                ? Loc.F("Rw.076", TractionCalculator.KindName(kind))
                : Loc.F("Rw.077", TractionCalculator.KindName(kind), Path.GetFileName(_targets[kind])))];
            int steps = forceRows[0].Length - 1;
            MessageBoxResult result = MessageBox.Show(
                Loc.F("Rw.078", forceRows.Count, steps, string.Join("\n", plan)),
                Loc.T("Rw.079"), MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;

            // 開いている表のほかは、ファイルに直接書く（1 つ書けなくても、ほかは続ける）
            List<string> written = [];
            List<string> failed = [];
            foreach ((TableKind kind, List<string[]> rows) in built.Where(pair => pair.Key != _kind))
            {
                string path = _targets[kind];
                try
                {
                    string backup = PerformanceTableWriter.WriteRows(path, rows, PerformanceTableHeader);
                    written.Add(Loc.F("Rw.080", TractionCalculator.KindName(kind), Path.GetFileName(path), Path.GetFileName(backup)));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    OperationHistory.Add($"抵抗制御で作る: 書けない {path}（{ex.GetType().Name}: {ex.Message}）");
                    failed.Add(Loc.F("Rw.081", TractionCalculator.KindName(kind), Path.GetFileName(path), ErrorText.Describe(ex)));
                }
            }

            if (written.Count > 0 || failed.Count > 0)
            {
                string message = written.Count > 0 ? Loc.F("Rw.082", string.Join("\n", written)) : string.Empty;
                if (failed.Count > 0) message += Loc.F("Rw.083", (message.Length > 0 ? "\n\n" : string.Empty), string.Join("\n", failed));
                MessageBox.Show(message, failed.Count > 0 ? Loc.T("Rw.084") : Loc.T("Rw.085"), MessageBoxButton.OK, failed.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
            }

            OperationHistory.Add($"抵抗制御で作る: {string.Join("・", built.Keys)}（{steps} 段）");
            GeneratedRows = built.TryGetValue(_kind, out List<string[]>? opened) ? opened : null;
            DialogResult = true;
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
