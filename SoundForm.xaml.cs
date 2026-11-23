using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using Microsoft.Win32;

namespace BveVehicleEditor
{
    /// <summary>
    /// サウンドのファイル（Sound.txt）の入力フォーム
    /// 段階1：セクションを選ぶと、名前のキーのセクション（[Horn] など）はキーごと、番号のセクション（[Run] など）は書かれている番号ごとに、
    /// 音のファイルのパスと「参照」ボタンを出す。参照で選ぶと下のテキストを書き換える（行やセクションがなければ足す）
    /// 下のテキストエディタが元になり、テキストが変わると少しあとに表示し直す（ノッチのタブの NotchForm と同じ作り）
    /// セクションとキーは公式ページ（車両サウンドファイル。BveTs Vehicle Sound 3.01）の一覧
    /// </summary>
    public partial class SoundForm : UserControl
    {
        /// <summary>
        /// サウンドのセクション 1 つ分
        /// </summary>
        /// <param name="Name">セクション名（[ ] の中身）</param>
        /// <param name="Label">画面の名前</param>
        /// <param name="IsNumbered">キーが番号（0 以上の整数）のセクションか</param>
        /// <param name="Keys">名前のキーと画面の名前（番号のセクションでは空）</param>
        private sealed record SoundSection(string Name, string Label, bool IsNumbered, (string Key, string Label)[] Keys);

        // セクションの一覧（公式ページの順）
        private static SoundSection[] Sections =>
        [
            new("Run", Loc.T("Sf.001"), true, []),
            new("Flange", Loc.T("Sf.002"), true, []),
            new("Motor", Loc.T("Sf.003"), true, []),
            new("Joint", Loc.T("Sf.004"), true, []),
            new("Brake", Loc.T("Sf.005"), false,
            [
                ("BcRelease", Loc.T("Sf.006")),
                ("BcReleaseFull", Loc.T("Sf.007")),
                ("BcReleaseHigh", Loc.T("Sf.008")),
                ("Emergency", Loc.T("Sf.009")),
                ("BpDecomp", Loc.T("Sf.010")),
            ]),
            new("Compressor", Loc.T("Sf.011"), false,
            [
                ("Attack", Loc.T("Sf.012")),
                ("Loop", Loc.T("Sf.013")),
                ("Release", Loc.T("Sf.014")),
            ]),
            new("AirSpring", Loc.T("Sf.015"), false,
            [
                ("LeftRelease", Loc.T("Sf.016")),
                ("RightRelease", Loc.T("Sf.017")),
                ("LeftApply", Loc.T("Sf.018")),
                ("RightApply", Loc.T("Sf.019")),
            ]),
            new("Breaker", Loc.T("Sf.020"), false,
            [
                ("On", Loc.T("Sf.021")),
                ("Off", Loc.T("Sf.022")),
            ]),
            new("Horn", Loc.T("Sf.023"), false,
            [
                ("Primary", Loc.T("Sf.024")),
                ("PrimaryRelease", Loc.T("Sf.025")),
                ("Secondary", Loc.T("Sf.026")),
                ("SecondaryRelease", Loc.T("Sf.027")),
                ("Music", Loc.T("Sf.028")),
            ]),
            new("Door", Loc.T("Sf.029"), false,
            [
                ("OpenLeft", Loc.T("Sf.030")),
                ("OpenRight", Loc.T("Sf.031")),
                ("CloseLeft", Loc.T("Sf.032")),
                ("CloseRight", Loc.T("Sf.033")),
            ]),
            new("Ats", Loc.T("Sf.034"), true, []),
            new("Buzzer", Loc.T("Sf.035"), false,
            [
                ("Correct", Loc.T("Sf.036")),
            ]),
            new("PilotLamp", Loc.T("Sf.037"), false,
            [
                ("On", Loc.T("Sf.038")),
                ("Off", Loc.T("Sf.039")),
            ]),
            new("BrakeHandle", Loc.T("Sf.040"), false,
            [
                ("Apply", Loc.T("Sf.041")),
                ("ApplyFast", Loc.T("Sf.042")),
                ("Release", Loc.T("Sf.043")),
                ("ReleaseFast", Loc.T("Sf.044")),
                ("Min", Loc.T("Sf.045")),
                ("Max", Loc.T("Sf.046")),
            ]),
            new("MasterController", Loc.T("Sf.047"), false,
            [
                ("Up", Loc.T("Sf.048")),
                ("UpFast", Loc.T("Sf.049")),
                ("Down", Loc.T("Sf.050")),
                ("DownFast", Loc.T("Sf.051")),
                ("Max", Loc.T("Sf.052")),
                ("Min", Loc.T("Sf.053")),
            ]),
            new("Reverser", Loc.T("Sf.054"), false,
            [
                ("On", Loc.T("Sf.055")),
                ("Off", Loc.T("Sf.056")),
            ]),
            new("Others", Loc.T("Sf.057"), false,
            [
                ("Noise", Loc.T("Sf.058")),
                ("Shoe", Loc.T("Sf.059")),
            ]),
        ];

        // 警告をまとめて出す数（多いときは「ほか ○ 件」）
        private const int MaxWarningLines = 8;

        // [Ats] の番号の上限（公式ページ：キーは 0 ～ 255 の整数）
        private const int MaxAtsNumber = 255;

        // 元になるテキストエディタ
        private TextEditor? _editor;

        // サウンドのファイルのフルパスを読む（Attach でもらう。参照ボタンでファイルが替わることがあるので、そのつど読む）
        private Func<string?>? _getSoundPath;

        // サウンドのファイルの今の文字コードを読む（Attach でもらう。古い形式を書き直すときに使う）
        private Func<Encoding>? _getSoundEncoding;

        // 古い形式を書き直したときのヘッダー（公式ページの今の書式）
        private const string UpgradedHeader = "BveTs Vehicle Sound 3.01";

        // ヘッダーのバージョンで使えるようになったキー（セクション, キー, 使えるバージョン）
        // ・ApplyFast・ReleaseFast・UpFast・DownFast：公式ページで「NEW」の印。3.01 からと考える（ユーザーの判断「この案で作る」。違っていたら直す）
        // ・BpDecomp：Plan.md のユーザーのメモ「Bvets Vehicle Sound 2.0 では存在しない」
        private static readonly (string Section, string Key, decimal MinVersion)[] KeyMinVersions =
        [
            ("BrakeHandle", "ApplyFast", 3.01m),
            ("BrakeHandle", "ReleaseFast", 3.01m),
            ("MasterController", "UpFast", 3.01m),
            ("MasterController", "DownFast", 3.01m),
            ("Brake", "BpDecomp", 3.00m),
        ];

        // テキストを打つたびに読み直すと重いので、打ち終わって少し待ってから1回だけ表示し直す
        private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };

        // コンボボックスを作り直している間は、選び直しの処理をしない
        private bool _isUpdatingCombo;

        // 音を鳴らす部品（WPF の MediaPlayer。1 つだけ鳴らす）と、鳴らしているファイルのフルパス（止まっていれば null）
        private readonly MediaPlayer _player = new();
        private string? _playingPath;

        public SoundForm()
        {
            InitializeComponent();
            _player.MediaEnded += (s, e) => SetPlayingPath(null);
            _player.MediaFailed += Player_MediaFailed;
            // ほかのタブへ移ったら止める
            IsVisibleChanged += (s, e) =>
            {
                if (!IsVisible) StopSound();
            };
            _refreshTimer.Tick += (s, e) =>
            {
                _refreshTimer.Stop();
                Refresh();
            };
            foreach (SoundSection section in Sections) CmbSection.Items.Add(new ComboBoxItem { Tag = section });
            CmbSection.SelectedIndex = 0;
        }

        /// <summary>
        /// 言語を切り替えたあと、セクションの一覧の説明と行を新しい言語で出し直す（選んでいるセクションはそのまま）
        /// </summary>
        public void ApplyLanguage()
        {
            SoundSection[] sections = [.. Sections];
            for (int i = 0; i < CmbSection.Items.Count && i < sections.Length; i++)
            {
                ((ComboBoxItem)CmbSection.Items[i]).Tag = sections[i];
            }
            Refresh();
        }

        /// <summary>
        /// 元になるテキストエディタをつなぐ。テキストが変わったら表示し直す
        /// </summary>
        /// <param name="editor">サウンドのテキストエディタ</param>
        /// <param name="getSoundPath">サウンドのファイルのフルパスを読む（相対パスの基準）。読み込めていなければ null</param>
        /// <param name="getSoundEncoding">サウンドのファイルの今の文字コードを読む（古い形式を書き直すとき、Shift_JIS ならヘッダーに :shift_jis を付けるため）</param>
        public void Attach(TextEditor editor, Func<string?> getSoundPath, Func<Encoding> getSoundEncoding)
        {
            _editor = editor;
            _getSoundPath = getSoundPath;
            _getSoundEncoding = getSoundEncoding;
            editor.TextChanged += (s, e) =>
            {
                _refreshTimer.Stop();
                _refreshTimer.Start();
            };
            // 参照ボタンでサウンドのファイルが替わったあとにタブを開いたとき、見つかるかを調べ直す
            IsVisibleChanged += (s, e) =>
            {
                if (IsVisible) Refresh();
            };
            Refresh();
        }

        /// <summary>
        /// テキストを読み直して、セクションの一覧（書かれている数）・選んでいるセクションの行・警告を表示し直す
        /// </summary>
        public void Refresh()
        {
            if (_editor == null) return;

            string? folder = SoundFolder();
            List<BveTextLine> lines = BveTextParser.Parse(_editor.Text);

            // コンボボックスの名前に、書かれている数を付ける（選んでいるセクションはそのまま）
            _isUpdatingCombo = true;
            foreach (ComboBoxItem item in CmbSection.Items)
            {
                var section = (SoundSection)item.Tag;
                int count = EntriesOf(lines, section).Count;
                bool written = HasSection(lines, section.Name);
                item.Content = $"[{section.Name}] {section.Label}" + (written ? Loc.F("Sf.060", count) : Loc.T("Sf.061"));
            }
            _isUpdatingCombo = false;

            ShowSelectedSection(lines, folder);
            ShowWarnings(lines, folder);
            PanelLegacy.Visibility = IsLegacyText(_editor.Text) ? Visibility.Visible : Visibility.Collapsed;

            TxtSoundNotice.Text = folder == null ? Loc.T("Sf.062") : string.Empty;
            TxtSoundNotice.Visibility = folder == null && _editor.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // セクションに書かれている「キー = パス」（同じキーは最後の行。番号のセクションは番号の小さい順で、整数のキーだけ）
        // キーは空白を除いて比べる（泉北 7000 のサンプルは「Open Left」のように空白を入れて書いている）
        private static List<(string Key, string Path)> EntriesOf(List<BveTextLine> lines, SoundSection section)
        {
            return [.. WrittenEntriesOf(lines, section).Select(entry => (entry.Key, entry.Path))];
        }

        // EntriesOf と同じものに、書かれているとおりのキー（書き換えるときに使う）を付けたもの
        private static List<(string Key, string WrittenKey, string Path)> WrittenEntriesOf(List<BveTextLine> lines, SoundSection section)
        {
            Dictionary<string, (string WrittenKey, string Path)> found = new(StringComparer.OrdinalIgnoreCase);
            foreach (BveTextLine line in lines.Where(line => line.Kind == BveLineKind.KeyValue && IsSameSection(line.Section, section.Name)))
            {
                found[NormalizeKey(line.Key!)] = (line.Key!, line.Value);
            }

            if (!section.IsNumbered) return [.. section.Keys.Where(key => found.ContainsKey(key.Key)).Select(key => (key.Key, found[key.Key].WrittenKey, found[key.Key].Path))];
            return [.. found.Where(pair => int.TryParse(pair.Key, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                .OrderBy(pair => int.Parse(pair.Key, CultureInfo.InvariantCulture))
                .Select(pair => (pair.Key, pair.Value.WrittenKey, pair.Value.Path))];
        }

        // 比べるためのキー（空白を除く）
        private static string NormalizeKey(string key) => string.Concat(key.Where(c => !char.IsWhiteSpace(c)));

        // 古い名前（別名）→ 公式の名前。BVE4 のころの名前と思われ、サンプルの 3.0 形式のサウンドで [Switch] 33・[Suspension] 18・[Rolling] 1 件使われている
        // （BVE で動いている南海 3000 系も使う。組み合わせはユーザーの Plan.md のメモ「joint / Switch」「AirSpring / Suspension」「Run / Rolling」）
        private static readonly Dictionary<string, string> SectionAliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Switch"] = "Joint",
            ["Suspension"] = "AirSpring",
            ["Rolling"] = "Run",
        };

        // 比べるためのセクション名（空白を除き、別名は公式の名前にする）
        private static string NormalizeSection(string name)
        {
            string normalized = NormalizeKey(name);
            return SectionAliases.TryGetValue(normalized, out string? official) ? official : normalized;
        }

        // 同じセクションか（空白を除き、別名を公式の名前にして、大文字・小文字を区別せずに比べる。[Pilot Lamp] と [PilotLamp]、[Switch] と [Joint] は同じ）
        // 公式の英語版のページは「Pilot Lamp」「Brake Handle」「Master Controller」と空白入りで書き、サンプルの 132 個中 106 個のサウンドもそう書いている
        private static bool IsSameSection(string? written, string name) => written != null && string.Equals(NormalizeSection(written), NormalizeSection(name), StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// 公式のセクションの名前と説明の一覧（右クリックメニューの「定型文を入れる」EditorTemplates で使う）
        /// </summary>
        internal static IEnumerable<(string Name, string Label)> SectionNames => Sections.Select(section => (section.Name, section.Label));

        /// <summary>
        /// そのセクションが書かれているか（空白入りの名前・古い名前も同じとみなす。EditorTemplates で使う）
        /// </summary>
        internal static bool IsSectionWritten(List<BveTextLine> lines, string name) => HasSection(lines, name);

        // そのセクションが書かれているか（空白入りの名前も同じとみなす）
        private static bool HasSection(List<BveTextLine> lines, string name) => lines.Any(line => line.Kind == BveLineKind.Section && IsSameSection(line.Section, name));

        // ファイルに書かれているとおりのセクション名（[Pilot Lamp] など）。書かれていなければ公式の名前。書き込むときに使う（別の名前で 2 つ目のセクションを足さないため）
        private static string WrittenSectionName(List<BveTextLine> lines, string name) => lines.FirstOrDefault(line => line.Kind == BveLineKind.Section && IsSameSection(line.Section, name))?.Section ?? name;

        // 選んでいるセクションの行を作り直す（名前のキーはキーごと、番号は書かれている番号ごと）
        private void ShowSelectedSection(List<BveTextLine> lines, string? folder)
        {
            if (CmbSection.SelectedItem is not ComboBoxItem { Tag: SoundSection section }) return;

            GroupKeys.Header = $"[{section.Name}] {section.Label}";
            GridKeys.Children.Clear();
            GridKeys.RowDefinitions.Clear();
            if (GridKeys.ColumnDefinitions.Count == 0)
            {
                GridKeys.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "SoundKey" });
                GridKeys.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                GridKeys.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                GridKeys.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                GridKeys.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            }

            List<(string Key, string Path)> entries = EntriesOf(lines, section);
            string text = _editor?.Text ?? string.Empty;
            // 古い形式のあいだは、参照・消す・番号を足すを使えなくする（▶ は使える）
            bool isLegacy = IsLegacyText(text);
            decimal? version = ReadVersion(text);
            PanelAddNumber.Visibility = section.IsNumbered ? Visibility.Visible : Visibility.Collapsed;
            if (section.IsNumbered)
            {
                TxtSectionNote.Text = entries.Count == 0
                    ? Loc.T("Sf.063")
                    : Loc.F("Sf.064", (section.Name == "Ats" ? Loc.F("Sf2.001", MaxAtsNumber) : string.Empty));
                TxtSectionNote.Visibility = Visibility.Visible;
                foreach ((string key, string path) in entries) AddRow(section, key, Loc.F("Sf.065", key), key, path, folder, isLegacy, null);
                TxtNewNumber.Text = NextNumber(section, entries).ToString(CultureInfo.InvariantCulture);
                BtnAddNumber.IsEnabled = folder != null && !isLegacy;
                return;
            }

            TxtSectionNote.Text = HasSection(lines, section.Name) ? string.Empty : Loc.T("Sf.066");
            // 書かれているセクションでは説明の行を隠す（空の行がすき間にならないように）
            TxtSectionNote.Visibility = TxtSectionNote.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            foreach ((string key, string label) in section.Keys)
            {
                string? path = entries.FirstOrDefault(entry => string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase)).Path;
                AddRow(section, key, key, label, path, folder, isLegacy, UnavailableNote(section.Name, key, version));
            }
        }

        // 1 行（名前・パスの欄・再生・参照・消すボタン）を足す
        // isLegacy：古い形式なので参照・消すを使えなくする。unavailableNote：ヘッダーのバージョンで使えないキーの説明（行を灰色にして参照を使えなくする。使えるなら null）
        private void AddRow(SoundSection section, string key, string name, string description, string? path, string? folder, bool isLegacy, string? unavailableNote)
        {
            int row = GridKeys.RowDefinitions.Count;
            GridKeys.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            TextBlock label = new() { Text = name, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 8, 2), ToolTip = $"{key}：{description}" };
            // パスは下のテキストか「参照」で変える（この欄では書き換えない）。何の音かは欄の中に薄く出す
            TextBox box = new() { IsReadOnly = true, Margin = new Thickness(0, 2, 0, 2), Text = path ?? string.Empty };
            string? problem = path == null || folder == null ? null : FindProblem(folder, path);
            box.ToolTip = path == null ? Loc.F("Sf.067", description) : problem ?? FullPathOf(folder, path) ?? path;
            if (problem != null) box.SetResourceReference(ForegroundProperty, "SystemFillColorCautionBrush");
            if (path == null)
            {
                box.Text = unavailableNote == null ? description : $"{description}　※ {unavailableNote}";
                box.SetResourceReference(ForegroundProperty, "TextFillColorTertiaryBrush");
            }
            // ヘッダーのバージョンで使えないキーは、名前も灰色にする
            if (unavailableNote != null)
            {
                label.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorDisabledBrush");
                label.ToolTip = $"{key}：{description}　※ {unavailableNote}";
            }
            // 鳴らせるのは、書かれていてファイルが見つかる行だけ
            string? fullPath = path == null || problem != null ? null : FullPathOf(folder, path);
            // 行を作り直しても、鳴らしている行は ■ のまま（ShowPlayMark）
            // ▶ と ■ で幅が変わって右のボタンが動かないように、幅をそろえる
            Button play = new() { Content = PlayMark, Margin = new Thickness(6, 2, 0, 2), Padding = new Thickness(8, 4, 8, 5), MinWidth = 36, Tag = fullPath, IsEnabled = fullPath != null };
            ShowPlayMark(play);
            play.Click += PlayButton_Click;
            Button browse = new() { Content = Loc.T("Sf.068"), Margin = new Thickness(4, 2, 0, 2), Padding = new Thickness(8, 4, 8, 5), Tag = (section.Name, key), IsEnabled = folder != null && !isLegacy && unavailableNote == null };
            browse.Click += BrowseButton_Click;
            // 消すのは書かれている行だけ（セクションの行は残す）。使えないキーがもう書かれていたら消せる
            Button remove = new() { Content = Loc.T("Sf.069"), Margin = new Thickness(4, 2, 0, 2), Padding = new Thickness(8, 4, 8, 5), Tag = (section.Name, key), IsEnabled = path != null && !isLegacy,
                ToolTip = Loc.T("Sf.070") };
            remove.Click += RemoveButton_Click;

            Grid.SetRow(label, row);
            Grid.SetRow(box, row);
            Grid.SetColumn(box, 1);
            Grid.SetRow(play, row);
            Grid.SetColumn(play, 2);
            Grid.SetRow(browse, row);
            Grid.SetColumn(browse, 3);
            Grid.SetRow(remove, row);
            Grid.SetColumn(remove, 4);
            GridKeys.Children.Add(label);
            GridKeys.Children.Add(box);
            GridKeys.Children.Add(play);
            GridKeys.Children.Add(browse);
            GridKeys.Children.Add(remove);
        }

        // 足す番号の最初の値：書かれている番号のいちばん大きい番号の次（なければ 0）。ATS で 255 を超えるときは、空いている小さい番号
        private static int NextNumber(SoundSection section, List<(string Key, string Path)> entries)
        {
            HashSet<int> used = [.. entries.Select(entry => int.Parse(entry.Key, CultureInfo.InvariantCulture))];
            int next = used.Count == 0 ? 0 : used.Max() + 1;
            if (section.Name != "Ats" || next <= MaxAtsNumber) return next;
            return Enumerable.Range(0, MaxAtsNumber + 1).FirstOrDefault(number => !used.Contains(number));
        }

        // 公式の一覧にないセクション・キー（FindListProblems）と、見つからない・使えないパスを、どのセクションの分もまとめて警告する
        private void ShowWarnings(List<BveTextLine> lines, string? folder)
        {
            List<string> warnings = [.. FindListProblems(lines, _editor?.Text ?? string.Empty)];
            if (folder != null)
            {
                foreach (SoundSection section in Sections)
                {
                    foreach ((string key, string path) in EntriesOf(lines, section))
                    {
                        string? problem = FindProblem(folder, path);
                        if (problem != null) warnings.Add($"[{section.Name}] {key} = {path} … {problem}");
                    }
                }
            }

            List<string> shown = [.. warnings.Take(MaxWarningLines).Select(warning => "⚠ " + warning)];
            if (warnings.Count > MaxWarningLines) shown.Add(Loc.F("Sf.071", warnings.Count - MaxWarningLines));
            TxtSoundWarnings.Text = string.Join("\n", shown);
            TxtSoundWarnings.Visibility = warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// 公式の一覧にないセクション・キー、番号のセクションの整数でないキー・ATS の 256 以上を探す（行番号の順）
        /// 古い形式（1 行目が「Version 1.0」）は書き方が違うので調べない
        /// </summary>
        internal static List<string> FindListProblems(List<BveTextLine> lines, string text)
        {
            List<string> problems = [];
            if (IsLegacyText(text)) return problems;
            decimal? version = ReadVersion(text);

            HashSet<string> reported = new(StringComparer.OrdinalIgnoreCase);
            // 一覧のセクションごとに、最初に書かれていた名前と行（空白の入れ方が違う同じセクションが 2 つあれば知らせる）
            Dictionary<string, BveTextLine> firstSectionLines = new(StringComparer.OrdinalIgnoreCase);
            foreach (BveTextLine line in lines)
            {
                if (line.Section == null) continue;
                // 空白を除いて比べる（[Pilot Lamp]・[Brake Handle]・[Master Controller] も一覧のセクションとして扱う）
                SoundSection? section = Sections.FirstOrDefault(item => IsSameSection(line.Section, item.Name));
                if (section == null)
                {
                    // 一覧にないセクションは 1 回だけ知らせる
                    if (line.Kind != BveLineKind.Section || !reported.Add(line.Section)) continue;
                    problems.Add(Loc.F("Sf.072", line.LineNumber, line.Section));
                    continue;
                }
                if (line.Kind == BveLineKind.Section)
                {
                    // [PilotLamp] と [Pilot Lamp]、[Joint] と [Switch] のように、書き方の違う同じセクションが 2 つ
                    if (firstSectionLines.TryGetValue(section.Name, out BveTextLine? first) && !string.Equals(first.Section, line.Section, StringComparison.OrdinalIgnoreCase))
                        problems.Add(Loc.F("Sf.073", line.LineNumber, line.Section, first.LineNumber, first.Section));
                    firstSectionLines.TryAdd(section.Name, line);
                    continue;
                }
                if (line.Kind != BveLineKind.KeyValue) continue;

                string key = NormalizeKey(line.Key!);
                if (!section.IsNumbered)
                {
                    if (!section.Keys.Any(item => string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase)))
                        problems.Add(Loc.F("Sf.074", line.LineNumber, section.Name, line.Key, string.Join(Loc.T("Sf2.002"), section.Keys.Select(item => item.Key))));
                    // ヘッダーのバージョンではまだ使えないキー
                    string? unavailable = UnavailableNote(section.Name, key, version);
                    if (unavailable != null) problems.Add(Loc.F("Sf.075", line.LineNumber, section.Name, line.Key, unavailable));
                    continue;
                }
                if (!int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out int number))
                    problems.Add(Loc.F("Sf.076", line.LineNumber, section.Name, line.Key));
                else if (section.Name == "Ats" && number > MaxAtsNumber)
                    problems.Add(Loc.F("Sf.077", line.LineNumber, line.Key, MaxAtsNumber));
            }
            return problems;
        }

        /// <summary>
        /// 古い形式（1 行目が「Version 1.0」）か
        /// </summary>
        internal static bool IsLegacyText(string text) => text.TrimStart('﻿').TrimStart().StartsWith("Version 1.0", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// 1 行目の「BveTs Vehicle Sound 3.00」などからバージョンを読む（大文字・小文字は区別しない。読めなければ null）
        /// </summary>
        internal static decimal? ReadVersion(string text)
        {
            string firstLine = text.TrimStart('﻿').Split('\n')[0];
            Match match = Regex.Match(firstLine, @"^\s*BveTs\s+Vehicle\s+Sound\s+(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
            if (!match.Success) return null;
            return decimal.TryParse(match.Groups[1].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal version) ? version : null;
        }

        /// <summary>
        /// ヘッダーのバージョンでは使えないキーなら説明（「BveTs Vehicle Sound 3.01 から使えます（今のヘッダーは 3.00）」）。使えるか、バージョンを読めなければ null
        /// </summary>
        internal static string? UnavailableNote(string section, string key, decimal? version)
        {
            if (version == null) return null;
            foreach ((string keySection, string keyName, decimal minVersion) in KeyMinVersions)
            {
                if (!string.Equals(keySection, section, StringComparison.OrdinalIgnoreCase) || !string.Equals(keyName, key, StringComparison.OrdinalIgnoreCase)) continue;
                // decimal は書かれた桁のまま文字にできる（3.00 は 3.00、2.0 は 2.0）
                return version < minVersion
                    ? Loc.F("Sf.078", minVersion.ToString("0.00", CultureInfo.InvariantCulture), version.Value.ToString(CultureInfo.InvariantCulture))
                    : null;
            }
            return null;
        }

        /// <summary>
        /// 古い形式を書き直したテキスト：1 行目だけをヘッダーに置き換える（中身は変えない。Plan.md のユーザーのメモ「中身は変えなくても動く」）
        /// </summary>
        internal static string BuildUpgradedText(string text, string header)
        {
            bool hasBom = text.StartsWith('﻿');
            string body = hasBom ? text[1..] : text;
            int end = body.IndexOf('\n');
            string rest = end < 0 ? string.Empty : body[end..];
            // 1 行目の終わりの \r は rest の頭の \n の前にあるので残す
            string carriageReturn = end > 0 && body[end - 1] == '\r' ? "\r" : string.Empty;
            return (hasBom ? "﻿" : string.Empty) + header + carriageReturn + rest;
        }

        // 書き直す：確認してから 1 行目を BveTs Vehicle Sound 3.01 にする（Shift_JIS のファイルなら :shift_jis を付ける。付けないと BVE が UTF-8 として読んで文字化けする）
        private void BtnUpgradeSound_Click(object sender, RoutedEventArgs e)
        {
            if (_editor == null || !IsLegacyText(_editor.Text)) return;

            Encoding? encoding = _getSoundEncoding?.Invoke();
            string header = UpgradedHeader + (encoding?.CodePage == 932 ? ":shift_jis" : string.Empty);
            string encodingNote = header.EndsWith(":shift_jis") ? Loc.T("Sf.079") : string.Empty;
            MessageBoxResult answer = MessageBox.Show(
                Loc.F("Sf.080", header, encodingNote),
                Loc.T("Sf.081"), MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;

            EditorTextHelper.ReplaceChangedText(_editor, BuildUpgradedText(_editor.Text, header));
            OperationHistory.Add($"サウンド: 古い形式を書き直す（{header}）");
        }

        private void CmbSection_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingCombo || _editor == null) return;
            ShowSelectedSection(BveTextParser.Parse(_editor.Text), SoundFolder());
        }

        // サウンドのファイルがあるフォルダー（相対パスの基準）。読み込めていなければ null
        private string? SoundFolder()
        {
            string? path = _getSoundPath?.Invoke();
            return string.IsNullOrEmpty(path) ? null : Path.GetDirectoryName(path);
        }

        // パスに問題があれば理由（使えない文字・見つからないなど）。問題なければ null
        private static string? FindProblem(string folder, string path)
        {
            string? pathProblem = VehicleFileParser.FindPathProblem(path, folder);
            if (pathProblem != null) return pathProblem;

            string? fullPath = FullPathOf(folder, path);
            return fullPath != null && File.Exists(fullPath) ? null : Loc.T("Sf.082");
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

        // 参照ボタン：音のファイルを選んで、Sound.txt からの相対パスを下のテキストに書き込む（行やセクションがなければ足す。Ctrl+Z で戻せる）
        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            if (_editor == null || sender is not Button { Tag: (string section, string key) }) return;
            string? folder = SoundFolder();
            if (folder == null) return;

            List<BveTextLine> lines = BveTextParser.Parse(_editor.Text);
            var written = WrittenEntriesOf(lines, Sections.First(item => item.Name == section))
                .FirstOrDefault(entry => string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase));
            // すでに書かれていれば、書かれているとおりのキー（「Open Left」など）の行を書き換える
            string writeKey = written.WrittenKey ?? key;
            string? relativePath = ChooseSoundFile(folder, written.Path, Loc.F("Sf.083", section, key));
            if (relativePath == null) return;

            // セクションも、書かれているとおりの名前（[Pilot Lamp] など）の中に書く
            string writeSection = WrittenSectionName(lines, section);
            EditorTextHelper.ReplaceChangedText(_editor, BveTextParser.SetValue(_editor.Text, writeSection, writeKey, relativePath));
            OperationHistory.Add($"サウンド入力: [{writeSection}] {writeKey} = {relativePath}");
        }

        // 再生ボタン：その行の音を鳴らす（同じ音を鳴らしている途中なら止める）
        private void PlayButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string fullPath }) return;
            bool isSame = _playingPath != null && string.Equals(_playingPath, fullPath, StringComparison.OrdinalIgnoreCase);
            StopSound();
            if (isSame) return;

            // MediaPlayer は、音を出せないときに失敗を知らせるまで 4 秒ほどかかる（ユーザーが気づいた）ので、先に調べてすぐ知らせる
            string? problem = FindPlayProblem(fullPath);
            if (problem != null)
            {
                OperationHistory.Add($"サウンド: 鳴らす前に止めた {fullPath}（{problem}）");
                MessageBox.Show(Loc.F("Sf.084", problem, fullPath), Loc.T("Sf.085"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SetPlayingPath(fullPath);
            _player.Open(new Uri(fullPath, UriKind.Absolute));
            _player.Play();
            OperationHistory.Add($"サウンド: 鳴らす {fullPath}");
        }

        // 再生ボタンの印（止まっているとき ▶、鳴らしているとき ■）
        private const string PlayMark = "▶";
        private const string StopMark = "■";

        // 鳴らしているファイルを覚え、行の再生ボタンの印を合わせる（鳴らしている行だけ ■。ユーザーの要望）
        private void SetPlayingPath(string? path)
        {
            _playingPath = path;
            foreach (Button button in GridKeys.Children.OfType<Button>().Where(button => button.Content is PlayMark or StopMark)) ShowPlayMark(button);
        }

        // 1 つの再生ボタンの印と説明を、今鳴らしているかに合わせる
        private void ShowPlayMark(Button button)
        {
            bool isPlaying = _playingPath != null && button.Tag is string fullPath && string.Equals(fullPath, _playingPath, StringComparison.OrdinalIgnoreCase);
            button.Content = isPlaying ? StopMark : PlayMark;
            button.ToolTip = isPlaying ? Loc.T("Sf.086") : Loc.T("Sf.087");
        }

        // 鳴らしている音を止める
        private void StopSound()
        {
            if (_playingPath == null) return;
            _player.Stop();
            _player.Close();
            SetPlayingPath(null);
        }

        // 鳴らせなかったとき。MediaPlayer の理由では「PC で音が出せない」と「ファイルが壊れている」を見分けられないので、
        // ファイルの先頭が wav の形かを調べて文を分ける（ユーザーが、Windows Audio が止まっている・音の機器がないときに「壊れている」と出るのに気づいた）
        // 英語の理由は操作履歴だけに残す
        private void Player_MediaFailed(object? sender, ExceptionEventArgs e)
        {
            string path = _playingPath ?? string.Empty;
            OperationHistory.Add($"サウンド: 鳴らせない {path}（{e.ErrorException?.GetType().Name}: 0x{e.ErrorException?.HResult:X8} {e.ErrorException?.Message}）");
            SetPlayingPath(null);

            string reason = DescribePlayFailure(path);
            MessageBox.Show(Loc.F("Sf.088", reason, path), Loc.T("Sf.089"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        /// <summary>
        /// 鳴らす前に調べる：wav の形でない・読めない・音を出せない（サービスが止まっている・機器がない。AudioDeviceCheck）、なら理由の文。鳴らしてよければ null
        /// </summary>
        internal static string? FindPlayProblem(string path)
        {
            bool? isWave = IsWaveFile(path);
            if (isWave != true) return DescribePlayFailure(path);
            AudioDeviceCheck.AudioProblem problem = AudioDeviceCheck.Check();
            return problem == AudioDeviceCheck.AudioProblem.None ? null : AudioDeviceCheck.Describe(problem);
        }

        /// <summary>
        /// 鳴らせなかった理由の文（ファイルの先頭が wav の形「RIFF ○○○○ WAVE」かで分ける。wav の形なら、音を出せない理由を AudioDeviceCheck で分ける）
        /// </summary>
        internal static string DescribePlayFailure(string path)
        {
            bool? isWave = IsWaveFile(path);
            if (isWave == true)
            {
                AudioDeviceCheck.AudioProblem problem = AudioDeviceCheck.Check();
                // 機器はあるのに鳴らせなかったときは、理由を分けられない
                if (problem == AudioDeviceCheck.AudioProblem.None) problem = AudioDeviceCheck.AudioProblem.Unknown;
                return Loc.T("Sf.090") + AudioDeviceCheck.Describe(problem);
            }
            if (isWave == false)
                return Loc.T("Sf.091");
            return Loc.T("Sf.092");
        }

        // ファイルの先頭 12 バイトが「RIFF」＋大きさ＋「WAVE」か（読めなければ null）
        private static bool? IsWaveFile(string path)
        {
            try
            {
                using FileStream stream = File.OpenRead(path);
                byte[] head = new byte[12];
                if (stream.Read(head, 0, head.Length) < head.Length) return false;
                return head[0] == 'R' && head[1] == 'I' && head[2] == 'F' && head[3] == 'F'
                    && head[8] == 'W' && head[9] == 'A' && head[10] == 'V' && head[11] == 'E';
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return null;
            }
        }

        // 音のファイルを選ぶダイアログを出し、Sound.txt からの相対パスを返す（やめた・書けないパスなら null。書けないときは理由を出す）
        private static string? ChooseSoundFile(string folder, string? currentPath, string title)
        {
            string? currentFull = currentPath == null ? null : FullPathOf(folder, currentPath);
            string? initialFolder = currentFull == null ? null : Path.GetDirectoryName(currentFull);
            OpenFileDialog dialog = new()
            {
                Filter = Loc.T("Sf.093"),
                Title = title,
                InitialDirectory = initialFolder != null && Directory.Exists(initialFolder) ? initialFolder : folder,
                CheckFileExists = true,
            };
            if (dialog.ShowDialog() != true) return null;

            string relativePath = Path.GetRelativePath(folder, dialog.FileName);
            string? problem = VehicleFileParser.FindPathProblem(relativePath, folder);
            if (problem == null) return relativePath;

            MessageBox.Show(Loc.F("Sf.094", relativePath, problem), Loc.T("Sf.095"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }

        /// <summary>
        /// 番号のセクションに足す番号を調べる。問題があれば日本語の理由、なければ null
        /// </summary>
        /// <param name="sectionName">セクション名（Ats なら 0～255）</param>
        /// <param name="text">入れた番号の文字</param>
        /// <param name="used">書かれている番号</param>
        internal static string? CheckNewNumber(string sectionName, string text, IEnumerable<string> used)
        {
            if (!int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int number)) return Loc.F("Sf.096", text);
            if (sectionName == "Ats" && number > MaxAtsNumber) return Loc.F("Sf.097", MaxAtsNumber, number);
            if (used.Any(key => int.Parse(key, CultureInfo.InvariantCulture) == number)) return Loc.F("Sf.098", number);
            return null;
        }

        // 番号を足す：番号を調べて、音のファイルを選び、「番号 = パス」の行を足す（セクションがなければセクションごと足す。Ctrl+Z で戻せる）
        private void BtnAddNumber_Click(object sender, RoutedEventArgs e)
        {
            if (_editor == null || CmbSection.SelectedItem is not ComboBoxItem { Tag: SoundSection { IsNumbered: true } section }) return;
            string? folder = SoundFolder();
            if (folder == null) return;

            List<BveTextLine> lines = BveTextParser.Parse(_editor.Text);
            List<(string Key, string Path)> entries = EntriesOf(lines, section);
            string? problem = CheckNewNumber(section.Name, TxtNewNumber.Text, entries.Select(entry => entry.Key));
            if (problem != null)
            {
                MessageBox.Show(problem, Loc.T("Sf.099"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string number = int.Parse(TxtNewNumber.Text.Trim(), CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
            // 最初のフォルダーは、いちばん最後に書かれている番号の音があるフォルダー
            string? relativePath = ChooseSoundFile(folder, entries.Count > 0 ? entries[^1].Path : null, Loc.F("Sf.100", section.Name, number));
            if (relativePath == null) return;

            // セクションも、書かれているとおりの名前の中に書く（番号のセクションは今は空白のない名前だけだが、そろえておく）
            EditorTextHelper.ReplaceChangedText(_editor, BveTextParser.SetValue(_editor.Text, WrittenSectionName(lines, section.Name), number, relativePath));
            OperationHistory.Add($"サウンド入力: [{section.Name}] {number} = {relativePath}（番号を足す）");
        }

        // 消す：その行（同じキーが何行もあれば全部）を、確認してから消す（セクションの行は残す。Ctrl+Z で戻せる）
        private void RemoveButton_Click(object sender, RoutedEventArgs e)
        {
            if (_editor == null || sender is not Button { Tag: (string section, string key) }) return;

            List<BveTextLine> targets = FindKeyLines(BveTextParser.Parse(_editor.Text), section, key);
            if (targets.Count == 0) return;

            string shown = string.Join("\n", targets.Select(line => Loc.F("Sf.101", line.LineNumber, line.Key, line.Value)));
            MessageBoxResult answer = MessageBox.Show(Loc.F("Sf.102", section, shown),
                Loc.T("Sf.103"), MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;

            EditorTextHelper.ReplaceChangedText(_editor, BveTextParser.RemoveLines(_editor.Text, targets.Select(line => line.LineNumber)));
            OperationHistory.Add($"サウンド入力: [{section}] {key} の行を消す（{targets.Count} 行）");
        }

        /// <summary>
        /// セクションの中で、そのキー（空白を除いて、大文字・小文字を区別せずに比べる）の「キー = 値」の行を全部探す
        /// セクション名も空白を除いて比べる（[Pilot Lamp] も [PilotLamp] の中として探す）
        /// </summary>
        internal static List<BveTextLine> FindKeyLines(List<BveTextLine> lines, string section, string key)
        {
            string normalized = NormalizeKey(key);
            return [.. lines.Where(line => line.Kind == BveLineKind.KeyValue
                && IsSameSection(line.Section, section)
                && string.Equals(NormalizeKey(line.Key!), normalized, StringComparison.OrdinalIgnoreCase))];
        }
    }
}
