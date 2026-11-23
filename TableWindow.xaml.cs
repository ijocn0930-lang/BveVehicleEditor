using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;

namespace BveVehicleEditor
{
    /// <summary>
    /// 表のファイル（引張力・電流・進段パラメーターなどの CSV）を表の形で見る・直す・保存するウィンドウ
    /// 1 行目のヘッダーは表に入れず上に出し、「#」で始まるコメントの行は灰色で元の位置に出す
    /// 保存するときは、直していない行・行の後ろの「,」・改行の形を元のまま残す
    /// 行・列を足す・消すことができ、数字でないセルがあれば警告する
    /// </summary>
    public partial class TableWindow : Window
    {
        /// <summary>
        /// 表の 1 行（ファイルの 1 行）
        /// </summary>
        public sealed class TableLine : INotifyPropertyChanged
        {
            public event PropertyChangedEventHandler? PropertyChanged;

            private string _description = string.Empty;

            /// <summary>「単位・説明」の列に出す文字（進段パラメーターだけ。項目名を直すと出し直す）</summary>
            public string Description
            {
                get => _description;
                set
                {
                    if (_description == value) return;
                    _description = value;
                    PropertyChanged?.Invoke(this, new(nameof(Description)));
                }
            }

            /// <summary>ファイルの何行目か（ヘッダーが 1 行目。行を足す・消すと付け直す）</summary>
            public int LineNumber { get; set; }

            /// <summary>「,」で分けた中身。表の列の数にそろえて、足りない分は空にしてある（表で直すとここが変わる）</summary>
            public string[] Cells { get; set; }

            /// <summary>ファイルに書かれていた「,」で分けた数（保存するとき、後ろの空のセルをここまでは残す。足した行は 0）</summary>
            public int OriginalCellCount { get; set; }

            /// <summary>「#」で始まるコメントの行か（読み込んだときに決める。足した行は false）</summary>
            public bool IsComment { get; }

            public TableLine(int lineNumber, string[] cells, int originalCellCount, bool isComment)
            {
                LineNumber = lineNumber;
                Cells = cells;
                OriginalCellCount = originalCellCount;
                IsComment = isComment;
            }
        }

        // 数字でないセルの警告に、場所をいくつまで書くか（多すぎると読めないので、残りは「ほか n か所」）
        private const int MaxListedProblems = 5;

        // 表のファイル（ヘッダーの確認・文字コードの見分け・保存は FileItem に任せる）
        private readonly FileItem _file;

        // ウィンドウの名前（未保存なら最後に「 *」を付ける）
        private readonly string _baseTitle;

        // 1 行目のヘッダー（表に入れない。保存するときはそのまま書く）
        private readonly string _headerLine;

        // 元のファイルの改行（\r\n か \n）と、最後に改行があったか（保存するときに合わせる）
        private readonly string _newLine;
        private readonly bool _endsWithNewLine;

        // 1 列目の名前（速度 (km/h) や 項目）と、進段パラメーターの表か（1 列目が項目名。「単位・説明」の列を出し、項目名を調べる）
        private readonly string _firstColumnName;
        private readonly bool _isParamsTable;

        // グラフの縦の軸の名前（例：引張力 [N]。グラフを出さない表では null）
        private readonly string? _valueName;

        // ヘッダーのバージョン（読めなければ null）
        private readonly decimal? _version;

        // セルの列（番号 = セルの番号。左の「行」と「単位・説明」の列は入れない）
        private readonly List<DataGridColumn> _cellColumns = [];

        // 表の行（2 行目から下）と、列の数（1 列目とノッチの列。左の「行」は数えない）
        private readonly List<TableLine> _rows;
        private int _columnCount;

        /// <summary>
        /// 表のファイルを読み込んでウィンドウを作る。読めない・ヘッダーが違うときは例外（IOException・InvalidDataException など）
        /// </summary>
        /// <param name="fullPath">表のファイルのフルパス</param>
        /// <param name="title">ウィンドウの名前（例：[Power] 引張力（空車））</param>
        /// <param name="expectedHeader">1 行目のヘッダー（例：BveTs Vehicle Performance Table）</param>
        /// <param name="firstColumnName">1 列目の名前（例：速度 (km/h)）</param>
        /// <param name="isParamsTable">進段パラメーターの表か（1 列目は項目名なので数字か調べず、「単位・説明」の列と項目名のチェックを出す）</param>
        /// <param name="valueName">グラフの縦の軸の名前（例：引張力 [N]）。null ならグラフのタブを出さない</param>
        /// <param name="columnLabel">2 列目からの列の名前（例：ノッチ・音）</param>
        /// <param name="firstColumnNumber">2 列目の番号（ノッチは 1、モーター音は Motor0.wav に合わせて 0）</param>
        public TableWindow(string fullPath, string title, string expectedHeader, string firstColumnName, bool isParamsTable = false, string? valueName = null,
            string? columnLabel = null, int firstColumnNumber = 1)
        {
            InitializeComponent();
            // 列の名前（渡されなければ「ノッチ」。言語に合わせる）
            _columnLabel = columnLabel ?? Loc.T("Tab.Notch");
            _firstColumnNumber = firstColumnNumber;
            BtnAddColumn.Content = Loc.F("Tw.002", columnLabel);

            _file = new(Path.GetFileName(fullPath), expectedHeader);
            _file.Load(fullPath);
            if (!_file.IsLoaded) throw new FileNotFoundException($"ファイルが見つかりません。\n{fullPath}", fullPath);

            _baseTitle = $"{title} - {_file.FileName}";
            Title = _baseTitle;
            TxtFilePath.Text = fullPath;
            TxtFilePath.ToolTip = fullPath;
            _firstColumnName = firstColumnName;
            _isParamsTable = isParamsTable;
            _valueName = valueName;
            // グラフにできない表（進段パラメーターなど）では、グラフのタブを隠す
            if (valueName == null || isParamsTable) TabGraph.Visibility = Visibility.Collapsed;

            string text = _file.TextContent;
            _newLine = text.Contains("\r\n") ? "\r\n" : "\n";
            _endsWithNewLine = text.EndsWith('\n');
            string[] lines = SplitLines(text);
            _headerLine = lines.Length == 0 ? string.Empty : lines[0];
            _version = PerformanceParams.ReadVersion(_headerLine);

            _columnCount = Math.Max(1, lines.Skip(1).Select(line => line.Split(',').Length).DefaultIfEmpty(1).Max());
            _rows = BuildRows(lines, _columnCount);
            BuildColumns();
            GridTable.ItemsSource = _rows;
            InitializeEditing();
            UpdateInfoAndWarnings();
        }

        /// <summary>
        /// 直したところがあって、まだ保存していないか
        /// </summary>
        public bool IsModified => _file.IsModified;

        /// <summary>
        /// 今の表からファイルの中身を作る。直していない行は元と同じ文字になる
        /// </summary>
        public string BuildText()
        {
            IEnumerable<string> lines = _rows.Select(BuildLine).Prepend(_headerLine);
            string text = string.Join(_newLine, lines);
            return _endsWithNewLine ? text + _newLine : text;
        }

        /// <summary>
        /// 今の表をファイルに保存する（メッセージは出さない）。保存できなければ理由を返す。保存できたか、変更がなければ null
        /// </summary>
        public string? SaveFile()
        {
            _file.TextContent = BuildText();
            string? unencodable = FileItem.FindUnencodableText(_file.TextContent, _file.DetectedEncoding);
            if (unencodable != null) return Loc.F("Tw.004", _file.EncodingDisplayName, unencodable);

            try
            {
                _file.Save();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                OperationHistory.Add($"表のファイルを保存できない: {ex.GetType().Name}: {ex.Message}");
                return ErrorText.Describe(ex);
            }

            OperationHistory.Add($"表のファイルを保存: {_file.FullPath}");
            UpdateModified();
            return null;
        }

        /// <summary>
        /// 数字でないセルを探す（コメントの行・空のセルは調べない。1 列目は数字の表のときだけ調べる）
        /// 見つかったセルを「5 行目のノッチ 2「abc」」の形で返す
        /// </summary>
        public List<string> FindNonNumberCells()
        {
            List<string> problems = [];
            foreach (TableLine row in _rows.Where(row => !row.IsComment))
            {
                for (int i = _isParamsTable ? 1 : 0; i < row.Cells.Length; i++)
                {
                    string cell = row.Cells[i].Trim();
                    if (cell.Length == 0 || double.TryParse(cell, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) continue;
                    problems.Add(Loc.F("Tw.005", row.LineNumber, ColumnName(i), row.Cells[i]));
                }
            }
            return problems;
        }

        /// <summary>
        /// 値が空の行を探す（コメントの行・全部空の行は調べない。全部空の行は保存すると空の行になるので問題ない）
        /// ・1 列目（速度・進段パラメーターは項目名）だけ書いて、2 列目から全部空の行 → BVE でエラー（Plan.md）
        /// ・1 列目が空なのに、2 列目から値が入っている行
        /// 後ろのノッチだけが空の行（ノッチがない部分）は調べない。途中のノッチが空の行は BVE の動きが分からないので調べない
        /// </summary>
        public (List<string> NoValues, List<string> NoFirst) FindEmptyValueRows()
        {
            List<string> noValues = [];
            List<string> noFirst = [];
            foreach (TableLine row in _rows.Where(row => !row.IsComment && row.Cells.Length > 0))
            {
                bool hasFirst = row.Cells[0].Trim().Length > 0;
                bool hasValues = row.Cells.Skip(1).Any(cell => cell.Trim().Length > 0);
                if (hasFirst && !hasValues) noValues.Add(Loc.F("Tw.006", row.LineNumber, _firstColumnName, row.Cells[0].Trim()));
                if (!hasFirst && hasValues) noFirst.Add(Loc.F("Tw.007", row.LineNumber));
            }
            return (noValues, noFirst);
        }

        // 警告に並べる（多ければ MaxListedProblems までにして「ほか ○ 行」）
        private static string ListProblems(List<string> problems)
        {
            string listed = string.Join("、", problems.Take(MaxListedProblems));
            return problems.Count > MaxListedProblems ? Loc.F("Tw.008", listed, problems.Count - MaxListedProblems) : listed;
        }

        // 1 行分の文字を作る。後ろの空のセルは、元のファイルに書かれていた数までは残す（直していない行が元と同じになるように）
        private static string BuildLine(TableLine row)
        {
            int lastFilled = Array.FindLastIndex(row.Cells, cell => !string.IsNullOrEmpty(cell));
            int count = Math.Max(row.OriginalCellCount, lastFilled + 1);
            return string.Join(",", row.Cells.Take(count));
        }

        // 改行で分ける（最後の改行のあとの空の行は入れない）
        private static string[] SplitLines(string text)
        {
            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            return lines.Length > 0 && lines[^1].Length == 0 ? lines[..^1] : lines;
        }

        // 2 行目から下を表の行にする（1 行目はヘッダーなので表に入れない）。列の数にそろえて、足りない分は空にする
        private static List<TableLine> BuildRows(string[] lines, int columnCount)
        {
            List<TableLine> rows = [];
            for (int i = 1; i < lines.Length; i++)
            {
                string[] cells = lines[i].Split(',');
                rows.Add(new(i + 1, PadCells(cells, columnCount), cells.Length, lines[i].TrimStart().StartsWith('#')));
            }
            return rows;
        }

        // 列の数にそろえる（足りない分は空。表で同じ列に並べるため）
        private static string[] PadCells(string[] cells, int count)
        {
            if (cells.Length >= count) return cells;
            string[] padded = new string[count];
            Array.Fill(padded, string.Empty);
            cells.CopyTo(padded, 0);
            return padded;
        }

        // 2 列目からの列の名前（ノッチ・音）と、2 列目の番号（ノッチは 1、モーター音は 0）
        private readonly string _columnLabel;
        private readonly int _firstColumnNumber;

        // 列の名前（0 は 1 列目の名前、1 から「ノッチ n」や「音 n」）
        private string ColumnName(int cellIndex)
        {
            return cellIndex == 0 ? _firstColumnName : $"{_columnLabel} {cellIndex - 1 + _firstColumnNumber}";
        }

        // 列を作り直す：左から「行」（ファイルの何行目か。直せない）、1 列目（速度や項目）、
        // （進段パラメーターなら「単位・説明」。直せない）、ノッチ 1・ノッチ 2 …
        private void BuildColumns()
        {
            GridTable.Columns.Clear();
            _cellColumns.Clear();
            var cellStyle = (Style)GridTable.FindResource("CellTextStyle");
            GridTable.Columns.Add(new DataGridTextColumn { Header = Loc.T("Tw.009"), Binding = new Binding(nameof(TableLine.LineNumber)), ElementStyle = cellStyle, IsReadOnly = true });
            for (int i = 0; i < _columnCount; i++)
            {
                // セルを直し終えたとき（Enter・ほかのセルへ移る）に、表の中身へ書き戻す
                Binding binding = new($"{nameof(TableLine.Cells)}[{i}]") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus };
                DataGridTextColumn column = new() { Header = ColumnName(i), Binding = binding, ElementStyle = cellStyle };
                GridTable.Columns.Add(column);
                _cellColumns.Add(column);
                if (i == 0 && _isParamsTable) GridTable.Columns.Add(BuildDescriptionColumn());
            }
        }

        // 「単位・説明」の列（直せない。長い説明は「…」で切り、吹き出しで全部見せる）
        private DataGridTextColumn BuildDescriptionColumn()
        {
            Style style = new(typeof(TextBlock), (Style)GridTable.FindResource("CellTextStyle"));
            style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
            style.Setters.Add(new Setter(TextBlock.ForegroundProperty, new DynamicResourceExtension("TextFillColorSecondaryBrush")));
            style.Setters.Add(new Setter(ToolTipProperty, new Binding(nameof(TableLine.Description))));
            return new DataGridTextColumn
            {
                Header = Loc.T("Tw.010"),
                Binding = new Binding(nameof(TableLine.Description)) { Mode = BindingMode.OneWay },
                ElementStyle = style,
                IsReadOnly = true,
                MaxWidth = 260,
            };
        }

        // 行の番号（ファイルの何行目か）を付け直す。ヘッダーが 1 行目なので、表の 1 行目は 2
        private void RenumberRows()
        {
            for (int i = 0; i < _rows.Count; i++) _rows[i].LineNumber = i + 2;
        }

        /// <summary>
        /// 表の index 番目（0 から）の行の下に空の行を足す。index が -1 なら最後に足す。足した行を返す（メッセージは出さない）
        /// </summary>
        private TableLine InsertRowAfter(int index)
        {
            return InsertRowAt(index < 0 ? _rows.Count : index + 1);
        }

        /// <summary>
        /// 表の position 番目（0 から。行の数なら最後）に空の行を足す。足した行を返す（メッセージは出さない）
        /// </summary>
        private TableLine InsertRowAt(int position)
        {
            using var step = UndoStep();
            string[] cells = new string[_columnCount];
            Array.Fill(cells, string.Empty);
            TableLine row = new(0, cells, 0, false);
            _rows.Insert(Math.Clamp(position, 0, _rows.Count), row);
            RenumberRows();
            GridTable.Items.Refresh();
            UpdateModified();
            return row;
        }

        /// <summary>
        /// 行を消す（メッセージは出さない）
        /// </summary>
        private void RemoveRows(IReadOnlyCollection<TableLine> rows)
        {
            using var step = UndoStep();
            _rows.RemoveAll(rows.Contains);
            RenumberRows();
            GridTable.Items.Refresh();
            UpdateModified();
        }

        /// <summary>
        /// 右の端に空の列を 1 つ足す（メッセージは出さない）。空のままなら保存しても書かれない
        /// </summary>
        private void AddColumn()
        {
            InsertColumnAt(_columnCount);
        }

        /// <summary>
        /// cellIndex 番目（1 以上。1 列目の左には足さない）に空の列を足し、右の列を右へずらす（メッセージは出さない）
        /// </summary>
        private void InsertColumnAt(int cellIndex)
        {
            using var step = UndoStep();
            cellIndex = Math.Clamp(cellIndex, 1, _columnCount);
            foreach (TableLine row in _rows)
            {
                string[] cells = PadCells(row.Cells, _columnCount);
                row.Cells = [.. cells.Take(cellIndex), string.Empty, .. cells.Skip(cellIndex)];
                // 元のファイルに書かれていた列の間に足したときは、書く数も 1 つ増やす（右の値の位置がずれないように）
                if (row.OriginalCellCount > cellIndex) row.OriginalCellCount++;
            }
            _columnCount++;
            BuildColumns();
            UpdateModified();
        }

        /// <summary>
        /// cellIndex 番目（1 以上。0 は 1 列目なので消さない）の列を消し、右の列を左へ詰める（メッセージは出さない）
        /// </summary>
        private void RemoveColumn(int cellIndex)
        {
            if (cellIndex < 1 || cellIndex >= _columnCount) return;
            using var step = UndoStep();

            foreach (TableLine row in _rows)
            {
                if (cellIndex < row.Cells.Length) row.Cells = [.. row.Cells.Take(cellIndex), .. row.Cells.Skip(cellIndex + 1)];
                // 元のファイルに書かれていた数も 1 つ減らす（消した列の分の「,」を書かないように）
                if (row.OriginalCellCount > cellIndex) row.OriginalCellCount--;
            }
            _columnCount--;
            BuildColumns();
            UpdateModified();
        }

        // 今の表がファイルと違うかを調べて、ウィンドウの名前の「*」と保存ボタンを合わせる。上の情報と警告も出し直す
        private void UpdateModified()
        {
            _file.TextContent = BuildText();
            bool modified = _file.IsModified;
            Title = modified ? _baseTitle + " *" : _baseTitle;
            BtnSave.IsEnabled = modified;
            UpdateInfoAndWarnings();
        }

        // 上の情報（ヘッダー・文字コード・行と列の数）と警告（文字コードの指定・数字でないセル）を出す
        private void UpdateInfoAndWarnings()
        {
            int dataCount = _rows.Count(row => !row.IsComment && row.Cells.Any(cell => cell.Trim().Length > 0));
            TxtFileInfo.Text = Loc.F("Tw.011", _headerLine.TrimEnd(',', ' ', '\t'), _file.EncodingDisplayName, dataCount, _columnLabel, _columnCount - 1);

            List<string> warnings = [];
            if (_file.EncodingWarning != null) warnings.Add(_file.EncodingWarning);
            List<string> nonNumbers = FindNonNumberCells();
            if (nonNumbers.Count > 0)
            {
                string listed = string.Join("、", nonNumbers.Take(MaxListedProblems));
                string more = nonNumbers.Count > MaxListedProblems ? Loc.F("Tw.012", nonNumbers.Count - MaxListedProblems) : string.Empty;
                warnings.Add(Loc.F("Tw.013", listed, more));
            }
            // 値が空の行（1 列目だけ）と、1 列目が空の行
            (List<string> noValues, List<string> noFirst) = FindEmptyValueRows();
            if (noValues.Count > 0) warnings.Add(Loc.F("Tw.014", ListProblems(noValues)));
            if (noFirst.Count > 0) warnings.Add(Loc.F("Tw.015", _firstColumnName, ListProblems(noFirst)));

            List<string> notices = [];
            if (_isParamsTable) CheckParams(warnings, notices);
            // モーター音の表（音量・ピッチ）の値の範囲と、Sound.txt の [Motor] との突き合わせ（TableWindow.MotorNoise.cs）
            if (_isMotorNoiseTable) CheckMotorNoise(warnings, notices);

            TxtTableWarnings.Text = string.Join("\n", warnings.Select(warning => "⚠ " + warning));
            TxtTableWarnings.Visibility = warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            TxtTableNotices.Text = string.Join("\n", notices.Select(notice => "ⓘ " + notice));
            TxtTableNotices.Visibility = notices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // 進段パラメーターの項目名を調べる：「単位・説明」の列を出し直し、
        // 一覧にない項目名・同じ項目名が 2 回は警告、ヘッダーのバージョンより新しい項目・書かれていない項目はお知らせ
        private void CheckParams(List<string> warnings, List<string> notices)
        {
            List<TableLine> keyRows = [];
            foreach (TableLine row in _rows)
            {
                row.Description = row.IsComment ? string.Empty : PerformanceParams.Describe(row.Cells[0], _version);
                if (!row.IsComment && row.Cells[0].Trim().Length > 0) keyRows.Add(row);
            }

            List<string> unknown = [.. keyRows.Where(row => PerformanceParams.Find(row.Cells[0]) == null).Select(row => Loc.F("Tw.016", row.LineNumber, row.Cells[0].Trim()))];
            // BVE は一覧にない項目名を「'○○' is not valid」のエラーにする（ユーザーが確かめた。Plan.md の window10）
            if (unknown.Count > 0) warnings.Add(Loc.F("Tw.017", string.Join("、", unknown)));

            List<string> duplicated = [.. keyRows
                .GroupBy(row => row.Cells[0].Trim(), StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .Select(group => Loc.F("Tw.018", group.First().Cells[0].Trim(), string.Join(Loc.T("Tw2.003"), group.Select(row => row.LineNumber))))];
            if (duplicated.Count > 0) warnings.Add(Loc.F("Tw.019", string.Join("、", duplicated)));

            List<string> tooNew = [.. keyRows
                .Select(row => PerformanceParams.Find(row.Cells[0]))
                .OfType<PerformanceParams.ParamKey>()
                .Where(key => PerformanceParams.IsTooNew(key, _version))
                .Select(key => Loc.F("Tw.020", key.Key, key.MinVersion))];
            if (tooNew.Count > 0) notices.Add(Loc.F("Tw.021", string.Join("、", tooNew), _version));

            List<string> missing = PerformanceParams.FindMissing(keyRows.Select(row => row.Cells[0]), _version);
            if (missing.Count > 0) notices.Add(Loc.F("Tw.022", string.Join("、", missing)));
            BtnAddMissing.Visibility = missing.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// 進段パラメーターの書かれていない項目を、最後のデータの行の下に足す（値はノッチの数だけ 0。メッセージは出さない）
        /// 足した行を返す（足すものがなければ空）
        /// </summary>
        private List<TableLine> AddMissingKeys()
        {
            if (!_isParamsTable) return [];

            IEnumerable<string> written = _rows.Where(row => !row.IsComment).Select(row => row.Cells[0]);
            List<string> missing = PerformanceParams.FindMissing(written, _version);
            if (missing.Count == 0) return [];
            using var step = UndoStep();

            // 最後の空の行（ファイルの最後の空行など）より前に足す
            int insertAt = _rows.FindLastIndex(row => row.Cells.Any(cell => cell.Trim().Length > 0)) + 1;
            List<TableLine> added = [];
            foreach (string key in missing)
            {
                string[] cells = new string[_columnCount];
                Array.Fill(cells, "0");
                cells[0] = key;
                added.Add(new(0, cells, 0, false));
            }
            _rows.InsertRange(insertAt, added);
            RenumberRows();
            GridTable.Items.Refresh();
            UpdateModified();
            return added;
        }

        // 足りない項目を足す：項目名を見せて確認してから足し、足した最初の行を選ぶ
        private void BtnAddMissing_Click(object sender, RoutedEventArgs e)
        {
            GridTable.CommitEdit(DataGridEditingUnit.Row, true);
            List<string> missing = PerformanceParams.FindMissing(_rows.Where(row => !row.IsComment).Select(row => row.Cells[0]), _version);
            if (missing.Count == 0) return;

            MessageBoxResult result = MessageBox.Show(
                Loc.F("Tw.023", string.Join("\n", missing)),
                Loc.T("Tw.024"), MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;

            List<TableLine> added = AddMissingKeys();
            if (added.Count == 0) return;
            OperationHistory.Add($"表に足りない項目を足す: {string.Join("・", added.Select(row => row.Cells[0]))}");
            // 足した最初の行の「ノッチ 1」（ノッチの列がなければ項目名）のセルを選ぶ
            DataGridColumn firstValue = _cellColumns[Math.Min(1, _cellColumns.Count - 1)];
            SelectCell(added[0], GridTable.Columns.IndexOf(firstValue));
        }

        // セルを直し終えたとき：表の中身に書き戻されたあとで、未保存かを調べ直す
        private void GridTable_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
        {
            _isEditing = false;
            if (e.EditAction != DataGridEditAction.Commit)
            {
                _editBefore = null;
                return;
            }
            // 書き戻されたあとで、未保存の印を合わせ、中身が変わっていれば元に戻す記録に入れる
            Dispatcher.BeginInvoke(() =>
            {
                UpdateModified();
                EndCellEditRecord();
            }, DispatcherPriority.Background);
        }

        // 選んでいるセルの行（なければ、今いるセルの行）
        private List<TableLine> SelectedRows()
        {
            List<TableLine> rows = [.. GridTable.SelectedCells.Select(cell => cell.Item).OfType<TableLine>().Distinct()];
            if (rows.Count == 0 && GridTable.CurrentCell.Item is TableLine current) rows.Add(current);
            return rows;
        }

        // 足した行・列のセルを選んで、見える所までスクロールする
        private void SelectCell(TableLine row, int columnIndex)
        {
            if (columnIndex < 0 || columnIndex >= GridTable.Columns.Count) return;
            DataGridColumn column = GridTable.Columns[columnIndex];
            GridTable.SelectedCells.Clear();
            GridTable.CurrentCell = new DataGridCellInfo(row, column);
            GridTable.SelectedCells.Add(GridTable.CurrentCell);
            GridTable.ScrollIntoView(row, column);
            GridTable.Focus();
        }

        // 行を下に足す：選んでいるセルの行のうち一番下の行の下（選んでいなければ最後）
        private void BtnAddRow_Click(object sender, RoutedEventArgs e)
        {
            GridTable.CommitEdit(DataGridEditingUnit.Row, true);
            List<TableLine> selected = SelectedRows();
            int index = selected.Count == 0 ? -1 : selected.Max(row => _rows.IndexOf(row));
            TableLine added = InsertRowAfter(index);
            OperationHistory.Add($"表に行を足す: {added.LineNumber} 行目");
            SelectCell(added, 1);
        }

        // 行を消す：選んでいるセルの行を、確認してから消す
        private void BtnRemoveRow_Click(object sender, RoutedEventArgs e)
        {
            GridTable.CommitEdit(DataGridEditingUnit.Row, true);
            List<TableLine> selected = SelectedRows();
            if (selected.Count == 0)
            {
                MessageBox.Show(Loc.T("Tw.025"), Loc.T("Tw.026"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string numbers = string.Join(Loc.T("Tw.027"), selected.Select(row => row.LineNumber).Order().Take(MaxListedProblems));
            string more = selected.Count > MaxListedProblems ? Loc.T("Tw.028") : string.Empty;
            MessageBoxResult result = MessageBox.Show(
                Loc.F("Tw.029", selected.Count, numbers, more),
                Loc.T("Tw.030"), MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;

            RemoveRows(selected);
            OperationHistory.Add($"表の行を消す: {selected.Count} 行");
        }

        // 列（ノッチ）を足す：右の端に足して、足した列を見せる
        private void BtnAddColumn_Click(object sender, RoutedEventArgs e)
        {
            GridTable.CommitEdit(DataGridEditingUnit.Row, true);
            AddColumn();
            OperationHistory.Add($"表に列を足す: {ColumnName(_columnCount - 1)}");
            TableLine? first = _rows.FirstOrDefault(row => !row.IsComment) ?? _rows.FirstOrDefault();
            if (first == null) return;
            SelectCell(first, GridTable.Columns.Count - 1);
        }

        // 列を消す：今いるセルの列を、確認してから消す（左の「行」と 1 列目は消せない）
        private void BtnRemoveColumn_Click(object sender, RoutedEventArgs e)
        {
            GridTable.CommitEdit(DataGridEditingUnit.Row, true);
            // セルの列の番号（「行」と「単位・説明」の列は -1）
            int cellIndex = GridTable.CurrentCell.Column == null ? -1 : _cellColumns.IndexOf(GridTable.CurrentCell.Column);
            if (cellIndex < 1)
            {
                MessageBox.Show(Loc.F("Tw.031", _columnLabel), Loc.T("Tw.032"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            MessageBoxResult result = MessageBox.Show(
                Loc.F("Tw.033", ColumnName(cellIndex)),
                Loc.T("Tw.034"), MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;

            RemoveColumn(cellIndex);
            OperationHistory.Add($"表の列を消す: {ColumnName(cellIndex)}");
        }

        // 直している途中のセルを決めてから、保存する
        private bool CommitAndSave()
        {
            GridTable.CommitEdit(DataGridEditingUnit.Row, true);
            string? error = SaveFile();
            if (error == null) return true;

            MessageBox.Show(Loc.F("Tw.035", error), Loc.T("Tw.036"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (!CommitAndSave()) return;
            MessageBox.Show(Loc.F("Tw.037", _file.FullPath), Loc.T("Tw.038"), MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        // Ctrl+S で保存（セルを直している途中でも、決めてから保存する）
        // Ctrl+Z で元に戻す、Ctrl+Y・Ctrl+Shift+Z でやり直す（セルを直している途中は、打っている文字の取り消しに使うので、ここでは受け取らない）
        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!_isEditing && e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                Undo();
                return;
            }
            if (!_isEditing && ((e.Key == Key.Y && Keyboard.Modifiers == ModifierKeys.Control) || (e.Key == Key.Z && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))))
            {
                e.Handled = true;
                Redo();
                return;
            }
            if (e.Key != Key.S || Keyboard.Modifiers != ModifierKeys.Control) return;
            e.Handled = true;
            GridTable.CommitEdit(DataGridEditingUnit.Row, true);
            UpdateModified();
            if (!BtnSave.IsEnabled) return;
            BtnSave_Click(sender, e);
        }

        // Esc で閉じる（セルを直している途中の Esc は、表が「やめる」に使うので、ここまで来ない）
        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Close();
        }

        // 未保存のまま閉じようとしたら、保存して閉じる／保存せずに閉じる／キャンセルを選ぶ
        private void Window_Closing(object? sender, CancelEventArgs e)
        {
            GridTable.CommitEdit(DataGridEditingUnit.Row, true);
            UpdateModified();
            if (!_file.IsModified) return;

            MessageBoxResult result = MessageBox.Show(
                Loc.F("Tw.039", _file.FileName),
                Loc.T("Tw.040"), MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (result == MessageBoxResult.No) return;
            if (result == MessageBoxResult.Yes && CommitAndSave()) return;
            e.Cancel = true;
        }
    }
}
