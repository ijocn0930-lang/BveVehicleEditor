using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using OxyPlot;
using static BveVehicleEditor.TractionCalculator;

namespace BveVehicleEditor
{
    /// <summary>
    /// 力行の表（引張力・電流の空車と満車、無負荷電流。M 車 1 両あたり）を式から作るウィンドウ（電流・引張力テーブルの段階5）
    /// 「作る表」でチェックした表をまとめて作る（表を 1 つずつ開き直す手間をなくす。ユーザーの判断）。値を変えると少しあとにプレビューを描き直す
    /// 「表に入れる」で確認してから、開いている表の作った行は GeneratedRows で渡し、ほかの表はファイルに直接書く（PerformanceTableWriter）
    /// 入れた値はアプリを閉じるまで覚え、同じパラメーターの車両なら次に開いたときも同じ値から始める（ユーザーの判断）
    /// 計算は TractionCalculator、グラフは ChartBuilder
    /// </summary>
    public partial class TractionWindow : Window
    {
        /// <summary>
        /// ノッチの表の 1 行（欄は打ち間違いも受け付けるため文字で持ち、計算のときに数字に直す）
        /// </summary>
        public sealed class NotchRow : INotifyPropertyChanged
        {
            public event PropertyChangedEventHandler? PropertyChanged;

            private string _startForce = string.Empty;

            public int Notch { get; set; }
            public string Acceleration { get; set; } = string.Empty;
            public string V1 { get; set; } = string.Empty;
            public string V2 { get; set; } = string.Empty;
            public string Decay { get; set; } = string.Empty;
            public string StartCurrentText { get; set; } = string.Empty;

            /// <summary>ブレーキだけ：効き始める速度 vs・最大になる速度 va</summary>
            public string Vs { get; set; } = string.Empty;
            public string Va { get; set; } = string.Empty;

            /// <summary>起動時の力 [N]（計算した値。直せない）</summary>
            public string StartForce
            {
                get => _startForce;
                set
                {
                    if (_startForce == value) return;
                    _startForce = value;
                    PropertyChanged?.Invoke(this, new(nameof(StartForce)));
                }
            }

            public NotchRow Copy() => new() { Notch = Notch, Acceleration = Acceleration, V1 = V1, V2 = V2, Decay = Decay, StartCurrentText = StartCurrentText, Vs = Vs, Va = Va };
        }

        /// <summary>
        /// 覚えておく値（アプリを閉じるまで）。ParamText が同じパラメーターのときだけ使う
        /// </summary>
        /// <param name="CheckedKinds">「作る表」でチェックしていた表</param>
        private sealed record Memory(string ParamText, Dictionary<string, string> Fields, List<NotchRow> Notches, HashSet<TableKind> CheckedKinds);

        // 作る表の種類の並び（画面の「作る表」のチェックと、グラフで見る表の並び）
        private static readonly TableKind[] AllKinds = [TableKind.Force, TableKind.MaxForce, TableKind.Current, TableKind.MaxCurrent, TableKind.NoLoadCurrent];

        // 表の種類ごとのファイルのフルパス（ノッチのファイルに書かれていない・見つからない表は入れない。開いている表は入れなくてよい）
        private readonly IReadOnlyDictionary<TableKind, string> _targets;

        // 「作る表」のチェック
        private readonly Dictionary<TableKind, CheckBox> _checks = [];

        // グラフで見る表（最初は開いている表）
        private TableKind _previewKind;

        // 前に開いたときに入れた値（力行とブレーキは、ノッチの表がまったく違うので別々に覚える）
        private static Memory? _powerMemory;
        private static Memory? _brakeMemory;

        // 今のウィンドウの覚えた値（力行かブレーキか）
        private Memory? CurrentMemory
        {
            get => _isBrake ? _brakeMemory : _powerMemory;
            set
            {
                if (_isBrake) _brakeMemory = value;
                else _powerMemory = value;
            }
        }

        // ブレーキ（[Brake]）の表か（段階6。欄の名前・ノッチの表・計算の形が変わる）
        private readonly bool _isBrake;

        // ブレーキのとき最初に入れておく値（サンプルの 79 個の表の目安：減速度 2.1～3.4 km/h/s、列は 1 つがいちばん多い）
        private const double DefaultBrakeDeceleration = 2.5;
        private const double DefaultBrakeVs = 0;
        private const double DefaultBrakeVa = 10;
        private const double DefaultBrakeVb = 80;
        private const int DefaultBrakeNotchCount = 1;

        // 最初に入れておく値（BVE の車両でよくある値の目安）
        private const double DefaultDecay = 2;
        private const double DefaultSpeedStep = 1;
        private const double DefaultMaxSpeed = 120;
        private const double DefaultTopAcceleration = 3.0;
        private const double DefaultTopV1 = 40;
        private const double DefaultTopV2 = 80;
        private const double DefaultTopCurrent = 300;
        private const double DefaultLoadPercent = 100;
        private const double DefaultNoLoadCurrent = 0;

        // 作る表の種類
        private readonly TableKind _kind;

        // 覚えておくときの、パラメーターのテキスト
        private readonly string _paramText;

        // ノッチの表
        private readonly ObservableCollection<NotchRow> _notches = [];

        // 打つたびに描き直すと重いので、打ち終わって少し待ってから 1 回だけ描き直す
        private readonly DispatcherTimer _recalculateTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };

        // パラメーターに書かれていた走行抵抗の係数（書かれていなければ null で、既定の式を使う）
        private readonly ResistanceFactors? _paramResistance;

        // 作り終わるまで（コンストラクターで欄に値を入れている間）は描き直さない
        private bool _isLoading = true;

        /// <summary>
        /// 「表に入れる」で作った、開いている表の行（1 列目が速度、2 列目からノッチごとの値）
        /// 閉じただけ、または開いている表をチェックしていなければ null
        /// </summary>
        public List<string[]>? GeneratedRows { get; private set; }

        /// <summary>
        /// ウィンドウを作る
        /// </summary>
        /// <param name="paramText">パラメーターのテキスト（[Dynamics]・[Passengers] と力行ノッチ数を読む）</param>
        /// <param name="currentMaxSpeed">今の表の最後の速度（覚えている値がなければ、最高速度の最初の値にする）</param>
        /// <param name="kind">開いている表の種類</param>
        /// <param name="targets">表の種類ごとのファイルのフルパス（まとめて作るときに、ほかの表を書く場所）</param>
        /// <param name="isBrake">ブレーキ（[Brake]）の表か</param>
        internal TractionWindow(string paramText, double? currentMaxSpeed, TableKind kind, IReadOnlyDictionary<TableKind, string> targets, bool isBrake = false)
        {
            InitializeComponent();
            _isBrake = isBrake;
            _kind = kind;
            _previewKind = kind;
            _paramText = paramText;
            _targets = targets;
            _recalculateTimer.Tick += (s, e) =>
            {
                _recalculateTimer.Stop();
                Recalculate();
            };

            ParamValues values = ParameterForm.ReadTrainForTraction(paramText);
            _paramResistance = values.Resistance;
            string section = isBrake ? Loc.T("Tr.001") : Loc.T("Tr.002");
            Title = Loc.F("Tr.003", section, KindName(kind));
            TxtKind.Text = Loc.F("Tr.004", section, KindName(kind));
            GridNotches.ItemsSource = _notches;
            PlotPreview.Controller = ChartBuilder.CreateController();
            if (isBrake) ApplyBrakeMode();
            BuildTargets();

            // 同じパラメーターの車両で前に入れた値があれば、それから始める（力行とブレーキは別々）
            if (CurrentMemory != null && CurrentMemory.ParamText == paramText) Restore(CurrentMemory);
            else FillDefaults(values, currentMaxSpeed);
            TxtExplain.Text = Explain(_previewKind);

            _isLoading = false;
            Recalculate();
        }

        // ブレーキのとき：欄の名前・説明・ノッチの表の列を変える（v1 は使わず、vs・va を出す。v2 は vb として使う）
        private void ApplyBrakeMode()
        {
            TxtNotchNote.Text = Loc.T("Tr.005");
            LblTopAcceleration.Text = Loc.T("Tr.006");
            LblTopV2.Text = Loc.T("Tr.007");
            LblTopCurrent.Text = Loc.T("Tr.008");
            PnlTopV1.Visibility = Visibility.Collapsed;
            PnlTopVs.Visibility = Visibility.Visible;
            PnlTopVa.Visibility = Visibility.Visible;
            ColAcceleration.Header = Loc.T("Tr.009");
            ColV1.Visibility = Visibility.Collapsed;
            ColVs.Visibility = Visibility.Visible;
            ColVa.Visibility = Visibility.Visible;
            ColV2.Header = "vb [km/h]";
            ColCurrent.Header = Loc.T("Tr.010");
            ColForce.Header = Loc.T("Tr.011");
        }

        // 「作る表」のチェックと「グラフで見る表」の選ぶ欄を作る
        // 開いている表と、ファイルが見つかる表だけ選べる。最初にチェックするのは開いている表だけ
        private void BuildTargets()
        {
            foreach (TableKind kind in AllKinds)
            {
                bool isOpened = kind == _kind;
                bool hasFile = _targets.TryGetValue(kind, out string? path);
                CheckBox check = new()
                {
                    Content = isOpened ? Loc.F("Tr.012", KindName(kind)) : hasFile ? $"{KindName(kind)}（{System.IO.Path.GetFileName(path)}）" : Loc.F("Tr.013", KindName(kind)),
                    IsEnabled = isOpened || hasFile,
                    IsChecked = isOpened,
                    Margin = new Thickness(0, 0, 14, 4),
                    ToolTip = isOpened ? Loc.T("Tr.014") : hasFile ? Loc.F("Tr.015", path) : Loc.T("Tr.016"),
                };
                check.Checked += (s, e) => { if (!_isLoading) Recalculate(); };
                check.Unchecked += (s, e) => { if (!_isLoading) Recalculate(); };
                _checks[kind] = check;
                PanelTargets.Children.Add(check);
                CmbPreview.Items.Add(new ComboBoxItem { Content = KindName(kind), Tag = kind });
            }
            CmbPreview.SelectedIndex = Array.IndexOf(AllKinds, _kind);
        }

        // チェックしている表（並びは AllKinds の順）
        private List<TableKind> CheckedKinds()
        {
            return [.. AllKinds.Where(kind => _checks[kind].IsChecked == true)];
        }

        // グラフで見る表を選んだら、説明とグラフを変える
        private void CmbPreview_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbPreview.SelectedItem is not ComboBoxItem { Tag: TableKind kind }) return;
            _previewKind = kind;
            TxtExplain.Text = Explain(kind);
            if (_isLoading) return;
            Recalculate();
        }

        // 欄の一覧（覚えるとき・戻すときに使う。名前は XAML の x:Name）
        private IEnumerable<TextBox> MemorizedBoxes =>
        [
            TxtMotorcarWeight, TxtMotorcarCount, TxtMotorcarInertia, TxtTrailerWeight, TxtTrailerCount, TxtTrailerInertia,
            TxtCapacity, TxtBodyWeight, TxtLoadPercent, TxtBoostUpTo, TxtCurrentRatio, TxtNoLoadCurrent, TxtNoLoadDrop,
            TxtMaxSpeed, TxtSpeedStep, TxtTopAcceleration, TxtTopV1, TxtTopVs, TxtTopVa, TxtTopV2, TxtDecay, TxtTopCurrent, TxtNotchCount,
        ];

        // パラメーターから読んだ値と、最初に入れておく値で欄を埋める
        private void FillDefaults(ParamValues values, double? currentMaxSpeed)
        {
            TrainSpec train = values.Train;
            TxtMotorcarWeight.Text = Format(train.MotorcarWeight);
            TxtMotorcarCount.Text = Format(train.MotorcarCount);
            TxtMotorcarInertia.Text = Format(train.MotorcarInertiaFactor);
            TxtTrailerWeight.Text = Format(train.TrailerWeight);
            TxtTrailerCount.Text = Format(train.TrailerCount);
            TxtTrailerInertia.Text = Format(train.TrailerInertiaFactor);
            TxtCapacity.Text = Format(values.Capacity);
            TxtBodyWeight.Text = Format(values.BodyWeight);
            TxtLoadPercent.Text = Format(DefaultLoadPercent);
            TxtBoostUpTo.Text = string.Empty;
            // 満車の電流の倍率は、最初は満車の力の倍率と同じ値（ユーザーと決めた）
            TxtCurrentRatio.Text = FormatRatio(FullLoadRatio(train, new(values.Capacity, values.BodyWeight, DefaultLoadPercent, null)));
            TxtNoLoadCurrent.Text = Format(DefaultNoLoadCurrent);
            TxtNoLoadDrop.Text = string.Empty;
            TxtMaxSpeed.Text = Format(currentMaxSpeed is > 0 ? currentMaxSpeed.Value : DefaultMaxSpeed);
            TxtSpeedStep.Text = Format(DefaultSpeedStep);
            // ノッチの最初の値（力行は起動加速度・v1・v2、ブレーキは減速度・vs・va・vb。ブレーキの列は最初 1 つ。ユーザーの判断）
            double topAcceleration = _isBrake ? DefaultBrakeDeceleration : DefaultTopAcceleration;
            double topV2 = _isBrake ? DefaultBrakeVb : DefaultTopV2;
            int notchCount = _isBrake ? DefaultBrakeNotchCount : values.PowerNotchCount;
            TxtTopAcceleration.Text = Format(topAcceleration);
            TxtTopV1.Text = Format(DefaultTopV1);
            TxtTopVs.Text = Format(DefaultBrakeVs);
            TxtTopVa.Text = Format(DefaultBrakeVa);
            TxtTopV2.Text = Format(topV2);
            TxtDecay.Text = Format(DefaultDecay);
            TxtTopCurrent.Text = Format(DefaultTopCurrent);
            TxtNotchCount.Text = notchCount.ToString(CultureInfo.InvariantCulture);
            FillNotches(notchCount, new(topAcceleration, DefaultTopV1, topV2, DefaultDecay, DefaultTopCurrent, DefaultBrakeVs, DefaultBrakeVa));
        }

        // 覚えていた値で欄とノッチの表を埋める
        private void Restore(Memory memory)
        {
            foreach (TextBox box in MemorizedBoxes)
            {
                if (memory.Fields.TryGetValue(box.Name, out string? text)) box.Text = text;
            }
            _notches.Clear();
            foreach (NotchRow row in memory.Notches) _notches.Add(row.Copy());

            // チェックは、選べる表（開いている表と、ファイルが見つかる表）だけ戻す。開いている表はいつもチェックしておく
            foreach (TableKind kind in AllKinds)
            {
                CheckBox check = _checks[kind];
                check.IsChecked = kind == _kind || (check.IsEnabled && memory.CheckedKinds.Contains(kind));
            }
        }

        // 閉じるとき（表に入れたときも、閉じただけのときも）に、今の値を覚える
        private void Window_Closing(object? sender, CancelEventArgs e)
        {
            GridNotches.CommitEdit(DataGridEditingUnit.Row, true);
            Dictionary<string, string> fields = MemorizedBoxes.ToDictionary(box => box.Name, box => box.Text);
            CurrentMemory = new(_paramText, fields, [.. _notches.Select(row => row.Copy())], [.. CheckedKinds()]);
        }

        // 作る表の種類の作り方の説明
        private string Explain(TableKind kind)
        {
            if (_isBrake) return ExplainBrake(kind);
            string force = Loc.T("Tr.017");
            string current = Loc.T("Tr.018");
            string preview = Loc.T("Tr.019");
            return kind switch
            {
                TableKind.Force => Loc.F("Tr.020", force, preview),
                TableKind.MaxForce => Loc.F("Tr.021", force, preview),
                TableKind.Current => $"{current}{preview}",
                TableKind.MaxCurrent => Loc.F("Tr.022", current, preview),
                _ => Loc.F("Tr.023", preview),
            };
        }

        // ブレーキの表の種類の作り方の説明
        private static string ExplainBrake(TableKind kind)
        {
            string force = Loc.T("Tr.024");
            string current = Loc.T("Tr.025");
            string preview = Loc.T("Tr.026");
            return kind switch
            {
                TableKind.Force => $"{force}{preview}",
                TableKind.MaxForce => Loc.F("Tr.027", force, preview),
                TableKind.Current => $"{current}{preview}",
                TableKind.MaxCurrent => Loc.F("Tr.028", current, preview),
                _ => Loc.F("Tr.029", preview),
            };
        }

        /// <summary>
        /// 今の欄の値で計算する。問題があれば理由を出し、プレビューと「表に入れる」を止める
        /// チェックした表と、グラフで見る表の値を調べる。グラフで見る表の作った行を返す（問題があれば空）
        /// </summary>
        public List<string[]> Recalculate()
        {
            List<string> problems = [];
            TractionInput? input = ReadInput(problems);
            List<TableKind> checkedKinds = CheckedKinds();
            if (checkedKinds.Count == 0) problems.Add(Loc.T("Tr.030"));
            if (input != null)
            {
                foreach (TableKind kind in checkedKinds.Append(_previewKind).Distinct())
                {
                    foreach (string problem in Validate(input, kind).Where(problem => !problems.Contains(problem))) problems.Add(problem);
                }
            }

            // 満車の力の倍率（乗車率などが読めれば出す）
            TxtForceRatio.Text = input != null ? FormatRatio(FullLoadRatio(input.Train, input.Load)) : string.Empty;

            // 起動時の力の列（読めたノッチだけ出す）
            foreach (NotchRow row in _notches)
            {
                row.StartForce = input != null && TryParse(row.Acceleration, out double a)
                    ? Format(StartForce(input.Train, a))
                    : string.Empty;
            }

            TxtProblems.Text = string.Join("\n", problems.Select(problem => "⚠ " + problem));
            TxtProblems.Visibility = problems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            BtnApply.IsEnabled = problems.Count == 0;
            if (input == null || problems.Count > 0)
            {
                ChartBuilder.Show(PlotPreview, ChartBuilder.CreateModel(ValueName(_previewKind), this));
                return [];
            }

            List<string[]> rows = BuildRows(input, _previewKind);
            DrawPreview(rows, input);
            return rows;
        }

        /// <summary>
        /// チェックした表それぞれの作った行（問題があれば null）
        /// </summary>
        internal Dictionary<TableKind, List<string[]>>? BuildCheckedRows()
        {
            List<string> problems = [];
            TractionInput? input = ReadInput(problems);
            if (input == null) return null;

            Dictionary<TableKind, List<string[]>> result = [];
            foreach (TableKind kind in CheckedKinds())
            {
                List<string[]> rows = BuildRows(input, kind);
                if (rows.Count == 0) return null;
                result[kind] = rows;
            }
            return result.Count > 0 ? result : null;
        }

        // 欄の値を読む。数字でない欄があれば理由を足して null
        private TractionInput? ReadInput(List<string> problems)
        {
            double Read(TextBox box, string name)
            {
                if (TryParse(box.Text, out double value)) return value;
                problems.Add(Loc.F("Tr.031", name, box.Text));
                return 0;
            }

            TrainSpec train = new(
                Read(TxtMotorcarWeight, Loc.T("Tr.032")), Read(TxtTrailerWeight, Loc.T("Tr.033")),
                Read(TxtMotorcarCount, Loc.T("Tr.034")), Read(TxtTrailerCount, Loc.T("Tr.035")),
                Read(TxtMotorcarInertia, Loc.T("Tr.036")), Read(TxtTrailerInertia, Loc.T("Tr.037")));
            double? boostUpTo = string.IsNullOrWhiteSpace(TxtBoostUpTo.Text) ? null : Read(TxtBoostUpTo, Loc.T("Tr.038"));
            LoadSpec load = new(Read(TxtCapacity, Loc.T("Tr.039")), Read(TxtBodyWeight, Loc.T("Tr.040")), Read(TxtLoadPercent, Loc.T("Tr.041")), boostUpTo);
            double currentRatio = Read(TxtCurrentRatio, Loc.T("Tr.042"));
            double noLoad = Read(TxtNoLoadCurrent, Loc.T("Tr.043"));
            double? noLoadDrop = string.IsNullOrWhiteSpace(TxtNoLoadDrop.Text) ? null : Read(TxtNoLoadDrop, Loc.T("Tr.044"));
            double maxSpeed = Read(TxtMaxSpeed, Loc.T("Tr.045"));
            double step = Read(TxtSpeedStep, Loc.T("Tr.046"));

            List<NotchSetting> notches = [];
            foreach (NotchRow row in _notches)
            {
                // 力行は v1、ブレーキは vs・va を読む（使わないほうは 0）
                double v1 = 0, vs = 0, va = 0;
                bool ok = TryParse(row.Acceleration, out double a) & TryParse(row.V2, out double v2)
                    & TryParse(row.Decay, out double k) & TryParse(row.StartCurrentText, out double i0)
                    & (_isBrake ? TryParse(row.Vs, out vs) & TryParse(row.Va, out va) : TryParse(row.V1, out v1));
                if (!ok)
                {
                    problems.Add(_isBrake
                        ? Loc.F("Tr.047", row.Notch)
                        : Loc.F("Tr.048", row.Notch));
                }
                notches.Add(new(a, v1, v2, k, i0, vs, va));
            }
            return problems.Count > 0 ? null : new(train, maxSpeed, step, notches, load, currentRatio, noLoad, noLoadDrop, _isBrake);
        }

        // プレビューを描く（表のウィンドウのグラフと同じ作り方）
        private void DrawPreview(List<string[]> rows, TractionInput input)
        {
            PlotModel model = ChartBuilder.CreateModel(ValueName(_previewKind), this);
            for (int notch = 1; notch <= _notches.Count; notch++)
            {
                IEnumerable<DataPoint> points = rows.Select(cells => new DataPoint(
                    double.Parse(cells[0], CultureInfo.InvariantCulture), double.Parse(cells[notch], CultureInfo.InvariantCulture)));
                ChartBuilder.AddLine(model, Loc.F("Tr.049", notch), notch - 1, points);
            }

            // 引張力の表では、走行抵抗（M 車 1 両あたり）を灰色の点線で重ねる（ユーザーの要望）
            // パラメーターに係数が書かれていればその値、なければ今の「編成」の欄の値から既定の式で計算する
            if (!_isBrake && _previewKind is TableKind.Force or TableKind.MaxForce)
            {
                ResistanceFactors factors = _paramResistance ?? DefaultResistance(input.Train);
                string title = _paramResistance != null ? Loc.T("Tr.050") : Loc.T("Tr.051");
                IEnumerable<DataPoint> resistance = rows.Select(cells =>
                {
                    double speed = double.Parse(cells[0], CultureInfo.InvariantCulture);
                    return new DataPoint(speed, RunningResistancePerMotorcar(factors, input.Train, speed));
                });
                ChartBuilder.AddReferenceLine(model, title, resistance);
            }

            // 縦の軸は 0 から（低い速度の点線や値が、軸の下の端に張りつかないように。力も電流もマイナスにならない）
            model.Axes[1].Minimum = 0;
            ChartBuilder.Show(PlotPreview, model);
            ChartBuilder.ApplyTrackerStyle(PlotPreview);
        }

        // ノッチの表を作り直す（最高ノッチの値から、起動加速度と起動時の電流をノッチの割合にする）
        private void FillNotches(int notchCount, NotchSetting top)
        {
            _notches.Clear();
            List<NotchSetting> settings = DefaultNotches(notchCount, top);
            for (int i = 0; i < settings.Count; i++)
            {
                _notches.Add(new NotchRow
                {
                    Notch = i + 1,
                    Acceleration = Format(settings[i].StartAcceleration),
                    V1 = Format(settings[i].V1),
                    V2 = Format(settings[i].V2),
                    Decay = Format(settings[i].DecayExponent),
                    StartCurrentText = Format(settings[i].StartCurrent),
                    Vs = Format(settings[i].Vs),
                    Va = Format(settings[i].Va),
                });
            }
        }

        // 倍率を画面に出す形にする（小数 4 けたまで。表の値の Format は 3 けたなので、倍率には足りない）
        private static string FormatRatio(double value)
        {
            return value.ToString("0.####", CultureInfo.InvariantCulture);
        }

        // 数字として読む（前後の空白は無視）
        private static bool TryParse(string? text, out double value)
        {
            return double.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        // 欄を打ったら、少しあとに描き直す
        private void Input_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isLoading) return;
            _recalculateTimer.Stop();
            _recalculateTimer.Start();
        }

        // ノッチの表のセルを直し終えたら、表に書き戻されたあとで描き直す
        private void GridNotches_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
        {
            if (e.EditAction != DataGridEditAction.Commit) return;
            Dispatcher.BeginInvoke(Recalculate, DispatcherPriority.Background);
        }

        // 表を作り直す：最高ノッチの値とノッチ数を読み、今のノッチの表を置き換えるか確かめてから作り直す
        private void BtnFillNotches_Click(object sender, RoutedEventArgs e)
        {
            GridNotches.CommitEdit(DataGridEditingUnit.Row, true);
            // 力行は v1、ブレーキは vs・va を読む（使わないほうは 0）
            double v1 = 0, vs = 0, va = 0;
            bool ok = TryParse(TxtTopAcceleration.Text, out double a) & TryParse(TxtTopV2.Text, out double v2)
                & TryParse(TxtDecay.Text, out double k) & TryParse(TxtTopCurrent.Text, out double i0)
                & (_isBrake ? TryParse(TxtTopVs.Text, out vs) & TryParse(TxtTopVa.Text, out va) : TryParse(TxtTopV1.Text, out v1));
            string names = _isBrake ? Loc.T("Tr.052") : Loc.T("Tr.053");
            if (!ok || !int.TryParse(TxtNotchCount.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) || count < 1 || count > 255)
            {
                MessageBox.Show(Loc.F("Tr.054", names), Loc.T("Tr.055"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string values = _isBrake
                ? Loc.F("Tr.056", a, vs, va, v2, k, i0)
                : Loc.F("Tr.057", a, v1, v2, k, i0);
            MessageBoxResult result = MessageBox.Show(
                Loc.F("Tr.058", count, values),
                Loc.T("Tr.059"), MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;

            FillNotches(count, new(a, v1, v2, k, i0, vs, va));
            Recalculate();
        }

        // 表に入れる：チェックした表をまとめて作る（ユーザーの判断）。問題がなければ、作る表の一覧を見せて確認する
        // 開いている表は GeneratedRows で表のウィンドウに渡し（保存するまでファイルは変わらない）、ほかの表はファイルに直接書く（前の中身は .bak）
        private void BtnApply_Click(object sender, RoutedEventArgs e)
        {
            GridNotches.CommitEdit(DataGridEditingUnit.Row, true);
            if (Recalculate().Count == 0) return;
            Dictionary<TableKind, List<string[]>>? built = BuildCheckedRows();
            if (built == null) return;

            List<string> plan = [.. built.Select(pair => pair.Key == _kind
                ? Loc.F("Tr.060", KindName(pair.Key))
                : Loc.F("Tr.061", KindName(pair.Key), System.IO.Path.GetFileName(_targets[pair.Key])))];
            MessageBoxResult result = MessageBox.Show(
                Loc.F("Tr.062", built.First().Value.Count, _notches.Count, string.Join("\n", plan)),
                Loc.T("Tr.063"), MessageBoxButton.YesNo, MessageBoxImage.Question);
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
                    written.Add(Loc.F("Tr.064", KindName(kind), System.IO.Path.GetFileName(path), System.IO.Path.GetFileName(backup)));
                }
                catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.IO.InvalidDataException)
                {
                    OperationHistory.Add($"表を式から作る: 書けない {path}（{ex.GetType().Name}: {ex.Message}）");
                    failed.Add(Loc.F("Tr.065", KindName(kind), System.IO.Path.GetFileName(path), ErrorText.Describe(ex)));
                }
            }

            if (written.Count > 0 || failed.Count > 0)
            {
                string message = written.Count > 0 ? Loc.F("Tr.066", string.Join("\n", written)) : string.Empty;
                if (failed.Count > 0) message += Loc.F("Tr.067", (message.Length > 0 ? "\n\n" : string.Empty), string.Join("\n", failed));
                MessageBox.Show(message, failed.Count > 0 ? Loc.T("Tr.068") : Loc.T("Tr.069"), MessageBoxButton.OK, failed.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
            }

            GeneratedRows = built.TryGetValue(_kind, out List<string[]>? opened) ? opened : null;
            DialogResult = true;
        }

        // 表のファイルの 1 行目のヘッダー（NotchForm の PerformanceTableHeader と同じ）
        private const string PerformanceTableHeader = "BveTs Vehicle Performance Table";

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
