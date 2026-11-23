using System.IO;

namespace BveVehicleEditor
{
    /// <summary>
    /// 表のファイル（引張力・電流などの CSV）の中身を、作った行に置き換えてファイルに直接書く（「式から作る」でまとめて作るとき。ユーザーの判断）
    /// 1 行目のヘッダーと、その下の先頭のコメントの行は残す。文字コードと改行の形は元のまま。書く前の中身は同じフォルダーに .bak で残す
    /// </summary>
    internal static class PerformanceTableWriter
    {
        /// <summary>
        /// 表のファイルの中身を、作った行に置き換えて書く。書く前の中身を残した .bak のパスを返す
        /// 読めない・ヘッダーが違う・書けないときは例外（呼ぶ側で ErrorText.Describe を使って日本語で知らせる）
        /// </summary>
        /// <param name="fullPath">表のファイルのフルパス</param>
        /// <param name="rows">作った行（1 列目が速度、2 列目からノッチごとの値）</param>
        /// <param name="expectedHeader">1 行目のヘッダー（例：BveTs Vehicle Performance Table）</param>
        internal static string WriteRows(string fullPath, IReadOnlyList<string[]> rows, string expectedHeader)
        {
            FileItem file = new(Path.GetFileName(fullPath), expectedHeader);
            file.Load(fullPath);
            if (!file.IsLoaded) throw new FileNotFoundException($"ファイルが見つかりません。\n{fullPath}", fullPath);

            file.TextContent = BuildText(file.TextContent, rows);

            // 書く前の中身を残す（何回作っても前のものを消さないように、時刻を名前に入れる）
            string backupPath = $"{fullPath}.{DateTime.Now:yyyyMMdd-HHmmss}.bak";
            File.Copy(fullPath, backupPath, overwrite: true);
            file.Save();
            OperationHistory.Add($"表のファイルに直接書く: {fullPath}（前の中身は {backupPath}）");
            return backupPath;
        }

        /// <summary>
        /// 元の中身の 1 行目（ヘッダー）と先頭のコメントの行を残し、その下を作った行にした中身を作る（改行の形は元に合わせる）
        /// </summary>
        internal static string BuildText(string original, IReadOnlyList<string[]> rows)
        {
            string newLine = original.Contains("\r\n") ? "\r\n" : "\n";
            string[] lines = original.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            List<string> kept = [];
            if (lines.Length > 0) kept.Add(lines[0]);
            for (int i = 1; i < lines.Length && lines[i].TrimStart().StartsWith('#'); i++) kept.Add(lines[i]);

            IEnumerable<string> body = rows.Select(cells => string.Join(",", cells));
            return string.Join(newLine, kept.Concat(body)) + newLine;
        }
    }
}
