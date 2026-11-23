using System.IO;

namespace BveVehicleEditor
{
    /// <summary>
    /// 例外（エラー）を、画面に出す日本語の文にする共通の処理
    /// Windows や .NET が作る例外の文（ex.Message）は英語になることがあるので、画面には出さない
    /// （英語の詳しい文は、操作履歴とクラッシュログに残す。英語対応はあとで行う）
    /// </summary>
    internal static class ErrorText
    {
        // Windows のエラー番号（IOException.HResult の下 16 ビット）
        private const int ErrorSharingViolation = 32;   // ほかのアプリがファイルを使っている
        private const int ErrorLockViolation = 33;      // ほかのアプリがファイルの一部を使っている
        private const int ErrorHandleDiskFull = 39;     // ディスクがいっぱい
        private const int ErrorDiskFull = 112;          // ディスクの空きが足りない
        private const int ErrorWriteProtect = 19;       // 書き込み禁止のディスク
        private const int ErrorNotReady = 21;           // ドライブの準備ができていない（USB を抜いた、など）

        /// <summary>
        /// 例外を、画面に出す日本語の文にする
        /// アプリが自分で作った日本語の文（ヘッダーが違う、など）は、そのまま使う
        /// </summary>
        internal static string Describe(Exception? exception)
        {
            return exception switch
            {
                null => Loc.T("Err.001"),
                // InvalidDataException はアプリが日本語の文を作って投げている（FileItem のヘッダーのチェックなど）
                InvalidDataException => exception.Message,
                FileNotFoundException notFound => Loc.F("Err.002", FileNameOf(notFound.FileName)),
                DirectoryNotFoundException => Loc.T("Err.003"),
                DriveNotFoundException => Loc.T("Err.004"),
                PathTooLongException => Loc.T("Err.005"),
                UnauthorizedAccessException => Loc.T("Err.006"),
                IOException io => DescribeIOException(io),
                ArgumentException or NotSupportedException => Loc.T("Err.007"),
                OutOfMemoryException => Loc.T("Err.008"),
                _ => Loc.F("Err.009", exception.GetType().Name),
            };
        }

        // ファイルの読み書きのエラーを、Windows のエラー番号で分ける
        private static string DescribeIOException(IOException exception)
        {
            int code = exception.HResult & 0xFFFF;
            return code switch
            {
                ErrorSharingViolation or ErrorLockViolation => Loc.T("Err.010"),
                ErrorHandleDiskFull or ErrorDiskFull => Loc.T("Err.011"),
                ErrorWriteProtect => Loc.T("Err.012"),
                ErrorNotReady => Loc.T("Err.013"),
                _ => Loc.F("Err.014", code),
            };
        }

        // ファイルの名前があれば、次の行に付ける
        private static string FileNameOf(string? fileName)
        {
            return string.IsNullOrEmpty(fileName) ? string.Empty : $"\n{fileName}";
        }
    }
}
