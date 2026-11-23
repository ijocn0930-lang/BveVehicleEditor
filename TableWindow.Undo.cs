using System.Windows.Controls;

namespace BveVehicleEditor
{
    /// <summary>
    /// 表のウィンドウの「元に戻す（Ctrl+Z）」「やり直す（Ctrl+Y / Ctrl+Shift+Z）」（ユーザーが見つけたバグ：表を変えたあとに Ctrl+Z が効かなかった）
    /// 表を変える操作の前に表の中身をまるごと覚えておき、戻すときはその中身にする（新しいほうから MaxUndoCount 回分）
    /// 表を変える処理は、始めに「using var step = UndoStep();」と書く。中でほかの処理を呼んでも、記録は 1 回分にまとまる
    /// </summary>
    public partial class TableWindow
    {
        /// <summary>
        /// 表の中身をまるごと写したもの（行の番号・セル・元のセルの数・コメントの行か、列の数）。Text は変わったかを比べるための中身
        /// </summary>
        private sealed record TableSnapshot(List<(int LineNumber, string[] Cells, int OriginalCellCount, bool IsComment)> Rows, int ColumnCount, string Text);

        // 覚えておく回数（行の多い表でもメモリーを使いすぎないように）
        private const int MaxUndoCount = 100;

        // 元に戻す記録と、やり直す記録（新しいものが後ろ）
        private readonly List<TableSnapshot> _undoStack = [];
        private readonly List<TableSnapshot> _redoStack = [];

        // UndoStep の入れ子の深さ（いちばん外側の処理だけで記録する）と、その始めに写した中身
        private int _undoDepth;
        private TableSnapshot? _undoBefore;

        // セルを直し始めたときに写した中身（直し終えて中身が変わったら記録する）
        private TableSnapshot? _editBefore;

        /// <summary>
        /// 戻せる操作があるか
        /// </summary>
        public bool CanUndo => _undoStack.Count > 0;

        /// <summary>
        /// やり直せる操作があるか
        /// </summary>
        public bool CanRedo => _redoStack.Count > 0;

        /// <summary>
        /// 表を変える処理の始めに呼ぶ。処理が終わって（Dispose で）中身が変わっていれば、元に戻す記録に入れる
        /// </summary>
        private UndoScope UndoStep()
        {
            if (_undoDepth == 0) _undoBefore = Capture();
            _undoDepth++;
            return new(this);
        }

        // UndoStep の終わり（using を抜けたとき）
        private void EndUndoStep()
        {
            _undoDepth--;
            if (_undoDepth > 0 || _undoBefore == null) return;
            Record(_undoBefore);
            _undoBefore = null;
        }

        /// <summary>
        /// using で UndoStep の終わりを呼ぶための入れ物
        /// </summary>
        private readonly struct UndoScope(TableWindow owner) : IDisposable
        {
            public void Dispose() => owner.EndUndoStep();
        }

        // 今の表の中身を写す
        private TableSnapshot Capture()
        {
            return new([.. _rows.Select(row => (row.LineNumber, (string[])row.Cells.Clone(), row.OriginalCellCount, row.IsComment))], _columnCount, BuildText());
        }

        // 操作の前の中身を、元に戻す記録に入れる（中身が変わっていなければ入れない）。やり直す記録は消す
        private void Record(TableSnapshot before)
        {
            if (before.Text == BuildText()) return;
            _undoStack.Add(before);
            if (_undoStack.Count > MaxUndoCount) _undoStack.RemoveAt(0);
            _redoStack.Clear();
        }

        // 写した中身に表を戻す（表示し直して、未保存の印と警告も出し直す）
        private void Restore(TableSnapshot snapshot)
        {
            _rows.Clear();
            foreach ((int lineNumber, string[] cells, int originalCount, bool isComment) in snapshot.Rows)
            {
                _rows.Add(new(lineNumber, (string[])cells.Clone(), originalCount, isComment));
            }
            _columnCount = snapshot.ColumnCount;
            BuildColumns();
            GridTable.Items.Refresh();
            UpdateModified();
            if (TabsTable.SelectedItem == TabGraph) DrawGraph();
        }

        /// <summary>
        /// 元に戻す（戻せなければ何もしない）
        /// </summary>
        internal void Undo()
        {
            GridTable.CommitEdit(DataGridEditingUnit.Row, true);
            if (_undoStack.Count == 0) return;
            _redoStack.Add(Capture());
            TableSnapshot previous = _undoStack[^1];
            _undoStack.RemoveAt(_undoStack.Count - 1);
            Restore(previous);
            OperationHistory.Add("表を元に戻す");
        }

        /// <summary>
        /// やり直す（やり直せなければ何もしない）
        /// </summary>
        internal void Redo()
        {
            GridTable.CommitEdit(DataGridEditingUnit.Row, true);
            if (_redoStack.Count == 0) return;
            _undoStack.Add(Capture());
            TableSnapshot next = _redoStack[^1];
            _redoStack.RemoveAt(_redoStack.Count - 1);
            Restore(next);
            OperationHistory.Add("表をやり直す");
        }

        // セルを直し始めたとき：直す前の中身を写しておく
        private void BeginCellEditRecord()
        {
            _editBefore = Capture();
        }

        // セルを直し終えたとき（表の中身に書き戻されたあと）：中身が変わっていれば記録する
        private void EndCellEditRecord()
        {
            if (_editBefore == null) return;
            Record(_editBefore);
            _editBefore = null;
        }

        private void MenuUndo_Click(object sender, System.Windows.RoutedEventArgs e) => Undo();

        private void MenuRedo_Click(object sender, System.Windows.RoutedEventArgs e) => Redo();

        // 右クリックメニューを開くとき：戻せる・やり直せるときだけ押せるようにする
        private void GridTable_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            MenuUndo.IsEnabled = CanUndo;
            MenuRedo.IsEnabled = CanRedo;
        }
    }
}
