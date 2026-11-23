using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace BveVehicleEditor
{
    /// <summary>
    /// クラッシュしたときに PC の情報と例外の内容をログファイルへ書き出す
    /// </summary>
    internal static class CrashLogger
    {
        // ログを保存するフォルダ名
        private const string LogFolderName = "CrashLogs";

        // 情報が取れなかったときに表示する文字
        private const string Unknown = "不明";

        /// <summary>
        /// クラッシュログを書き出して、保存したファイルのパスを返す。保存できなかったら null を返す
        /// </summary>
        public static string? Write(Exception? exception, string source)
        {
            string content = BuildContent(exception, source);
            string fileName = $"crash_{DateTime.Now:yyyyMMdd_HHmmss}.txt";

            // まずアプリと同じ場所に保存し、だめなら LocalAppData に保存する
            foreach (string folder in GetCandidateFolders())
            {
                string? path = TryWriteFile(folder, fileName, content);
                if (path != null) return path;
            }

            return null;
        }

        // ログを保存するフォルダの候補（上から順に試す。「ヘルプ → クラッシュログのフォルダーを開く」でも使う）
        internal static string[] GetCandidateFolders()
        {
            string appFolder = Path.Combine(AppContext.BaseDirectory, LogFolderName);
            string localAppDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BveVehicleEditor",
                LogFolderName);

            return [appFolder, localAppDataFolder];
        }

        // ファイルに書き込む。失敗したら null を返す（クラッシュ処理中なので例外は外に出さない）
        private static string? TryWriteFile(string folder, string fileName, string content)
        {
            try
            {
                Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, fileName);
                File.WriteAllText(path, content, new UTF8Encoding(true));
                return path;
            }
            catch
            {
                return null;
            }
        }

        // ログの中身を組み立てる
        private static string BuildContent(Exception? exception, string source)
        {
            var sb = new StringBuilder();

            sb.AppendLine("BveVehicleEditor クラッシュログ");
            sb.AppendLine($"日時: {DateTime.Now:yyyy/MM/dd HH:mm:ss}");
            sb.AppendLine();

            AppendSection(sb, "アプリ", AppendAppInfo);
            AppendSection(sb, "CPU", AppendCpuInfo);
            AppendSection(sb, "メモリ", AppendMemoryInfo);
            AppendSection(sb, "OS", AppendOsInfo);
            AppendSection(sb, "操作履歴（古い順・最大100件）", AppendOperationHistory);

            sb.AppendLine("[例外]");
            sb.AppendLine($"発生場所: {source}");
            sb.AppendLine(exception?.ToString() ?? "例外の情報がありません");

            return sb.ToString();
        }

        // 見出しをつけて情報を書く。途中で失敗しても他の情報は書けるようにする
        private static void AppendSection(StringBuilder sb, string title, Action<StringBuilder> append)
        {
            sb.AppendLine($"[{title}]");
            try
            {
                append(sb);
            }
            catch (Exception ex)
            {
                sb.AppendLine($"取得できませんでした: {ex.Message}");
            }
            sb.AppendLine();
        }

        // アプリの情報
        private static void AppendAppInfo(StringBuilder sb)
        {
            var version = typeof(CrashLogger).Assembly.GetName().Version;
            sb.AppendLine($"バージョン: {version?.ToString() ?? Unknown}");
            sb.AppendLine($".NET: {RuntimeInformation.FrameworkDescription}");
        }

        // CPU の情報
        private static void AppendCpuInfo(StringBuilder sb)
        {
            const string cpuKey = @"HARDWARE\DESCRIPTION\System\CentralProcessor\0";

            sb.AppendLine($"名前: {ReadRegistryString(cpuKey, "ProcessorNameString").Trim()}");
            // 例: Intel64 Family 6 Model 151 Stepping 2（ファミリーと型番）
            sb.AppendLine($"ファミリー/型番: {ReadRegistryString(cpuKey, "Identifier")}");
            sb.AppendLine($"コア数: {CountPhysicalCores()}");
            sb.AppendLine($"スレッド数: {Environment.ProcessorCount}");
        }

        // メモリの情報
        private static void AppendMemoryInfo(StringBuilder sb)
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (GlobalMemoryStatusEx(ref status))
            {
                sb.AppendLine($"物理メモリ: {FormatBytes(status.TotalPhys)}");
                sb.AppendLine($"空きメモリ: {FormatBytes(status.AvailPhys)}（使用率 {status.MemoryLoad}%）");
                sb.AppendLine($"仮想メモリ（コミット上限）: {FormatBytes(status.TotalPageFile)}");
                sb.AppendLine($"仮想メモリ（空き）: {FormatBytes(status.AvailPageFile)}");
            }
            else
            {
                sb.AppendLine($"システムのメモリ情報: {Unknown}");
            }

            using var process = Process.GetCurrentProcess();
            sb.AppendLine($"アプリ自身（ワーキングセット）: {FormatBytes((ulong)process.WorkingSet64)}");
            sb.AppendLine($"アプリ自身（プライベート）: {FormatBytes((ulong)process.PrivateMemorySize64)}");
            sb.AppendLine($"アプリ自身（仮想）: {FormatBytes((ulong)process.VirtualMemorySize64)}");
        }

        // OS の情報
        private static void AppendOsInfo(StringBuilder sb)
        {
            const string osKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

            Version version = Environment.OSVersion.Version;
            string productName = ReadRegistryString(osKey, "ProductName");
            // Windows 11 でもレジストリには "Windows 10" と書かれているので、ビルド番号で直す
            if (version.Build >= 22000)
            {
                productName = productName.Replace("Windows 10", "Windows 11");
            }

            sb.AppendLine($"OS: {productName} {ReadRegistryString(osKey, "DisplayVersion")}");
            sb.AppendLine($"OS バージョン: {version}");
            sb.AppendLine($"OS アーキテクチャ: {RuntimeInformation.OSArchitecture}");
            sb.AppendLine($"アプリのアーキテクチャ: {RuntimeInformation.ProcessArchitecture}");
        }

        // 操作履歴
        private static void AppendOperationHistory(StringBuilder sb)
        {
            string[] entries = OperationHistory.GetAll();
            if (entries.Length == 0)
            {
                sb.AppendLine("記録なし");
                return;
            }

            foreach (string entry in entries)
            {
                sb.AppendLine(entry);
            }
        }

        // レジストリ（HKEY_LOCAL_MACHINE）から文字列を読む
        private static string ReadRegistryString(string keyPath, string valueName)
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(keyPath);
            return key?.GetValue(valueName) as string ?? Unknown;
        }

        // 物理コア数を数える（取れなかったら "不明"）
        private static string CountPhysicalCores()
        {
            const int relationProcessorCore = 0;
            uint length = 0;

            // 1回目は必要なバッファの大きさを聞く
            GetLogicalProcessorInformationEx(relationProcessorCore, IntPtr.Zero, ref length);
            if (length == 0) return Unknown;

            IntPtr buffer = Marshal.AllocHGlobal((int)length);
            try
            {
                if (!GetLogicalProcessorInformationEx(relationProcessorCore, buffer, ref length)) return Unknown;

                // 1コアにつき1つの情報が並んでいるので、その数を数える
                int count = 0;
                int offset = 0;
                while (offset < length)
                {
                    // 先頭4バイトが種類、次の4バイトがこの情報の大きさ
                    int size = Marshal.ReadInt32(buffer, offset + 4);
                    if (size <= 0) break;
                    count++;
                    offset += size;
                }
                return count.ToString();
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        // バイト数を「GB（MB）」の形にする
        private static string FormatBytes(ulong bytes)
        {
            double mb = bytes / 1024.0 / 1024.0;
            double gb = mb / 1024.0;
            return $"{gb:F2} GB（{mb:N0} MB）";
        }

        // ---- Windows API ----

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatusEx
        {
            public uint Length;
            public uint MemoryLoad;
            public ulong TotalPhys;
            public ulong AvailPhys;
            public ulong TotalPageFile;
            public ulong AvailPageFile;
            public ulong TotalVirtual;
            public ulong AvailVirtual;
            public ulong AvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetLogicalProcessorInformationEx(int relationshipType, IntPtr buffer, ref uint returnedLength);
    }
}
