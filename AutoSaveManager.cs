using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace BveVehicleEditor
{
    /// <summary>
    /// 自動保存：決まった間隔で、保存していない変更があるタブの編集中の中身を、控えとしてアプリのフォルダーに書く（車両ファイルそのものは書き換えない）
    /// 置き場所は %LOCALAPPDATA%\BveVehicleEditor\AutoSave\＜車両ファイルごとのフォルダー＞\＜キー＞_＜ファイル名＞（UTF-8・BOM 付き。ファイルごとに最新の 1 つ）
    /// 元のファイルの場所と保存した日時は AutoSave.json に書く（あとで「クラッシュ後の自動復元」に使う）
    /// すべて保存して変更がなくなったとき・普通に閉じたときは、その車両ファイルのフォルダーを消す（アプリが落ちたときだけ残る）
    /// 残った控えは FindLeftover・FindAllLeftovers で探す（書いたアプリがまだ動いていれば、残った控えとして扱わない）。戻すか尋ねるのは MainWindow
    /// 書けない・消せないときもメッセージは出さない（操作履歴に残す）
    /// </summary>
    internal static class AutoSaveManager
    {
        /// <summary>初めの値（自動保存する・5 分ごと）と、間隔の範囲（設定のウィンドウと同じ）</summary>
        internal const bool DefaultEnabled = true;
        internal const int DefaultMinutes = 5;
        internal const int MinMinutes = 1;
        internal const int MaxMinutes = 60;

        /// <summary>自動保存するか（AppSettings から読む）</summary>
        internal static bool Enabled { get; set; } = DefaultEnabled;

        /// <summary>自動保存の間隔（分。AppSettings から読む）</summary>
        internal static int Minutes { get; set; } = DefaultMinutes;

        /// <summary>自動保存の控えを置くフォルダー</summary>
        internal static string FolderPath => Path.Combine(AppSettings.FolderPath, "AutoSave");

        /// <summary>
        /// 自動保存する 1 つのタブ（キー：Vehicle・Parameters など、元のファイルのフルパス、編集中の中身）
        /// </summary>
        internal sealed record Entry(string Key, string? FullPath, string Text);

        /// <summary>AutoSave.json に書く 1 つの控え（タブのキー・元のファイルの場所・控えのファイル名）</summary>
        internal sealed record InfoFile(string Key, string? FullPath, string SavedFileName);

        /// <summary>
        /// AutoSave.json に書く中身（車両ファイル・保存した日時・書いたアプリの番号と起動した時刻・控えの一覧）
        /// アプリの番号は、同時に起動している別のウィンドウが編集中の控えを、落ちたあとの控えと見間違えないために使う
        /// </summary>
        internal sealed record Info(string VehiclePath, DateTime SavedAt, int ProcessId, DateTime ProcessStartTime, List<InfoFile> Files);

        /// <summary>残った控え（落ちたあとに残ったもの）。Folder は控えのフォルダー</summary>
        internal sealed record Leftover(Info Info, string Folder);

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        // 自動保存の情報のファイル名
        private const string InfoFileName = "AutoSave.json";

        /// <summary>
        /// その車両ファイルの控えを書き直す（前の控えは消して、今の変更があるタブだけ書く）。書けたら true
        /// </summary>
        public static bool Save(string vehiclePath, IReadOnlyList<Entry> entries)
        {
            try
            {
                string folder = FolderOf(vehiclePath);
                Directory.CreateDirectory(folder);
                // 前の控えのうち、保存して変更がなくなったタブのものが残らないよう、先に全部消す
                foreach (string oldFile in Directory.GetFiles(folder)) File.Delete(oldFile);

                List<InfoFile> files = [];
                foreach (Entry entry in entries)
                {
                    string fileName = Path.GetFileName(entry.FullPath) is { Length: > 0 } name ? name : $"{entry.Key}.txt";
                    string savedFileName = $"{entry.Key}_{fileName}";
                    File.WriteAllText(Path.Combine(folder, savedFileName), entry.Text, new UTF8Encoding(true));
                    files.Add(new(entry.Key, entry.FullPath, savedFileName));
                }
                using Process current = Process.GetCurrentProcess();
                Info info = new(vehiclePath, DateTime.Now, current.Id, current.StartTime, files);
                File.WriteAllText(Path.Combine(folder, InfoFileName), JsonSerializer.Serialize(info, JsonOptions));
                OperationHistory.Add($"自動保存: {folder}（{files.Count} 件）");
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                OperationHistory.Add($"自動保存できない: {vehiclePath}（{ex.GetType().Name}: {ex.Message}）");
                return false;
            }
        }

        /// <summary>
        /// その車両ファイルの控えを消す（すべて保存して変更がなくなったとき・普通に閉じたとき）。なければ何もしない
        /// </summary>
        public static void Clear(string? vehiclePath)
        {
            if (string.IsNullOrEmpty(vehiclePath)) return;
            try
            {
                string folder = FolderOf(vehiclePath);
                if (!Directory.Exists(folder)) return;
                Directory.Delete(folder, true);
                OperationHistory.Add($"自動保存の控えを消す: {folder}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                OperationHistory.Add($"自動保存の控えを消せない: {vehiclePath}（{ex.GetType().Name}: {ex.Message}）");
            }
        }

        /// <summary>
        /// その車両ファイルの、落ちたあとに残った控え。ない・読めない・書いたアプリがまだ動いているときは null
        /// </summary>
        public static Leftover? FindLeftover(string vehiclePath)
        {
            try
            {
                return ReadLeftover(FolderOf(vehiclePath));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                OperationHistory.Add($"自動保存の控えを探せない: {vehiclePath}（{ex.GetType().Name}: {ex.Message}）");
                return null;
            }
        }

        /// <summary>
        /// すべての車両ファイルの、落ちたあとに残った控え（新しい順）
        /// </summary>
        public static List<Leftover> FindAllLeftovers()
        {
            List<Leftover> leftovers = [];
            try
            {
                if (!Directory.Exists(FolderPath)) return leftovers;
                foreach (string folder in Directory.GetDirectories(FolderPath))
                {
                    if (ReadLeftover(folder) is Leftover leftover) leftovers.Add(leftover);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                OperationHistory.Add($"自動保存の控えを探せない（{ex.GetType().Name}: {ex.Message}）");
            }
            return [.. leftovers.OrderByDescending(leftover => leftover.Info.SavedAt)];
        }

        /// <summary>
        /// 控えのファイルの中身。読めなければ null
        /// </summary>
        public static string? ReadText(Leftover leftover, InfoFile file)
        {
            try
            {
                string path = Path.Combine(leftover.Folder, file.SavedFileName);
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                OperationHistory.Add($"自動保存の控えを読めない: {file.SavedFileName}（{ex.GetType().Name}: {ex.Message}）");
                return null;
            }
        }

        // フォルダーの AutoSave.json を読む。ない・壊れている・書いたアプリがまだ動いているときは null
        private static Leftover? ReadLeftover(string folder)
        {
            string infoPath = Path.Combine(folder, InfoFileName);
            if (!File.Exists(infoPath)) return null;
            Info? info;
            try
            {
                info = JsonSerializer.Deserialize<Info>(File.ReadAllText(infoPath), JsonOptions);
            }
            catch (JsonException ex)
            {
                OperationHistory.Add($"自動保存の控えの情報が壊れている: {infoPath}（{ex.Message}）");
                return null;
            }
            if (info == null || info.Files == null || info.Files.Count == 0) return null;
            if (IsRunning(info.ProcessId, info.ProcessStartTime)) return null;
            return new(info, folder);
        }

        // 控えを書いたアプリが、まだ動いているか（番号が同じでも、起動した時刻が違えば別のアプリ）
        private static bool IsRunning(int processId, DateTime startTime)
        {
            try
            {
                using Process process = Process.GetProcessById(processId);
                return Math.Abs((process.StartTime - startTime).TotalSeconds) < 1;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                // 動いていない（または調べられない）
                return false;
            }
        }

        // 車両ファイルごとのフォルダー（名前はバックアップと同じ作り方）
        private static string FolderOf(string vehiclePath) => Path.Combine(FolderPath, BackupManager.VehicleFolderName(vehiclePath));
    }
}
