using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using OxyPlot;

namespace BveVehicleEditor
{
    /// <summary>
    /// 表のウィンドウの「グラフ」のタブ（段階4。部品は OxyPlot に決めた）
    /// 横が速度（1 列目）、縦が引張力・電流（ノッチの列）。ノッチごとに 1 本の線を描く。グラフの作り方は ChartBuilder と共通
    /// </summary>
    public partial class TableWindow
    {
        // グラフのタブを開いたら、今の表から描き直す
        private void TabsTable_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // 表の中のコンボボックスなどの選択の変化は、ここまで上がってくるので無視する
            if (e.OriginalSource != TabsTable || TabsTable.SelectedItem != TabGraph) return;

            GridTable.CommitEdit(DataGridEditingUnit.Row, true);
            DrawGraph();
        }

        /// <summary>
        /// 今の表からグラフを描く（コメントの行、数字でないセル・空のセルは飛ばす）。描いた線の数を返す
        /// </summary>
        private int DrawGraph()
        {
            PlotModel model = ChartBuilder.CreateModel(_valueName, this);
            int drawn = 0;
            List<double> values = [];
            for (int cell = 1; cell < _columnCount; cell++)
            {
                List<DataPoint> points = [];
                foreach (TableLine row in _rows.Where(row => !row.IsComment))
                {
                    if (!TryReadNumber(row, 0, out double speed) || !TryReadNumber(row, cell, out double value)) continue;
                    points.Add(new DataPoint(speed, value));
                    values.Add(value);
                }
                if (ChartBuilder.AddLine(model, ColumnName(cell), cell - 1, points)) drawn++;
            }
            // 使わない速度にとても大きい値を入れた表（抵抗制御のサンプルなど）でも、線がつぶれないように縦の軸の上限を決める
            ChartBuilder.LimitValueAxis(model, values);

            ChartBuilder.Show(PlotGraph, model);
            PlotGraph.Controller ??= ChartBuilder.CreateController();
            ChartBuilder.ApplyTrackerStyle(PlotGraph);
            return drawn;
        }

        /// <summary>
        /// 「式から作る…」を使えるようにする（作る表の種類と、[Dynamics]・[Passengers]・ノッチ数を読むためのパラメーターのテキスト）
        /// 呼ばなければ「式から作る…」は出さない
        /// </summary>
        /// <param name="targets">表の種類ごとのファイルのフルパス（まとめて作るとき、ほかの表を書く場所。見つかるファイルだけ）</param>
        /// <param name="isBrake">ブレーキ（[Brake]）の表か</param>
        internal void EnableGenerate(TractionCalculator.TableKind kind, Func<string> paramTextProvider, IReadOnlyDictionary<TractionCalculator.TableKind, string> targets, bool isBrake = false)
        {
            _generateIsBrake = isBrake;
            _generateKind = kind;
            _paramTextProvider = paramTextProvider;
            _generateTargets = targets;
            BtnGenerate.Visibility = Visibility.Visible;
        }

        // 式から作る表の種類と、パラメーターのテキストを読む、ほかの表のファイルの場所（EnableGenerate でもらう）
        private TractionCalculator.TableKind _generateKind;
        private bool _generateIsBrake;
        private Func<string>? _paramTextProvider;
        private IReadOnlyDictionary<TractionCalculator.TableKind, string> _generateTargets = new Dictionary<TractionCalculator.TableKind, string>();

        // 式から作る：引張力を式から作るウィンドウを開き、「表に入れる」なら表の中身を置き換える
        private void BtnGenerate_Click(object sender, RoutedEventArgs e)
        {
            if (_paramTextProvider == null) return;
            GridTable.CommitEdit(DataGridEditingUnit.Row, true);

            // 今の表の最後の速度を、最高速度の最初の値にする
            double? maxSpeed = _rows.Where(row => !row.IsComment).Select(row => TryReadNumber(row, 0, out double v) ? v : (double?)null).LastOrDefault(v => v != null);
            TractionWindow window = new(_paramTextProvider(), maxSpeed, _generateKind, _generateTargets, _generateIsBrake) { Owner = this };
            // 開いている表をチェックしていなければ、ほかの表だけがファイルに書かれ、この表は変わらない
            if (window.ShowDialog() != true || window.GeneratedRows == null) return;

            ReplaceDataRows(window.GeneratedRows);
            OperationHistory.Add($"表を式から作る: {window.GeneratedRows.Count} 行");
        }

        /// <summary>
        /// 「抵抗制御で作る…」を使えるようにする（力行・ブレーキの引張力・電流の空車・満車だけ）。呼ばなければ出さない
        /// </summary>
        /// <param name="targets">表の種類ごとのファイルのフルパス（まとめて作るとき、ほかの表を書く場所）</param>
        /// <param name="paramsPath">進段パラメーターのフルパス（段の数と限流値を読む。なければ null）</param>
        /// <param name="isBrake">ブレーキ（[Brake]）の表か（ブレーキなら ResistanceBrakeWindow を開く）</param>
        internal void EnableResistance(TractionCalculator.TableKind kind, IReadOnlyDictionary<TractionCalculator.TableKind, string> targets, string? paramsPath, bool isBrake = false)
        {
            _resistanceKind = kind;
            _resistanceTargets = targets;
            _resistanceParamsPath = paramsPath;
            _resistanceIsBrake = isBrake;
            BtnResistance.Visibility = Visibility.Visible;
        }

        // 抵抗制御で作る表の種類・ほかの表のファイルの場所・進段パラメーターの場所・ブレーキか（EnableResistance でもらう）
        private TractionCalculator.TableKind _resistanceKind;
        private IReadOnlyDictionary<TractionCalculator.TableKind, string> _resistanceTargets = new Dictionary<TractionCalculator.TableKind, string>();
        private string? _resistanceParamsPath;
        private bool _resistanceIsBrake;

        // 抵抗制御で作る：ウィンドウ（力行は ResistanceWindow、ブレーキは ResistanceBrakeWindow）を開き、「表に入れる」なら表の中身を置き換える
        private void BtnResistance_Click(object sender, RoutedEventArgs e)
        {
            GridTable.CommitEdit(DataGridEditingUnit.Row, true);
            List<string[]> rows = [.. _rows.Where(row => !row.IsComment).Select(row => row.Cells)];
            List<string[]>? generated;
            if (_resistanceIsBrake)
            {
                ResistanceBrakeWindow window = new(_resistanceKind, rows, _resistanceTargets, _resistanceParamsPath) { Owner = this };
                generated = window.ShowDialog() == true ? window.GeneratedRows : null;
            }
            else
            {
                ResistanceWindow window = new(_resistanceKind, rows, _resistanceTargets, _resistanceParamsPath) { Owner = this };
                generated = window.ShowDialog() == true ? window.GeneratedRows : null;
            }
            // 開いている表をチェックしていなければ、ほかの表だけがファイルに書かれ、この表は変わらない
            if (generated == null) return;

            ReplaceDataRows(generated);
            OperationHistory.Add($"抵抗制御で作る（{(_resistanceIsBrake ? "ブレーキ" : "力行")}）: {generated.Count} 行");
        }

        /// <summary>
        /// 表の中身を、作った行に置き換える（先頭のコメントの行は残す。メッセージは出さない）
        /// </summary>
        private void ReplaceDataRows(IReadOnlyList<string[]> newRows)
        {
            using var step = UndoStep();
            int firstData = _rows.FindIndex(row => !row.IsComment);
            List<TableLine> kept = firstData < 0 ? [.. _rows] : _rows.Take(firstData).ToList();
            _rows.Clear();
            _rows.AddRange(kept);
            foreach (string[] cells in newRows) _rows.Add(new(0, [.. cells], 0, false));

            _columnCount = Math.Max(1, _rows.Max(row => row.Cells.Length));
            foreach (TableLine row in _rows) row.Cells = PadCells(row.Cells, _columnCount);
            RenumberRows();
            BuildColumns();
            GridTable.Items.Refresh();
            UpdateModified();
            if (TabsTable.SelectedItem == TabGraph) DrawGraph();
        }

        // セルを数字として読む（空・数字でなければ false）
        private static bool TryReadNumber(TableLine row, int cell, out double value)
        {
            value = 0;
            return cell < row.Cells.Length
                && double.TryParse(row.Cells[cell].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
    }
}
