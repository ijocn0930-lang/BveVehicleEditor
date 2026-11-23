using System.Configuration;
using System.Data;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace BveVehicleEditor
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        // クラッシュ処理が二重に動かないようにするフラグ（0: 未処理, 1: 処理中）
        private static int _isCrashing = 0;

        public App()
        {
            // プロバイダーを登録する
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            // Fluent テーマを使い、Windows の設定（ライト/ダーク）に合わせる
            // ThemeMode は .NET では「試験的」扱いのため、警告 WPF0001 をここだけ止める
#pragma warning disable WPF0001
            ThemeMode = ThemeMode.System;
#pragma warning restore WPF0001

            // 例外ハンドラを登録する（JIT デバッガが立ち上がるのを防ぐ）
            // UI スレッドで起きた例外
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            // Task の中で起きて誰も受け取らなかった例外
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
            // それ以外のスレッドで起きた例外
            AppDomain.CurrentDomain.UnhandledException += OnCurrentDomainUnhandledException;

            OperationHistory.Add("アプリ起動");
        }

        /// <summary>
        /// 起動するときに渡されたファイル（右クリックの「プログラムから開く」・タスクバーの履歴から開いたとき）。なければ null
        /// MainWindow がウィンドウを出したあとに開く
        /// </summary>
        internal static string? StartupFilePath { get; private set; }

        // 起動したとき（MainWindow を作る前）：渡されたファイルの場所を覚える
        private void App_Startup(object sender, StartupEventArgs e)
        {
            if (e.Args.Length == 0) return;
            StartupFilePath = e.Args[0];
            OperationHistory.Add($"起動時に渡されたファイル: {StartupFilePath}");
        }

        // UI スレッドで起きた例外
        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            // 既定の処理（JIT デバッガ起動）をさせない
            e.Handled = true;
            HandleFatalException(e.Exception, "DispatcherUnhandledException");
        }

        // Task の中で起きて誰も受け取らなかった例外
        private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            // 既定の処理をさせない
            e.SetObserved();
            HandleFatalException(e.Exception, "UnobservedTaskException");
        }

        // それ以外のスレッドで起きた例外
        private void OnCurrentDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            HandleFatalException(e.ExceptionObject as Exception, "CurrentDomainUnhandledException");
        }

        // 共通のクラッシュ処理：メッセージを出してアプリを終了する
        private static void HandleFatalException(Exception? exception, string source)
        {
            // すでに処理中なら何もしない（二重にメッセージを出さない）
            if (Interlocked.Exchange(ref _isCrashing, 1) == 1) return;

            // クラッシュログ（CPU・メモリ・OS・例外）を書き出す
            string? logPath = TryWriteCrashLog(exception, source);

            // メモリ破損や不整合のおそれがあるときは、メッセージを出さずに Windows に終了してもらう
            if (IsCorruptedStateException(exception))
            {
                Environment.FailFast($"BveVehicleEditor: 回復できない例外のため終了します（{source}）", exception);
                return;
            }
            string logMessage = logPath != null
                ? Loc.F("App.002", logPath)
                : Loc.T("App.003");

            // 画面には日本語の文を出す（英語の詳しい文はクラッシュログに残る）
            string detail = ErrorText.Describe(exception);

            MessageBox.Show(
                Loc.T("App.004")
                + Loc.F("App.005", source, detail, logMessage),
                "BveVehicleEditor",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            Environment.Exit(1);
        }

        // クラッシュログを書き出す。ログの処理自体が失敗しても終了処理は続ける
        private static string? TryWriteCrashLog(Exception? exception, string source)
        {
            try
            {
                return CrashLogger.Write(exception, source);
            }
            catch
            {
                return null;
            }
        }

        // メモリ破損や不整合のおそれがある例外かどうか（中に包まれた例外も調べる）
        private static bool IsCorruptedStateException(Exception? exception)
        {
            if (exception == null) return false;

            // メモリ不足、メモリの不正アクセス、ネイティブコードの例外、壊れたプログラム
            if (exception is OutOfMemoryException
                or AccessViolationException
                or SEHException
                or InvalidProgramException)
            {
                return true;
            }

            // Task の例外は AggregateException に複数まとめて包まれている
            if (exception is AggregateException aggregate) return aggregate.InnerExceptions.Any(IsCorruptedStateException);

            return IsCorruptedStateException(exception.InnerException);
        }
    }

}
