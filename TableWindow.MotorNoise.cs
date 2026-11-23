using System.Globalization;

namespace BveVehicleEditor
{
    /// <summary>
    /// 表のウィンドウの、モーター音の表（音量・ピッチ）のチェック（TableWindow の partial）
    /// 値の範囲（音量は 0～1、ピッチはマイナスにしない）は警告、Sound.txt の [Motor] にない音の列はお知らせ
    /// MotorNoiseForm が EnableMotorNoiseChecks を呼んだときだけ調べる
    /// </summary>
    public partial class TableWindow
    {
        // モーター音の表のチェックをするか・音量の表か（false ならピッチの表）
        private bool _isMotorNoiseTable;
        private bool _isVolumeTable;

        // Sound.txt の [Motor] に書かれている番号（サウンドのファイルを読み込めていなければ null）
        private IReadOnlySet<int>? _motorNumbers;

        /// <summary>
        /// モーター音の表のチェックを使えるようにする（呼ばなければ調べない）
        /// </summary>
        /// <param name="isVolume">音量の表か（false ならピッチの表）</param>
        /// <param name="motorNumbers">Sound.txt の [Motor] に書かれている番号（サウンドのファイルを読み込めていなければ null）</param>
        internal void EnableMotorNoiseChecks(bool isVolume, IReadOnlySet<int>? motorNumbers)
        {
            _isMotorNoiseTable = true;
            _isVolumeTable = isVolume;
            _motorNumbers = motorNumbers;
            UpdateInfoAndWarnings();
        }

        // モーター音の表を調べる：値の範囲は警告、[Motor] にない音の列はお知らせ（コメントの行・空のセル・数字でないセルは飛ばす）
        private void CheckMotorNoise(List<string> warnings, List<string> notices)
        {
            List<string> outOfRange = [];
            HashSet<int> usedColumns = [];
            foreach (TableLine row in _rows.Where(row => !row.IsComment))
            {
                for (int cell = 1; cell < row.Cells.Length; cell++)
                {
                    if (!double.TryParse(row.Cells[cell].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)) continue;
                    if (value > 0) usedColumns.Add(cell);
                    bool isBad = _isVolumeTable ? value < 0 || value > 1 : value < 0;
                    if (isBad) outOfRange.Add(Loc.F("TwM.001", row.LineNumber, ColumnName(cell), row.Cells[cell].Trim()));
                }
            }

            if (outOfRange.Count > 0)
            {
                string listed = string.Join("、", outOfRange.Take(MaxListedProblems));
                string more = outOfRange.Count > MaxListedProblems ? Loc.F("TwM.002", outOfRange.Count - MaxListedProblems) : string.Empty;
                warnings.Add(_isVolumeTable
                    ? Loc.F("TwM.003", listed, more)
                    : Loc.F("TwM.004", listed, more));
            }

            if (_motorNumbers == null)
            {
                notices.Add(Loc.T("TwM.005"));
                return;
            }

            // 値が入っている音の列なのに、Sound.txt の [Motor] にその番号の音がない（その列は鳴らない）
            List<string> missing = [.. usedColumns.Order()
                .Select(cell => (Cell: cell, Number: cell - 1 + _firstColumnNumber))
                .Where(item => !_motorNumbers.Contains(item.Number))
                .Select(item => ColumnName(item.Cell))];
            if (missing.Count == 0) return;
            string names = string.Join(Loc.T("TwM.006"), missing.Take(MaxListedProblems * 2));
            string others = missing.Count > MaxListedProblems * 2 ? Loc.F("TwM.007", missing.Count - MaxListedProblems * 2) : string.Empty;
            notices.Add(Loc.F("TwM.008", names, others));
        }
    }
}
