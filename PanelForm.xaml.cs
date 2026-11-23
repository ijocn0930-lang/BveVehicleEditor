using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using static BveVehicleEditor.FormFieldHelper;

namespace BveVehicleEditor
{
    /// <summary>
    /// パネル構成ファイルの入力フォーム。セクションを選んで、その中身を出す
    /// [This]・計器の共通の項目・計器ごとの項目を欄で書き込める。Subject は一覧から選べる
    /// チェック：値の形・バージョンで使えない項目（Perspective・Tilt・StopPin は 2.01 から）・2.00 以降の色の #・画像があるか・画像の大きさが 2 のべき乗か
    /// 下のテキストエディタが元になり、テキストが変わると少しあとに表示し直す
    /// </summary>
    public partial class PanelForm : UserControl
    {
        // [This] のセクション名（1つだけ書ける）
        private const string ThisSection = "This";

        // 公式の一覧（https://bvets.net/jp/edit/formats/vehicle/panel.html。2026-11-23 に読んだ）：セクション → 書けるキー（"" はセクションの前。何も書けない）
        // 一覧にない名前は書き間違いかもしれないので警告する（BveTextParser.FindUnknownNames）。[This] だけのキーを計器に書いていても警告になる
        private static readonly Dictionary<string, string[]> KnownKeys = new()
        {
            [""] = [],
            ["This"] = ["Resolution", "Left", "Top", "Right", "Bottom", "DaytimeImage", "NighttimeImage", "TransparentColor", "Center", "Origin", "Perspective"],
            ["PilotLamp"] = ["Subject", "Location", "DaytimeImage", "NighttimeImage", "TransparentColor", "Layer"],
            ["Needle"] = ["Subject", "Location", "DaytimeImage", "NighttimeImage", "Color", "TransparentColor", "Origin", "Layer", "Radius", "InitialAngle", "LastAngle", "Minimum", "Maximum", "Tilt", "StopPin", "NaturalFreq", "DampingRatio"],
            ["DigitalGauge"] = ["Subject", "Location", "Color", "Layer", "Radius", "InitialAngle", "LastAngle", "Minimum", "Maximum", "Tilt", "Step"],
            ["DigitalNumber"] = ["Subject", "Location", "DaytimeImage", "NighttimeImage", "TransparentColor", "Layer", "Interval"],
        };

        /// <summary>
        /// 欄に書く値の形
        /// </summary>
        private enum ValueKind
        {
            // 数字（マイナスも可）
            Number,
            // 「x, y」の2つの数字
            Point,
            // 画像のパス（形は調べない）
            Path,
            // 6 桁の 16 進数の色（例 FF0000）
            Color,
            // 0 以上の整数（描画順序 Layer）
            Layer,
            // 文字（形は調べない。Subject など）
            Text,
        }

        // 計器のセクション名（どれも何個でも書ける）
        private static readonly string[] InstrumentSections = ["PilotLamp", "Needle", "DigitalNumber", "DigitalGauge"];

        // 画像と透過色がない計器（円形デジタルゲージは画像を使わない）
        private const string DigitalGaugeSection = "DigitalGauge";

        /// <summary>
        /// 数字・文字の欄1つ分
        /// </summary>
        /// <param name="Box">欄</param>
        /// <param name="Key">ファイルに書くキー</param>
        /// <param name="Label">画面の名前（警告の文に使う）</param>
        /// <param name="Kind">値の形</param>
        /// <param name="DefaultValue">書かれていないときに BVE が使う値。ページに書かれていなければ null</param>
        private sealed record PanelField(TextBox Box, string Key, string LabelKey, ValueKind Kind, string? DefaultValue)
        {
            // 画面の名前（言語を切り替えてもすぐ変わるよう、使うたびに引く）
            public string Label => Loc.T(LabelKey);
        }

        // [This] の欄（出典：https://bvets.net/jp/edit/formats/vehicle/panel.html。既定値が書かれているのは Perspective の 1 だけ）
        private readonly PanelField[] _thisFields;

        // 計器に共通の欄（出典は同じページ。どれも既定値は書かれていない）
        private readonly PanelField[] _instrumentFields;

        // 計器に共通の欄のうち、画像と透過色の欄（[DigitalGauge] では隠す）
        private readonly PanelField[] _imageFields;

        // 計器ごとの欄（StopPin はチェックボックスなので入らない）。出典は同じページ。既定値が書かれているのは Tilt の 0 と StopPin の False だけ
        private readonly PanelField[] _specificFields;

        // 計器ごとの欄の項目名（キー → 項目名。行を出し分けるのに使う。StopPin も入る）
        private readonly Dictionary<string, TextBlock> _specificLabels;

        // 計器ごとに使う項目（ページの表のとおり。[PilotLamp] は共通の項目だけ）
        private static readonly Dictionary<string, string[]> SpecificKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Needle"] = ["Color", "Origin", "Radius", "InitialAngle", "LastAngle", "Minimum", "Maximum", "Tilt", "StopPin", "NaturalFreq", "DampingRatio"],
            ["DigitalGauge"] = ["Color", "Radius", "InitialAngle", "LastAngle", "Minimum", "Maximum", "Tilt", "Step"],
            ["DigitalNumber"] = ["Interval"],
        };

        // StopPin の既定値（ページの説明文より）
        private const string StopPinDefault = "False";

        // 欄に入ったときの値（Esc で戻す・変わっていなければ書き込まないため）。書かれていなかったら null
        private readonly Dictionary<TextBox, string?> _valuesOnFocus = [];

        // 今のテキストが古い形式（Version 1.0）か。古い形式では # をコメントにしない
        private bool _isLegacy;
        /// <summary>
        /// 選んだセクションに書かれている「キー = 値」1つ分（画面の一覧に出す）
        /// </summary>
        public sealed record EntryRow(string Key, string Value, int LineNumber);

        /// <summary>
        /// パネルのセクション1つ分
        /// </summary>
        /// <param name="Name">セクション名（書かれているとおり）</param>
        /// <param name="Occurrence">同じ名前のセクションの何個目か（1 から。大文字小文字は区別しない）</param>
        /// <param name="LineNumber">[ ] の行の行番号</param>
        /// <param name="Comment">[ ] の行の後ろのコメント（[PilotLamp];知らせ灯 の「知らせ灯」）。なければ空</param>
        /// <param name="Entries">中に書かれている「キー = 値」</param>
        private sealed record PanelSection(string Name, int Occurrence, int LineNumber, string Comment, List<EntryRow> Entries);

        // 元になるテキストエディタ
        private TextEditor? _editor;

        // テキストを打つたびに読み直すと重いので、打ち終わって少し待ってから1回だけ表示し直す
        private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };

        // 表示し直している最中か（このときの選択の変化は、ユーザーの操作ではない）
        private bool _isRefreshing;

        // 今テキストに書かれているセクション（上から順）
        private List<PanelSection> _sections = [];

        public PanelForm()
        {
            InitializeComponent();
            _refreshTimer.Tick += (s, e) =>
            {
                _refreshTimer.Stop();
                Refresh();
            };

            _thisFields =
            [
                new(TxtThisResolution, "Resolution", "Pf.001", ValueKind.Number, null),
                new(TxtThisLeft, "Left", "Pf.002", ValueKind.Number, null),
                new(TxtThisTop, "Top", "Pf.003", ValueKind.Number, null),
                new(TxtThisRight, "Right", "Pf.004", ValueKind.Number, null),
                new(TxtThisBottom, "Bottom", "Pf.005", ValueKind.Number, null),
                new(TxtThisDaytimeImage, "DaytimeImage", "Pf.006", ValueKind.Path, null),
                new(TxtThisNighttimeImage, "NighttimeImage", "Pf.007", ValueKind.Path, null),
                new(TxtThisTransparentColor, "TransparentColor", "Pf.008", ValueKind.Color, null),
                new(TxtThisCenter, "Center", "Pf.009", ValueKind.Point, null),
                new(TxtThisOrigin, "Origin", "Pf.010", ValueKind.Point, null),
                new(TxtThisPerspective, "Perspective", "Pf.011", ValueKind.Number, "1"),
            ];

            _instrumentFields =
            [
                new(TxtInstSubject, "Subject", "Pf.012", ValueKind.Text, null),
                new(TxtInstLocation, "Location", "Pf.013", ValueKind.Point, null),
                new(TxtInstDaytimeImage, "DaytimeImage", "Pf.014", ValueKind.Path, null),
                new(TxtInstNighttimeImage, "NighttimeImage", "Pf.015", ValueKind.Path, null),
                new(TxtInstTransparentColor, "TransparentColor", "Pf.016", ValueKind.Color, null),
                new(TxtInstLayer, "Layer", "Pf.017", ValueKind.Layer, null),
            ];
            _imageFields = [.. _instrumentFields.Where(field => field.Kind is ValueKind.Path or ValueKind.Color)];

            // 項目名は計器ごとに ShowSpecificRows で変える。ここの名前は警告の文に使う
            _specificFields =
            [
                new(TxtSpColor, "Color", "Pf.018", ValueKind.Color, null),
                new(TxtSpOrigin, "Origin", "Pf.019", ValueKind.Point, null),
                new(TxtSpRadius, "Radius", "Pf.020", ValueKind.Number, null),
                new(TxtSpInitialAngle, "InitialAngle", "Pf.021", ValueKind.Number, null),
                new(TxtSpLastAngle, "LastAngle", "Pf.022", ValueKind.Number, null),
                new(TxtSpMinimum, "Minimum", "Pf.023", ValueKind.Number, null),
                new(TxtSpMaximum, "Maximum", "Pf.024", ValueKind.Number, null),
                new(TxtSpTilt, "Tilt", "Pf.025", ValueKind.Point, "0, 0"),
                new(TxtSpNaturalFreq, "NaturalFreq", "Pf.026", ValueKind.Number, null),
                new(TxtSpDampingRatio, "DampingRatio", "Pf.027", ValueKind.Number, null),
                new(TxtSpStep, "Step", "Pf.028", ValueKind.Number, null),
                new(TxtSpInterval, "Interval", "Pf.029", ValueKind.Number, null),
            ];
            _specificLabels = new(StringComparer.OrdinalIgnoreCase)
            {
                ["Color"] = LblSpColor,
                ["Origin"] = LblSpOrigin,
                ["Radius"] = LblSpRadius,
                ["InitialAngle"] = LblSpInitialAngle,
                ["LastAngle"] = LblSpLastAngle,
                ["Minimum"] = LblSpMinimum,
                ["Maximum"] = LblSpMaximum,
                ["Tilt"] = LblSpTilt,
                ["StopPin"] = LblSpStopPin,
                ["NaturalFreq"] = LblSpNaturalFreq,
                ["DampingRatio"] = LblSpDampingRatio,
                ["Step"] = LblSpStep,
                ["Interval"] = LblSpInterval,
            };

            // 欄に入ったら「（未記入）」を消す、出たら・Enter で書き込む、Esc で取り消す（パラメーターの入力フォームと同じ動き）
            foreach (PanelField field in _thisFields.Concat(_instrumentFields).Concat(_specificFields))
            {
                field.Box.GotKeyboardFocus += (s, e) => BeginEdit(field.Box);
                field.Box.LostKeyboardFocus += (s, e) => CommitField(field, isLeaving: true);
                field.Box.KeyDown += (s, e) =>
                {
                    if (e.Key == Key.Enter) CommitField(field, isLeaving: false);
                    if (e.Key == Key.Escape) CancelField(field);
                };
            }
        }

        /// <summary>
        /// 元になるテキストエディタをつなぐ。テキストが変わったら表示し直す
        /// </summary>
        /// <param name="editor">パネルのテキストエディタ</param>
        /// <param name="getParamText">パラメーターのテキストを読む（Subject の sap・bp・er をブレーキ方式で調べるため）</param>
        /// <param name="getPanelPath">パネルファイルのフルパスを読む（画像があるか・大きさを調べるため）。読み込めていなければ null</param>
        /// <param name="getPanelEncoding">パネルファイルの今の文字コードを読む（古い形式を書き直すとき、Shift_JIS ならヘッダーに :shift_jis を付けるため）</param>
        public void Attach(TextEditor editor, Func<string> getParamText, Func<string?> getPanelPath, Func<System.Text.Encoding> getPanelEncoding)
        {
            _editor = editor;
            _getParamText = getParamText;
            _getPanelPath = getPanelPath;
            _getPanelEncoding = getPanelEncoding;
            editor.TextChanged += (s, e) =>
            {
                _refreshTimer.Stop();
                _refreshTimer.Start();
            };
            // パラメーターのタブでブレーキ方式を変えたあとにパネルのタブを開いたとき、チェックをやり直す
            IsVisibleChanged += (s, e) =>
            {
                if (IsVisible) Refresh();
            };
            Refresh();
        }

        // パラメーターのテキストを読む（Attach でもらう）
        private Func<string>? _getParamText;

        // 次に表示し直したときに選ぶセクション（名前と何個目か）。セクションを足した・消したあとに使う
        private (string Name, int Occurrence)? _selectAfterRefresh;

        // 「セクションを足す」で選べるセクションと説明（[This] は 1 つだけなので、ないときだけ足せる）
        // 右クリックメニューの「定型文を入れる」（EditorTemplates）でも使う
        internal static (string Name, string Description)[] AddableSections =>
        [
            (ThisSection, Loc.T("Pf.030")),
            ("PilotLamp", Loc.T("Pf.031")),
            ("Needle", Loc.T("Pf.032")),
            ("DigitalNumber", Loc.T("Pf.033")),
            ("DigitalGauge", Loc.T("Pf.034")),
        ];

        // パネルファイルのフルパスを読む（Attach でもらう。参照ボタンでファイルが替わることがあるので、そのつど読む）
        private Func<string?>? _getPanelPath;

        // パネルファイルの今の文字コードを読む（Attach でもらう）
        private Func<System.Text.Encoding>? _getPanelEncoding;

        // 書き直したあとのヘッダー（文字コードの指定は、Shift_JIS のときだけ後ろに付ける）
        private const string UpgradedHeader = "BveTs Instrument Panel 2.01";

        // 古い形式で # を付けて書けた色のキー
        private static readonly string[] ColorKeys = ["Color", "TransparentColor"];

        // ヘッダーのバージョン（2.00・2.01。古い形式は 1.0）。読めなければ null
        private decimal? _panelVersion;

        // 画面に出すバージョンの文字（「2.00」「Version 1.0」など）
        private string _versionText = string.Empty;

        // テキストの行（コメントも含めたそのままの行。2.00 以降で色の前に # を付けたかを見るため）
        private string[] _rawLines = [];

        // 2.01 で増えた項目（2.00 と古い形式 Version 1.0 では使えない。ユーザーのメモとページの NEW の印）
        private static readonly string[] KeysSince201 = ["Perspective", "Tilt", "StopPin"];

        // この版より前のヘッダーでは、上の項目は使えない
        private const decimal FirstVersionWithNewKeys = 2.01m;

        // 画像の大きさの覚え（パスと更新日時ごと。表示し直すたびに画像を開かないため）
        private readonly Dictionary<(string Path, DateTime Time), (int Width, int Height)?> _imageSizes = [];

        /// <summary>
        /// テキストを読み直して、セクションの一覧と選んだセクションを表示する
        /// 選んでいたセクション（名前と何個目か）は、読み直しても同じものを選ぶ
        /// </summary>
        public void Refresh()
        {
            if (_editor == null) return;

            _isRefreshing = true;
            try
            {
                PanelSection? selected = CmbSection.SelectedIndex >= 0 && CmbSection.SelectedIndex < _sections.Count ? _sections[CmbSection.SelectedIndex] : null;
                string text = _editor.Text;
                bool isLegacy = IsLegacyPanel(text);
                _isLegacy = isLegacy;
                _sections = ReadSections(text, isLegacy);
                _rawLines = [.. text.Split('\n').Select(line => line.TrimEnd('\r'))];
                (_panelVersion, _versionText) = ReadPanelVersion(text, isLegacy);

                CmbSection.ItemsSource = _sections.Select(DisplayNameOf).ToList();
                CmbSection.IsEnabled = _sections.Count > 0;
                int index = selected == null ? -1 : _sections.FindIndex(section => IsSameSection(section, selected));
                // セクションを足した・消したあとは、決めておいたセクションを選ぶ
                if (_selectAfterRefresh != null)
                {
                    (string name, int occurrence) = _selectAfterRefresh.Value;
                    index = _sections.FindIndex(section => string.Equals(section.Name, name, StringComparison.OrdinalIgnoreCase) && section.Occurrence == occurrence);
                    _selectAfterRefresh = null;
                }
                CmbSection.SelectedIndex = index >= 0 ? index : (_sections.Count > 0 ? 0 : -1);

                // パネルファイルを読み込めていない（テキストが空）ときは、足せない。選んでいなければ消せない
                BtnAddSection.IsEnabled = text.Length > 0;
                BtnRemoveSection.IsEnabled = CmbSection.SelectedIndex >= 0;

                ShowNotice(text, isLegacy);
                ShowSelectedSection();
            }
            finally
            {
                _isRefreshing = false;
            }
        }

        // セクションを選んだとき：その中身を表示し、下のテキストをそのセクションの行までスクロールする
        private void CmbSection_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ShowSelectedSection();
            if (_isRefreshing || _editor == null || CmbSection.SelectedIndex < 0) return;

            _editor.ScrollToLine(_sections[CmbSection.SelectedIndex].LineNumber);
        }

        // 選んでいるセクションの「キー = 値」を表示する
        private void ShowSelectedSection()
        {
            int index = CmbSection.SelectedIndex;
            if (index < 0 || index >= _sections.Count)
            {
                GrpSection.Header = Loc.T("Pf.035");
                GrpSection.Visibility = Visibility.Visible;
                GrpThis.Visibility = Visibility.Collapsed;
                GrpInstrument.Visibility = Visibility.Collapsed;
                GrpSpecific.Visibility = Visibility.Collapsed;
                ListEntries.ItemsSource = null;
                TxtSectionEmpty.Visibility = Visibility.Collapsed;
                return;
            }

            PanelSection section = _sections[index];
            GrpSection.Header = Loc.F("Pf.036", DisplayNameOf(section), section.LineNumber);
            ListEntries.ItemsSource = section.Entries;
            TxtSectionEmpty.Visibility = section.Entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            // [This] は欄で出す（一覧は隠す）
            bool isThis = string.Equals(section.Name, ThisSection, StringComparison.OrdinalIgnoreCase);
            GrpThis.Visibility = isThis ? Visibility.Visible : Visibility.Collapsed;
            if (isThis)
            {
                GrpInstrument.Visibility = Visibility.Collapsed;
                GrpSpecific.Visibility = Visibility.Collapsed;
                GrpSection.Visibility = Visibility.Collapsed;
                List<string> thisNotices = [];
                List<string> thisProblems = ShowFields(section, _thisFields, thisNotices);
                CheckImages(section, thisProblems, thisNotices);
                ShowWarnings(TxtThisWarnings, thisProblems);
                ShowNotices(TxtThisNotice, thisNotices);
                return;
            }

            // 計器は共通の項目と計器ごとの項目を欄で出し、一覧には欄にない項目だけを出す。知らないセクションは一覧のまま
            bool isInstrument = InstrumentSections.Contains(section.Name, StringComparer.OrdinalIgnoreCase);
            GrpInstrument.Visibility = isInstrument ? Visibility.Visible : Visibility.Collapsed;
            GrpSpecific.Visibility = Visibility.Collapsed;
            GrpSection.Visibility = Visibility.Visible;
            if (!isInstrument) return;

            PanelField[] fields = ShownInstrumentFields(section);
            ShowInstrumentRows(section);
            List<string> notices = [];
            List<string> problems = ShowFields(section, fields, notices);
            ShowSubject(section, problems);
            if (!string.Equals(section.Name, DigitalGaugeSection, StringComparison.OrdinalIgnoreCase)) CheckImages(section, problems, notices);
            ShowWarnings(TxtInstrumentWarnings, problems);
            ShowNotices(TxtInstrumentNotice, notices);

            string[] specificKeys = SpecificKeys.TryGetValue(section.Name, out string[]? keys) ? keys : [];
            ShowSpecific(section, specificKeys);

            // 欄に出した項目（共通・計器ごと）以外を「そのほかの項目」に出す（書き間違えたキーなどが見えるように）
            List<EntryRow> others = [.. section.Entries.Where(entry =>
                !fields.Any(field => string.Equals(field.Key, entry.Key, StringComparison.OrdinalIgnoreCase))
                && !specificKeys.Contains(entry.Key, StringComparer.OrdinalIgnoreCase))];
            GrpSection.Header = Loc.F("Pf.037", section.LineNumber);
            ListEntries.ItemsSource = others;
            GrpSection.Visibility = others.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // 表示する状態量（Subject）の説明を欄の下に出し、名前がおかしければ problems に足す
        // sap・bp・er はパラメーターのブレーキ方式も見る（パラメーターを読み込めていなければ調べない）
        private void ShowSubject(PanelSection section, List<string> problems)
        {
            string? subject = ValueOf(section, "Subject");
            if (subject == null || subject.Length == 0)
            {
                TxtSubjectDescription.Text = Loc.T("Pf.038");
                return;
            }

            TxtSubjectDescription.Text = PanelSubjects.Describe(subject) ?? Loc.T("Pf.039");
            (string Section, string DisplayName)? brake = _getParamText == null ? null : ParameterForm.ReadBrakeSystem(_getParamText());
            string? problem = PanelSubjects.FindProblem(subject, brake?.Section, brake?.DisplayName);
            if (problem != null) problems.Add(problem);
        }

        // 「セクションを足す」を押したとき：足せるセクションの一覧を出す（[This] がすでにあれば選べない）
        private void BtnAddSection_Click(object sender, RoutedEventArgs e)
        {
            ContextMenu menu = new() { PlacementTarget = BtnAddSection, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            bool hasThis = HasThisSection();
            foreach ((string name, string description) in AddableSections)
            {
                bool isThis = name == ThisSection;
                MenuItem item = new() { Header = $"[{name}]　{description}{(isThis && hasThis ? "（もうあります）" : string.Empty)}", IsEnabled = !(isThis && hasThis) };
                item.Click += (s, args) => AddSection(name);
                menu.Items.Add(item);
            }
            menu.IsOpen = true;
        }

        // セクションをテキストの最後に足し、表示し直したらそのセクションを選ぶ（Ctrl+Z で戻せる）
        private void AddSection(string name)
        {
            if (_editor == null || _editor.Text.Length == 0) return;

            int occurrence = _sections.Count(section => string.Equals(section.Name, name, StringComparison.OrdinalIgnoreCase)) + 1;
            _selectAfterRefresh = (name, occurrence);
            EditorTextHelper.ReplaceChangedText(_editor, BveTextParser.RenameSection(_editor.Text, null, name, hashIsComment: !_isLegacy));
            OperationHistory.Add($"パネル入力: [{name}] を足した（{occurrence} 個目）");
        }

        // 「このセクションを消す」を押したとき：中の値の数を見せて確認してから消す
        private void BtnRemoveSection_Click(object sender, RoutedEventArgs e)
        {
            int index = CmbSection.SelectedIndex;
            if (index < 0 || index >= _sections.Count) return;

            PanelSection section = _sections[index];
            // [This] を消すと、BVE で運転台が丸ごと見えなくなるので、確認の文に書き足し、注意のアイコンにする
            bool isThis = string.Equals(section.Name, ThisSection, StringComparison.OrdinalIgnoreCase);
            string thisWarning = isThis ? Loc.T("Pf.040") : string.Empty;
            MessageBoxResult answer = MessageBox.Show(
                Loc.F("Pf.041", DisplayNameOf(section), section.Entries.Count, thisWarning),
                Loc.T("Pf.042"),
                MessageBoxButton.YesNo,
                isThis ? MessageBoxImage.Warning : MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;

            RemoveSelectedSection();
        }

        // 選んでいるセクションを消す。消したあとは、1 つ前のセクションを選ぶ（先頭を消したら新しい先頭）
        private void RemoveSelectedSection()
        {
            int index = CmbSection.SelectedIndex;
            if (_editor == null || index < 0 || index >= _sections.Count) return;

            PanelSection section = _sections[index];
            _selectAfterRefresh = index > 0 ? (_sections[index - 1].Name, _sections[index - 1].Occurrence) : null;
            EditorTextHelper.ReplaceChangedText(_editor, BveTextParser.RemoveSectionAt(_editor.Text, section.Name, section.Occurrence, hashIsComment: !_isLegacy));
            OperationHistory.Add($"パネル入力: [{section.Name}] {section.Occurrence} 個目を消した（値 {section.Entries.Count} 件）");
        }

        // 「一覧から選ぶ」を押したとき：状態量の一覧（名前と説明）を出し、選んだものを Subject に書き込む
        private void BtnSubjectList_Click(object sender, RoutedEventArgs e)
        {
            ContextMenu menu = new() { PlacementTarget = BtnSubjectList, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            foreach (PanelSubjects.Subject subject in PanelSubjects.All)
            {
                MenuItem item = new() { Header = $"{subject.Key}　{subject.Description}" };
                item.Click += (s, args) => WriteToSelectedSection("Subject", subject.Key);
                menu.Items.Add(item);
            }
            menu.IsOpen = true;
        }

        // 計器ごとの欄を出す。その計器で使う行だけを出し、項目名を計器に合わせる。使う項目がなければ枠ごと隠す
        private void ShowSpecific(PanelSection section, string[] keys)
        {
            GrpSpecific.Visibility = keys.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (keys.Length == 0) return;

            ShowSpecificRows(section, keys);
            PanelField[] fields = [.. _specificFields.Where(field => keys.Contains(field.Key, StringComparer.OrdinalIgnoreCase))];
            List<string> notices = [];
            List<string> problems = ShowFields(section, fields, notices);

            // StopPin（True / False）はチェックボックス。書かれていない・読めない値なら中間の印と既定値
            // 2.00 と古い形式では使えないので灰色にする（書かれていれば警告）
            if (keys.Contains("StopPin"))
            {
                string? stopPin = ValueOf(section, "StopPin");
                bool stopPinUnavailable = IsUnavailableByVersion("StopPin");
                ChkSpStopPin.IsEnabled = !stopPinUnavailable;
                if (stopPinUnavailable) notices.Add(VersionNotice(Loc.T("Pf.043"), "StopPin"));
                if (stopPinUnavailable && stopPin != null) problems.Add(VersionProblem(Loc.T("Pf.044"), "StopPin"));
                bool? isChecked = stopPin?.ToLowerInvariant() switch
                {
                    "true" => true,
                    "false" => false,
                    _ => null,
                };
                ChkSpStopPin.IsChecked = isChecked;
                ChkSpStopPin.Content = isChecked == null ? Loc.F("Pf.045", StopPinDefault) : Loc.T("Pf.046");
                if (stopPin != null && isChecked == null && !stopPinUnavailable) problems.Add(Loc.F("Pf.047", stopPin));
            }
            ShowWarnings(TxtSpecificWarnings, problems);
            ShowNotices(TxtSpecificNotice, notices);
        }

        // 計器ごとの欄の行を出し分け、項目名を計器に合わせて変える（同じキーでも [Needle] と [DigitalGauge] で意味が違う）
        private void ShowSpecificRows(PanelSection section, string[] keys)
        {
            foreach ((string key, TextBlock label) in _specificLabels)
            {
                Visibility visibility = keys.Contains(key, StringComparer.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;
                label.Visibility = visibility;
                FrameworkElement input = key == "StopPin" ? ChkSpStopPin : _specificFields.First(field => field.Key == key).Box;
                input.Visibility = visibility;
            }

            bool isNeedle = string.Equals(section.Name, "Needle", StringComparison.OrdinalIgnoreCase);
            LblSpColor.Text = isNeedle ? Loc.T("Pf.048") : Loc.T("Pf.049");
            LblSpRadius.Text = isNeedle ? Loc.T("Pf.050") : Loc.T("Pf.051");
            LblSpInitialAngle.Text = isNeedle ? Loc.T("Pf.052") : Loc.T("Pf.053");
            LblSpLastAngle.Text = isNeedle ? Loc.T("Pf.054") : Loc.T("Pf.055");
        }

        // StopPin をクリックしたとき：選んでいるセクションの StopPin を True / False に書き換える
        private void ChkSpStopPin_Click(object sender, RoutedEventArgs e)
        {
            if (_isRefreshing) return;
            WriteToSelectedSection("StopPin", ChkSpStopPin.IsChecked == true ? "True" : "False");
        }

        // 警告の一覧を出す（なければ隠す）
        private static void ShowWarnings(TextBlock warnings, List<string> problems)
        {
            warnings.Text = string.Join("\n", problems.Select(problem => "⚠ " + problem));
            warnings.Visibility = problems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // その計器で使う共通の欄（[DigitalGauge] は画像と透過色を使わない）
        private PanelField[] ShownInstrumentFields(PanelSection section)
        {
            bool isGauge = string.Equals(section.Name, DigitalGaugeSection, StringComparison.OrdinalIgnoreCase);
            return isGauge ? [.. _instrumentFields.Except(_imageFields)] : _instrumentFields;
        }

        // 計器の種類に合わせて、欄の行を出し分け、位置の名前を変える
        private void ShowInstrumentRows(PanelSection section)
        {
            bool isGauge = string.Equals(section.Name, DigitalGaugeSection, StringComparison.OrdinalIgnoreCase);
            Visibility imageVisibility = isGauge ? Visibility.Collapsed : Visibility.Visible;
            foreach (UIElement element in new UIElement[] { LblInstDaytimeImage, TxtInstDaytimeImage, LblInstNighttimeImage, TxtInstNighttimeImage, LblInstTransparentColor, TxtInstTransparentColor })
            {
                element.Visibility = imageVisibility;
            }

            // Location の意味は計器ごとに違う（ページの説明に合わせる）
            LblInstLocation.Text = section.Name.ToUpperInvariant() switch
            {
                "PILOTLAMP" => Loc.T("Pf.056"),
                "DIGITALNUMBER" => Loc.T("Pf.057"),
                _ => Loc.T("Pf.058"),
            };
        }

        // セクションの値を欄に出す。書かれていなければ既定値（なければ「（未記入）」）を薄く出す。形が合わない項目の警告の文を返す
        // ヘッダーのバージョンで使えない項目は灰色にして notices に知らせ、書かれていれば警告
        private List<string> ShowFields(PanelSection section, PanelField[] fields, List<string> notices)
        {
            List<string> problems = [];
            foreach (PanelField field in fields)
            {
                bool unavailable = IsUnavailableByVersion(field.Key);
                field.Box.IsEnabled = !unavailable;
                if (unavailable) notices.Add(VersionNotice(field.Label, field.Key));

                EntryRow? entry = EntryOf(section, field.Key);
                string? value = entry?.Value;
                if (!field.Box.IsKeyboardFocusWithin) ShowText(field.Box, value, PlaceholderOf(field));
                if (entry == null) continue;

                if (unavailable)
                {
                    problems.Add(VersionProblem(field.Label, field.Key));
                    continue;
                }

                // 2.00 以降は # から後ろがコメントになるので、「#00FFFF」と書くと色が読まれない
                if (field.Kind == ValueKind.Color && !_isLegacy && entry.Value.Length == 0 && RawLineOf(entry).Contains('#'))
                {
                    problems.Add(Loc.F("Pf.059", field.Label, field.Key));
                    continue;
                }

                string? problem = FindValueProblem(field, entry.Value);
                if (problem != null) problems.Add(problem);
            }
            return problems;
        }

        // ヘッダーのバージョンで使えない項目か（Perspective・Tilt・StopPin は 2.01 から。バージョンが読めなければ使える扱い）
        private bool IsUnavailableByVersion(string key)
        {
            return _panelVersion != null && _panelVersion < FirstVersionWithNewKeys && KeysSince201.Contains(key, StringComparer.OrdinalIgnoreCase);
        }

        // バージョンで使えない項目のお知らせと警告の文
        private string VersionNotice(string label, string key) => Loc.F("Pf.060", _versionText, label, key);
        private string VersionProblem(string label, string key) => Loc.F("Pf.061", label, key, _versionText);

        // セクションに書かれているキーの行（同じキーが何回もあれば最後のもの）。なければ null
        private static EntryRow? EntryOf(PanelSection section, string key)
        {
            return section.Entries.LastOrDefault(entry => string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase));
        }

        // その行のテキスト（コメントも含めたそのまま）
        private string RawLineOf(EntryRow entry)
        {
            return entry.LineNumber - 1 < _rawLines.Length ? _rawLines[entry.LineNumber - 1] : string.Empty;
        }

        // 画像を調べる（[This] と、画像を使う計器）
        // ・ファイルがなければ警告（パネルファイルからの相対パス。パネルファイルの場所が分からなければ調べない）
        // ・幅・高さが 2 のべき乗でなければお知らせ（DirectX の仕様で、画像が間延び・縮んだり崩れたりする。ユーザーの説明）
        // （png に透過色を書いたときの警告は、サンプルでも出たのでユーザー判断で入れない）
        private void CheckImages(PanelSection section, List<string> problems, List<string> notices)
        {
            string? panelPath = _getPanelPath?.Invoke();
            string? panelFolder = panelPath == null ? null : System.IO.Path.GetDirectoryName(panelPath);
            if (panelFolder == null) return;

            foreach ((string key, string label) in new[] { ("DaytimeImage", Loc.T("Pf.062")), ("NighttimeImage", Loc.T("Pf.063")) })
            {
                string? image = ValueOf(section, key);
                if (string.IsNullOrWhiteSpace(image)) continue;

                string? fullPath = TryCombine(panelFolder, image);
                if (fullPath == null || !System.IO.File.Exists(fullPath))
                {
                    problems.Add(Loc.F("Pf.064", label, key, image));
                    continue;
                }

                (int Width, int Height)? size = ReadImageSize(fullPath);
                if (size == null) continue;
                if (IsPowerOfTwo(size.Value.Width) && IsPowerOfTwo(size.Value.Height)) continue;
                notices.Add(Loc.F("Pf.065", label, image, size.Value.Width, size.Value.Height));
            }
        }

        // フォルダーとファイルパスをつなぐ。使えない文字などでつなげなければ null
        private static string? TryCombine(string folder, string path)
        {
            try
            {
                return System.IO.Path.GetFullPath(System.IO.Path.Combine(folder, path));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.IO.PathTooLongException)
            {
                return null;
            }
        }

        // 画像の幅と高さ（画像の中身は読まず、大きさだけを読む）。読めなければ null
        private (int Width, int Height)? ReadImageSize(string fullPath)
        {
            DateTime time = System.IO.File.GetLastWriteTimeUtc(fullPath);
            if (_imageSizes.TryGetValue((fullPath, time), out (int Width, int Height)? cached)) return cached;

            (int Width, int Height)? size = null;
            try
            {
                using var stream = new System.IO.FileStream(fullPath, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite);
                var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(stream, System.Windows.Media.Imaging.BitmapCreateOptions.DelayCreation, System.Windows.Media.Imaging.BitmapCacheOption.None);
                size = (decoder.Frames[0].PixelWidth, decoder.Frames[0].PixelHeight);
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or InvalidOperationException)
            {
                size = null;
            }
            _imageSizes[(fullPath, time)] = size;
            return size;
        }

        // 2 のべき乗か（1・2・4・8 …）
        private static bool IsPowerOfTwo(int value) => value > 0 && (value & (value - 1)) == 0;

        // お知らせの一覧を出す（なければ隠す）
        private static void ShowNotices(TextBlock notice, List<string> notices)
        {
            notice.Text = string.Join("\n", notices.Distinct());
            notice.Visibility = notices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // セクションに書かれているキーの値（同じキーが何回もあれば最後のもの。大文字小文字は区別しない）。なければ null
        private static string? ValueOf(PanelSection section, string key)
        {
            return section.Entries.LastOrDefault(entry => string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase))?.Value;
        }

        // 書かれていない欄に出す文字
        private static string PlaceholderOf(PanelField field)
        {
            return field.DefaultValue == null ? Loc.T("Pf.066") : Loc.F("Pf.067", field.DefaultValue);
        }

        // 値の形が合っているか。合っていなければ警告の文、合っていれば null
        private string? FindValueProblem(PanelField field, string value)
        {
            return field.Kind switch
            {
                ValueKind.Number when !TryReadNumber(value) => Loc.F("Pf.068", field.Label, field.Key, value),
                ValueKind.Point when !IsPoint(value) => Loc.F("Pf.069", field.Label, field.Key, value),
                ValueKind.Color when !IsColor(value) => Loc.F("Pf.070", field.Label, field.Key, value),
                ValueKind.Layer when !IsLayer(value) => Loc.F("Pf.071", field.Label, field.Key, value),
                _ => null,
            };
        }

        // 数字として読めるか（小数・マイナスも可）
        private static bool TryReadNumber(string value)
        {
            return double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _);
        }

        // 描画順序として使える 0 ～ 255 の整数か（上限 255 は Plan.md のメモ。ユーザー判断。ページは「ベース画像は 0」）
        private static bool IsLayer(string value)
        {
            return int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int layer) && layer <= 255;
        }

        // 「x, y」の2つの数字か
        private static bool IsPoint(string value)
        {
            string[] items = value.Split(',');
            return items.Length == 2 && items.All(item => TryReadNumber(item.Trim()));
        }

        // 6 桁の 16 進数の色か。古い形式では先頭の # も許す（2.00 以降は # がコメントになるので付けない）
        private bool IsColor(string value)
        {
            string color = _isLegacy && value.StartsWith('#') ? value[1..] : value;
            return Regex.IsMatch(color, "^[0-9A-Fa-f]{6}$");
        }

        // 欄に入ったとき：今の値を覚え、「（未記入）」なら消して打てるようにする
        private void BeginEdit(TextBox box)
        {
            string text = box.Text;
            bool isNotWritten = ClearPlaceholder(box);
            _valuesOnFocus[box] = isNotWritten ? null : text;
        }

        // 欄から出たとき・Enter：変わっていれば、選んでいるセクションの「キー = 値」を書き換える（なければ足す。Ctrl+Z で戻せる）
        // isLeaving：欄から出たとき true、Enter のとき false（欄に入ったまま）
        private void CommitField(PanelField field, bool isLeaving)
        {
            TextBox box = field.Box;
            if (_isRefreshing || _editor == null || !_valuesOnFocus.TryGetValue(box, out string? original)) return;
            // 「（未記入）」を出しているだけなら、何も打っていないので書き込まない
            if (IsShowingPlaceholder(box)) return;

            string value = box.Text.Trim();
            if (value.Length == 0)
            {
                RestoreEmptyBox(box, original, PlaceholderOf(field), isLeaving);
                return;
            }
            if (value == original) return;

            // ; と #（古い形式では ; だけ）はコメントの始まりなので、値に入れると BVE がそこから後ろを読まない
            if (BveTextParser.FindCommentIndex(value, hashIsComment: !_isLegacy) >= 0)
            {
                string chars = _isLegacy ? "「;」" : Loc.T("Pf.072");
                MessageBox.Show(Loc.F("Pf.073", chars), Loc.T("Pf.074"), MessageBoxButton.OK, MessageBoxImage.Warning);
                ShowText(box, original, PlaceholderOf(field));
                return;
            }

            if (!WriteToSelectedSection(field.Key, value)) return;
            _valuesOnFocus[box] = value;
        }

        // 選んでいるセクションの「キー = 値」を書き換える（なければ足す。Ctrl+Z で戻せる）。選んでいなければ false
        // 同じ名前のセクションが何個もあるので、選んでいる「何個目」のセクションの中だけを書き換える
        private bool WriteToSelectedSection(string key, string value)
        {
            int index = CmbSection.SelectedIndex;
            if (_editor == null || index < 0 || index >= _sections.Count) return false;

            PanelSection section = _sections[index];
            string newText = BveTextParser.SetValueInSection(_editor.Text, section.Name, section.Occurrence, key, value, hashIsComment: !_isLegacy);
            EditorTextHelper.ReplaceChangedText(_editor, newText);
            OperationHistory.Add($"パネル入力: [{section.Name}] {section.Occurrence} 個目 {key} = {value}");
            return true;
        }

        // Esc：欄に入ったときの値に戻す（書かれていなかった欄なら空のまま続けて打てる）
        private void CancelField(PanelField field)
        {
            if (!_valuesOnFocus.TryGetValue(field.Box, out string? original)) return;
            RestoreEmptyBox(field.Box, original, PlaceholderOf(field), isLeaving: false);
        }

        // セクションがない・古い形式、などを知らせる
        private void ShowNotice(string text, bool isLegacy)
        {
            string notice = string.Empty;
            if (text.Length > 0 && _sections.Count == 0) notice = Loc.T("Pf.075");
            if (isLegacy) notice = Loc.T("Pf.076");
            // [This] が 2 回以上書かれていれば知らせる（計器のセクションは何個でも書けるので知らせない）
            List<BveTextLine> sectionLines = [.. _sections.Select(section => new BveTextLine(section.LineNumber, BveLineKind.Section, section.Name, null, string.Empty))];
            List<string> duplicates = BveTextParser.DescribeDuplicateSections(sectionLines, name => string.Equals(name.Trim(), ThisSection, StringComparison.OrdinalIgnoreCase), "Common.DuplicateSectionSeparate");
            notice = string.Join("\n", new[] { notice }.Concat(duplicates).Where(item => item.Length > 0));
            TxtPanelNotice.Text = notice;
            TxtPanelNotice.Visibility = notice.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            BtnUpgradeLegacy.Visibility = isLegacy ? Visibility.Visible : Visibility.Collapsed;

            // [This] がないと、BVE で運転台（パネル）が丸ごと見えなくなる（ユーザーが確認）。パネルを読み込めていなければ出さない
            bool lacksThis = text.Length > 0 && !HasThisSection();
            List<string> warnings = lacksThis ? [Loc.T("Pf.077")] : [];
            // 公式の一覧にないセクション・キー（書き間違いかもしれない。Pf.077 は先頭に ⚠ が付いている）
            warnings.AddRange(BveTextParser.FindUnknownNames(BveTextParser.Parse(text, hashIsComment: !isLegacy), KnownKeys).Select(problem => "⚠ " + problem));
            TxtPanelWarnings.Text = string.Join("\n", warnings);
            TxtPanelWarnings.Visibility = warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// 古い形式（Version 1.0）のテキストを BveTs Instrument Panel 2.01 に書き直した結果
        /// </summary>
        /// <param name="Text">書き直したテキスト</param>
        /// <param name="Header">新しいヘッダー</param>
        /// <param name="ColorFixCount">色の前の # を外した数</param>
        /// <param name="OtherHashLines">色のほかに # が残っている行番号（2.01 ではそこから後ろがコメントになる）</param>
        private sealed record UpgradeResult(string Text, string Header, int ColorFixCount, List<int> OtherHashLines);

        // 古い形式のテキストを 2.01 に書き直す（画面にはまだ反映しない）
        // ・1 行目を「BveTs Instrument Panel 2.01」にする（Shift_JIS のファイルなら「:shift_jis」を付ける。付けないと BVE が UTF-8 として読んで文字化けする）
        // ・Color・TransparentColor の値の前の # を外す（2.01 では # から後ろがコメントになるため）
        // ・それ以外に # がある行（パスなど）は直さず、行番号を返す
        private static UpgradeResult BuildUpgrade(string text, System.Text.Encoding encoding)
        {
            string newLine = text.Contains("\r\n") ? "\r\n" : "\n";
            List<string> lines = [.. text.Split('\n').Select(line => line.TrimEnd('\r'))];
            string header = UpgradedHeader + (encoding.CodePage == 932 ? ":shift_jis" : string.Empty);
            lines[0] = header;

            int colorFixCount = 0;
            List<int> otherHashLines = [];
            for (int i = 1; i < lines.Count; i++)
            {
                // 古い形式のコメントは ; だけなので、; より前を調べる
                string line = lines[i];
                int commentIndex = BveTextParser.FindCommentIndex(line, hashIsComment: false);
                string content = commentIndex < 0 ? line : line[..commentIndex];
                string comment = commentIndex < 0 ? string.Empty : line[commentIndex..];

                Match color = Regex.Match(content, @"^(\s*(\w+)\s*=\s*)#", RegexOptions.None);
                if (color.Success && ColorKeys.Contains(color.Groups[2].Value, StringComparer.OrdinalIgnoreCase))
                {
                    content = color.Groups[1].Value + content[color.Length..];
                    lines[i] = content + comment;
                    colorFixCount++;
                }
                if (content.Contains('#')) otherHashLines.Add(i + 1);
            }
            return new(string.Join(newLine, lines), header, colorFixCount, otherHashLines);
        }

        // 「2.01 に書き直す」を押したとき：何を変えるか見せて確認してから書き直す（Ctrl+Z で戻せる）
        // 色のほかに # が残っている行があれば、書き直したあとに知らせる
        private void BtnUpgradeLegacy_Click(object sender, RoutedEventArgs e)
        {
            if (_editor == null || _getPanelEncoding == null) return;

            UpgradeResult result = BuildUpgrade(_editor.Text, _getPanelEncoding());
            string encodingNote = result.Header.EndsWith(":shift_jis") ? Loc.T("Pf.078") : string.Empty;
            MessageBoxResult answer = MessageBox.Show(
                Loc.T("Pf.079") +
                Loc.F("Pf.080", result.Header, encodingNote) +
                Loc.F("Pf.081", result.ColorFixCount) +
                Loc.T("Pf.082"),
                Loc.T("Pf.083"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;

            ApplyUpgrade(result);
            if (result.OtherHashLines.Count == 0) return;

            MessageBox.Show(
                Loc.F("Pf.084", string.Join(Loc.T("Pf2.001"), result.OtherHashLines.Select(line => Loc.F("Pf2.002", line)))),
                Loc.T("Pf.085"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        // 書き直したテキストを画面に入れる（変わった部分だけ置き換えるので Ctrl+Z で戻せる）
        private void ApplyUpgrade(UpgradeResult result)
        {
            if (_editor == null) return;

            EditorTextHelper.ReplaceChangedText(_editor, result.Text);
            OperationHistory.Add($"パネル入力: 古い形式を {result.Header} に書き直した（色の # を {result.ColorFixCount} か所外した）");
        }

        // [This] が書かれているか
        private bool HasThisSection()
        {
            return _sections.Any(section => string.Equals(section.Name, ThisSection, StringComparison.OrdinalIgnoreCase));
        }

        // 選ぶ欄に出す名前。同じ名前が2つ以上あれば「[Needle] 2つ目」、コメントや Subject があれば後ろに付ける
        // 例：「[PilotLamp] 2つ目：知らせ灯（ats19）」
        private string DisplayNameOf(PanelSection section)
        {
            int count = _sections.Count(other => string.Equals(other.Name, section.Name, StringComparison.OrdinalIgnoreCase));
            string name = count > 1 ? Loc.F("Pf.086", section.Name, section.Occurrence) : $"[{section.Name}]";
            if (section.Comment.Length > 0) name += $"：{section.Comment}";

            string? subject = section.Entries.LastOrDefault(entry => string.Equals(entry.Key, "Subject", StringComparison.OrdinalIgnoreCase))?.Value;
            if (!string.IsNullOrEmpty(subject)) name += $"（{subject}）";
            return name;
        }

        // 同じセクションか（名前と何個目かで比べる）
        private static bool IsSameSection(PanelSection a, PanelSection b)
        {
            return string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase) && a.Occurrence == b.Occurrence;
        }

        // 古い形式「Version 1.0」のパネルか（1行目で見る）。この形式では # をコメントにしない
        private static bool IsLegacyPanel(string text)
        {
            string firstLine = text.TrimStart('﻿').Split('\n')[0].Trim();
            return Regex.IsMatch(firstLine, @"^version\s+1\.0\b", RegexOptions.IgnoreCase);
        }

        // ヘッダー（1行目）のバージョンを読む。古い形式（Version 1.0）は 1.0。「BveTs Instrument Panel 2.01」なら 2.01。読めなければ null
        private static (decimal? Version, string Text) ReadPanelVersion(string text, bool isLegacy)
        {
            if (isLegacy) return (1.0m, "Version 1.0");

            string firstLine = text.TrimStart('﻿').Split('\n')[0].Trim();
            Match match = Regex.Match(firstLine, @"Instrument\s+Panel\s+(\d+\.\d+)", RegexOptions.IgnoreCase);
            if (!match.Success) return (null, string.Empty);
            return (decimal.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), match.Groups[1].Value);
        }

        // テキストからセクションを上から順に読む。同じ名前のセクションには、何個目かの番号を付ける
        private static List<PanelSection> ReadSections(string text, bool isLegacy)
        {
            List<PanelSection> sections = [];
            Dictionary<string, int> counts = new(StringComparer.OrdinalIgnoreCase);
            string[] rawLines = text.Split('\n');

            foreach (BveTextLine line in BveTextParser.Parse(text, hashIsComment: !isLegacy))
            {
                if (line.Kind == BveLineKind.Section)
                {
                    string name = line.Section!;
                    counts[name] = counts.TryGetValue(name, out int count) ? count + 1 : 1;
                    sections.Add(new(name, counts[name], line.LineNumber, CommentOf(rawLines[line.LineNumber - 1], isLegacy), []));
                    continue;
                }

                // セクションの前（ヘッダーなど）は飛ばす。「キー = 値」だけ、今のセクションに入れる
                if (sections.Count == 0 || line.Kind != BveLineKind.KeyValue) continue;
                sections[^1].Entries.Add(new(line.Key!, line.Value, line.LineNumber));
            }
            return sections;
        }

        // 1行のコメント部分（; や # の後ろ）。なければ空
        private static string CommentOf(string rawLine, bool isLegacy)
        {
            int index = BveTextParser.FindCommentIndex(rawLine, hashIsComment: !isLegacy);
            return index < 0 ? string.Empty : rawLine[(index + 1)..].Trim();
        }
    }
}
