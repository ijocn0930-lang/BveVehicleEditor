using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace BveVehicleEditor
{
    /// <summary>
    /// 表のウィンドウの、セルを選んで行う操作（段階3-1）
    /// Delete で中身を空にする・見出しのダブルクリックで列を全部選ぶ・右クリックメニュー（行・列を足す・消す、中身を空にする）・
    /// 切り取り・コピー・貼り付け（Excel と同じタブ区切り）
    /// </summary>
    public partial class TableWindow
    {
        // セルを直している途中か（Delete キーを、直している文字の削除に使わせるため）
        private bool _isEditing;

        // コンストラクターから呼ぶ：切り取り・コピー・貼り付けを、表の既定の動き（「行」の列も写る）より先に受け取る
        private void InitializeEditing()
        {
            GridTable.CommandBindings.Add(new(ApplicationCommands.Copy, (s, e) => CopySelection(), CanCopyOrCut));
            GridTable.CommandBindings.Add(new(ApplicationCommands.Cut, (s, e) => CutSelection(), CanCopyOrCut));
            GridTable.CommandBindings.Add(new(ApplicationCommands.Paste, (s, e) => PasteFromClipboard(), CanPaste));
        }

        // ---- 選んでいるセル ----

        /// <summary>
        /// 選んでいるセルの位置（表の何行目・セルの何列目。どちらも 0 から）。「行」と「単位・説明」の列は入れない
        /// </summary>
        private List<(int Row, int Cell)> SelectedPositions()
        {
            List<(int Row, int Cell)> positions = [];
            foreach (DataGridCellInfo info in GridTable.SelectedCells)
            {
                if (info.Item is not TableLine row) continue;
                int rowIndex = _rows.IndexOf(row);
                int cellIndex = _cellColumns.IndexOf(info.Column);
                if (rowIndex < 0 || cellIndex < 0) continue;
                positions.Add((rowIndex, cellIndex));
            }
            return positions;
        }

        // 選んでいるセルの列の番号（重なりなし・小さい順）。「行」と「単位・説明」の列は入れない
        private List<int> SelectedCellIndices()
        {
            List<int> indices = [.. SelectedPositions().Select(position => position.Cell).Distinct().Order()];
            if (indices.Count == 0 && GridTable.CurrentCell.Column != null)
            {
                int current = _cellColumns.IndexOf(GridTable.CurrentCell.Column);
                if (current >= 0) indices.Add(current);
            }
            return indices;
        }

        // 表を表示し直したあとに、前と同じセルを選び直す（Items.Refresh で選んでいたセルが外れるため）
        private void RefreshKeepingSelection()
        {
            List<DataGridCellInfo> selected = [.. GridTable.SelectedCells];
            GridTable.Items.Refresh();
            GridTable.SelectedCells.Clear();
            foreach (DataGridCellInfo info in selected.Where(info => info.Item is TableLine row && _rows.Contains(row)))
            {
                GridTable.SelectedCells.Add(new(info.Item, info.Column));
            }
        }

        // 表の rowStart～rowEnd 行目、セルの cellStart～cellEnd 列目を選ぶ（貼り付けたあとに使う）
        private void SelectRange(int rowStart, int rowEnd, int cellStart, int cellEnd)
        {
            GridTable.SelectedCells.Clear();
            for (int r = rowStart; r <= rowEnd && r < _rows.Count; r++)
            {
                for (int c = cellStart; c <= cellEnd && c < _cellColumns.Count; c++)
                {
                    GridTable.SelectedCells.Add(new(_rows[r], _cellColumns[c]));
                }
            }
            if (rowStart >= _rows.Count || cellStart >= _cellColumns.Count) return;
            GridTable.CurrentCell = new(_rows[rowStart], _cellColumns[cellStart]);
            GridTable.ScrollIntoView(_rows[rowStart], _cellColumns[cellStart]);
        }

        // ---- 中身を空にする（Delete） ----

        /// <summary>
        /// 選んでいるセルの中身を空にする（メッセージは出さない）。空にしたセルの数を返す
        /// </summary>
        private int ClearSelectedCells()
        {
            using var step = UndoStep();
            List<(int Row, int Cell)> positions = SelectedPositions();
            int cleared = 0;
            foreach ((int rowIndex, int cellIndex) in positions)
            {
                TableLine row = _rows[rowIndex];
                if (cellIndex >= row.Cells.Length || row.Cells[cellIndex].Length == 0) continue;
                row.Cells[cellIndex] = string.Empty;
                cleared++;
            }
            if (cleared == 0) return 0;

            RefreshKeepingSelection();
            UpdateModified();
            return cleared;
        }

        // ---- 切り取り・コピー・貼り付け ----

        /// <summary>
        /// 選んでいるセルを、Excel と同じタブ区切りの文字にする（選んでいる範囲を囲む四角の形。選んでいないセルは空）。選んでいなければ null
        /// </summary>
        private string? BuildCopyText()
        {
            List<(int Row, int Cell)> positions = SelectedPositions();
            if (positions.Count == 0) return null;

            int rowStart = positions.Min(p => p.Row), rowEnd = positions.Max(p => p.Row);
            int cellStart = positions.Min(p => p.Cell), cellEnd = positions.Max(p => p.Cell);
            HashSet<(int, int)> selected = [.. positions];
            List<string> lines = [];
            for (int r = rowStart; r <= rowEnd; r++)
            {
                IEnumerable<string> cells = Enumerable.Range(cellStart, cellEnd - cellStart + 1)
                    .Select(c => selected.Contains((r, c)) && c < _rows[r].Cells.Length ? _rows[r].Cells[c] : string.Empty);
                lines.Add(string.Join("\t", cells));
            }
            return string.Join("\r\n", lines);
        }

        private void CanCopyOrCut(object sender, CanExecuteRoutedEventArgs e)
        {
            e.CanExecute = !_isEditing && SelectedPositions().Count > 0;
            e.Handled = true;
        }

        private void CanPaste(object sender, CanExecuteRoutedEventArgs e)
        {
            e.CanExecute = !_isEditing && TryGetClipboardText(out _, showError: false);
            e.Handled = true;
        }

        // コピー：選んでいるセルをクリップボードに入れる
        private void CopySelection()
        {
            string? text = BuildCopyText();
            if (text == null) return;
            if (!TrySetClipboardText(text)) return;
            OperationHistory.Add($"表のコピー: {SelectedPositions().Count} セル");
        }

        // 切り取り：コピーしてから中身を空にする
        private void CutSelection()
        {
            string? text = BuildCopyText();
            if (text == null) return;
            if (!TrySetClipboardText(text)) return;
            int cleared = ClearSelectedCells();
            OperationHistory.Add($"表の切り取り: {cleared} セル");
        }

        // 貼り付け：選んでいる範囲の左上から貼る。表からはみ出すときは、行・列を足す
        private void PasteFromClipboard()
        {
            if (!TryGetClipboardText(out string text, showError: true)) return;

            (int row, int cell) = PasteStart();
            (int rows, int cells) = PasteText(text, row, cell);
            if (rows == 0) return;
            OperationHistory.Add($"表の貼り付け: {rows} 行 × {cells} 列（{_rows[row].LineNumber} 行目の{ColumnName(cell)}から）");
        }

        // 貼り付ける左上の位置：選んでいる範囲の左上（選んでいなければ今いるセル、それもなければ表の 1 行目の 1 列目）
        private (int Row, int Cell) PasteStart()
        {
            List<(int Row, int Cell)> positions = SelectedPositions();
            if (positions.Count > 0) return (positions.Min(p => p.Row), positions.Min(p => p.Cell));

            int rowIndex = GridTable.CurrentCell.Item is TableLine current ? _rows.IndexOf(current) : 0;
            int cellIndex = GridTable.CurrentCell.Column == null ? 0 : Math.Max(0, _cellColumns.IndexOf(GridTable.CurrentCell.Column));
            return (Math.Max(0, rowIndex), cellIndex);
        }

        /// <summary>
        /// 文字を表の rowStart 行目・cellStart 列目から貼る（メッセージは出さない）。タブ区切り（Excel）か、タブがなければ「,」区切り
        /// 表からはみ出すときは行・列を足す。貼った行と列の数を返す
        /// </summary>
        private (int Rows, int Cells) PasteText(string text, int rowStart, int cellStart)
        {
            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            if (lines.Length > 1 && lines[^1].Length == 0) lines = lines[..^1];
            char separator = text.Contains('\t') ? '\t' : ',';
            string[][] values = [.. lines.Select(line => line.Split(separator))];
            int width = values.Max(line => line.Length);
            if (values.Length == 0 || width == 0) return (0, 0);
            using var step = UndoStep();

            // はみ出す分の行（最後に空の行）と列（右の端に空の列）を足す
            while (_rows.Count < rowStart + values.Length)
            {
                string[] empty = new string[_columnCount];
                Array.Fill(empty, string.Empty);
                _rows.Add(new(0, empty, 0, false));
            }
            if (_columnCount < cellStart + width)
            {
                _columnCount = cellStart + width;
                foreach (TableLine row in _rows) row.Cells = PadCells(row.Cells, _columnCount);
                BuildColumns();
            }

            for (int r = 0; r < values.Length; r++)
            {
                TableLine row = _rows[rowStart + r];
                row.Cells = PadCells(row.Cells, _columnCount);
                for (int c = 0; c < values[r].Length; c++) row.Cells[cellStart + c] = values[r][c];
            }
            RenumberRows();
            GridTable.Items.Refresh();
            UpdateModified();
            SelectRange(rowStart, rowStart + values.Length - 1, cellStart, cellStart + width - 1);
            return (values.Length, width);
        }

        // クリップボードに文字を入れる。ほかのアプリが使っていて入れられなければ、日本語で知らせて false
        private static bool TrySetClipboardText(string text)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (ExternalException)
            {
                MessageBox.Show(Loc.T("TwE.001"), Loc.T("TwE.002"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
        }

        // クリップボードの文字を読む。文字がなければ false（showError のときだけ、読めない理由を知らせる）
        private static bool TryGetClipboardText(out string text, bool showError)
        {
            text = string.Empty;
            try
            {
                if (!Clipboard.ContainsText()) return false;
                text = Clipboard.GetText();
                return text.Length > 0;
            }
            catch (ExternalException)
            {
                if (showError) MessageBox.Show(Loc.T("TwE.003"), Loc.T("TwE.004"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
        }

        // ---- キー・マウス ----

        private void GridTable_BeginningEdit(object? sender, DataGridBeginningEditEventArgs e)
        {
            _isEditing = !e.Cancel;
            // 直す前の中身を写しておく（直し終えて変わっていれば、元に戻す記録に入れる）
            if (_isEditing) BeginCellEditRecord();
        }

        // Delete：直している途中でなければ、選んでいるセルの中身を空にする
        private void GridTable_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Delete || _isEditing || Keyboard.Modifiers != ModifierKeys.None) return;
            e.Handled = true;
            int cleared = ClearSelectedCells();
            if (cleared > 0) OperationHistory.Add($"表の中身を空にする: {cleared} セル");
        }

        // 見出しのダブルクリック：ノッチ・1 列目の見出しならその列のセルを全部、「行」の見出しなら表のセルを全部選ぶ
        private void GridTable_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            DataGridColumnHeader? header = FindParent<DataGridColumnHeader>(e.OriginalSource as DependencyObject);
            if (header?.Column == null) return;
            e.Handled = true;

            GridTable.CommitEdit(DataGridEditingUnit.Row, true);
            int cellIndex = _cellColumns.IndexOf(header.Column);
            if (cellIndex >= 0) SelectRange(0, _rows.Count - 1, cellIndex, cellIndex);
            else if (GridTable.Columns.IndexOf(header.Column) == 0) SelectRange(0, _rows.Count - 1, 0, _cellColumns.Count - 1);
            GridTable.Focus();
        }

        // 右クリック：押したセルが選ばれていなければ、そのセルだけを選んでからメニューを出す
        private void GridTable_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            DataGridCell? cell = FindParent<DataGridCell>(e.OriginalSource as DependencyObject);
            if (cell == null || cell.IsSelected) return;

            GridTable.CommitEdit(DataGridEditingUnit.Row, true);
            GridTable.SelectedCells.Clear();
            DataGridCellInfo info = new(cell.DataContext, cell.Column);
            GridTable.CurrentCell = info;
            GridTable.SelectedCells.Add(info);
            cell.Focus();
        }

        // 画面の部品の親をたどって、T の部品を探す（見つからなければ null）
        private static T? FindParent<T>(DependencyObject? element) where T : DependencyObject
        {
            while (element != null && element is not T)
            {
                element = element is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
            }
            return element as T;
        }

        // ---- 右クリックメニュー ----

        // 行を上に足す：選んでいる行のうち一番上の行の上（選んでいなければ最後）
        private void MenuAddRowAbove_Click(object sender, RoutedEventArgs e)
        {
            GridTable.CommitEdit(DataGridEditingUnit.Row, true);
            List<TableLine> selected = SelectedRows();
            int position = selected.Count == 0 ? _rows.Count : selected.Min(row => _rows.IndexOf(row));
            TableLine added = InsertRowAt(position);
            OperationHistory.Add($"表に行を足す（上）: {added.LineNumber} 行目");
            SelectCell(added, GridTable.Columns.IndexOf(_cellColumns[0]));
        }

        // 行を下に足す：選んでいる行のうち一番下の行の下（ボタンと同じ）
        private void MenuAddRowBelow_Click(object sender, RoutedEventArgs e)
        {
            BtnAddRow_Click(sender, e);
        }

        // 列を左に足す：選んでいる列のうち一番左の列の左（1 列目の左には足せない）
        private void MenuAddColumnLeft_Click(object sender, RoutedEventArgs e)
        {
            GridTable.CommitEdit(DataGridEditingUnit.Row, true);
            List<int> indices = SelectedCellIndices();
            if (indices.Count == 0)
            {
                MessageBox.Show(Loc.T("TwE.005"), Loc.T("TwE.006"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (indices[0] == 0)
            {
                MessageBox.Show(Loc.F("TwE.007", _firstColumnName), Loc.T("TwE.008"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            AddColumnAndSelect(indices[0]);
        }

        // 列を右に足す：選んでいる列のうち一番右の列の右（選んでいなければ右の端）
        private void MenuAddColumnRight_Click(object sender, RoutedEventArgs e)
        {
            GridTable.CommitEdit(DataGridEditingUnit.Row, true);
            List<int> indices = SelectedCellIndices();
            AddColumnAndSelect(indices.Count == 0 ? _columnCount : indices[^1] + 1);
        }

        // cellIndex 番目に列を足し、足した列の最初のデータの行のセルを選ぶ
        private void AddColumnAndSelect(int cellIndex)
        {
            InsertColumnAt(cellIndex);
            OperationHistory.Add($"表に列を足す: {ColumnName(cellIndex)}");
            TableLine? first = _rows.FirstOrDefault(row => !row.IsComment) ?? _rows.FirstOrDefault();
            if (first == null) return;
            SelectCell(first, GridTable.Columns.IndexOf(_cellColumns[cellIndex]));
        }

        // 選んだ列を消す：選んでいる列（1 列目は除く）を、確認してから消す
        private void MenuRemoveColumns_Click(object sender, RoutedEventArgs e)
        {
            GridTable.CommitEdit(DataGridEditingUnit.Row, true);
            List<int> indices = [.. SelectedCellIndices().Where(index => index >= 1)];
            if (indices.Count == 0)
            {
                MessageBox.Show(Loc.F("TwE.009", _columnLabel, _firstColumnName), Loc.T("TwE.010"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string names = string.Join(Loc.T("TwE.011"), indices.Select(ColumnName));
            MessageBoxResult result = MessageBox.Show(
                Loc.F("TwE.012", names),
                Loc.T("TwE.013"), MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;

            RemoveColumns(indices);
            OperationHistory.Add($"表の列を消す: {names}");
        }

        /// <summary>
        /// いくつかの列をまとめて消す（右の列から消すので、番号がずれない。メッセージは出さない）
        /// </summary>
        private void RemoveColumns(IEnumerable<int> cellIndices)
        {
            // いくつ消しても、元に戻すのは 1 回にまとめる
            using var step = UndoStep();
            foreach (int index in cellIndices.Distinct().OrderDescending()) RemoveColumn(index);
        }

        // 中身を空にする（Delete と同じ）
        private void MenuClearCells_Click(object sender, RoutedEventArgs e)
        {
            GridTable.CommitEdit(DataGridEditingUnit.Row, true);
            int cleared = ClearSelectedCells();
            if (cleared > 0) OperationHistory.Add($"表の中身を空にする: {cleared} セル");
        }
    }
}
