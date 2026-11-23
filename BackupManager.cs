using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace BveVehicleEditor
{
    /// <summary>
    /// 保存で上書きする前に、前の中身を控え（.bak）として残す（「すべて保存」で使う）
    /// 置き場所は %LOCALAPPDATA%\BveVehicleEditor\Backups\＜車両ファイルごとのフォルダー＞\＜ファイル名＞.日時.bak
    /// 1 つのファイルにつき MaxCount 個まで残し、古いものから消す。残せなくても保存は続ける（メッセージは出さず、操作履歴に残す）
    /// </summary>
    internal static class BackupManager
    {
        /// <summary>残す数の範囲（設定のウィンドウと同じ）</summary>
        internal const int MinCount = 1;
        internal const int MaxCountLimit = 100;

        /// <summary>初めの値（控えを残す・10 個）</summary>
        internal const bool DefaultEnabled = true;
        internal const int DefaultMaxCount = 10;

        /// <summary>控えを残すか（AppSettings から読む）</summary>
        internal static bool Enabled { get; set; } = DefaultEnabled;

        /// <summary>1 つのファイルにつき残す数（AppSettings から読む）</summary>
        internal static int MaxCount { get; set; } = DefaultMaxCount;

        /// <summary>控えを置くフォルダー（「ヘルプ → バックアップのフォルダーを開く」でも使う）</summary>
        internal static string FolderPath => Path.Combine(AppSettings.FolderPath, "Backups");

        // 日時の書き方（名前の順に並べると古い順になる）
        private const string TimeFormat = "yyyyMMdd-HHmmss";

        /// <summary>
        /// 今ファイルにある中身を控えとして残し、古い控えを消す
        /// </summary>
        /// <param name="filePath">これから上書きするファイル</param>
        /// <param name="vehiclePath">車両ファイル（控えのフォルダーを分けるのに使う）</param>
        public static void KeepCopy(string filePath, string? vehiclePath)
        {
            if (!Enabled || !File.Exists(filePath)) return;

            try
            {
                string folder = Path.Combine(FolderPath, VehicleFolderName(vehiclePath ?? filePath));
                Directory.CreateDirectory(folder);
                string fileName = Path.GetFileName(filePath);
                string backupPath = Path.Combine(folder, $"{fileName}.{DateTime.Now.ToString(TimeFormat)}.bak");
                // 同じ秒にもう残していれば、その控え（もっと前の中身）をそのまま残す
                if (!File.Exists(backupPath)) File.Copy(filePath, backupPath);
                OperationHistory.Add($"控えを残す: {backupPath}");
                RemoveOldCopies(folder, fileName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                OperationHistory.Add($"控えを残せない: {filePath}（{ex.GetType().Name}: {ex.Message}）");
            }
        }

        // そのファイルの控えが MaxCount 個より多ければ、古いものから消す
        private static void RemoveOldCopies(string folder, string fileName)
        {
            string[] copies = [.. Directory.GetFiles(folder, $"{fileName}.*.bak").OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)];
            foreach (string oldCopy in copies.Skip(MaxCount))
            {
                File.Delete(oldCopy);
                OperationHistory.Add($"古い控えを消す: {oldCopy}");
            }
        }

        // 車両ファイルごとのフォルダーの名前：「フォルダー名_車両ファイル名_場所の印」
        // 同じ名前の車両ファイル（Vehicle.txt など）が混ざらないよう、フルパスから作る 8 文字の印を付ける（自動保存のフォルダーでも使う）
        internal static string VehicleFolderName(string vehiclePath)
        {
            string fullPath = Path.GetFullPath(vehiclePath);
            string parentName = Path.GetFileName(Path.GetDirectoryName(fullPath)) ?? string.Empty;
            string vehicleName = Path.GetFileNameWithoutExtension(fullPath);
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(fullPath.ToLowerInvariant()));
            string mark = Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
            return string.IsNullOrEmpty(parentName) ? $"{vehicleName}_{mark}" : $"{parentName}_{vehicleName}_{mark}";
        }
    }
}
