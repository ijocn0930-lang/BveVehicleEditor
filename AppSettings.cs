using System.IO;
using System.Text.Json;

namespace BveVehicleEditor
{
    /// <summary>
    /// 1 つの車両ファイルの、タブごとのテキストのカーソルの位置
    /// </summary>
    internal sealed class VehicleCaretPositions
    {
        /// <summary>車両ファイルのフルパス</summary>
        public string VehiclePath { get; set; } = string.Empty;

        /// <summary>テキストエディタの名前（VehicleTextEditor など）→ カーソルの位置（何文字目か）</summary>
        public Dictionary<string, int> Offsets { get; set; } = [];
    }

    /// <summary>
    /// アプリを閉じても覚えておく設定（テーマ・ウィンドウの位置と大きさ・最大化・テキストの文字の大きさとフォント・タブの境目の位置・車両ファイルごとのカーソルの位置）
    /// %LOCALAPPDATA%\BveVehicleEditor\settings.json に JSON で書く。読めない・壊れているときは初めの値（null）で、書けないときも画面にメッセージは出さない（操作履歴に残す）
    /// </summary>
    internal sealed class AppSettings
    {
        /// <summary>テーマ（System / Light / Dark。「表示 → テーマ」のメニューの Tag と同じ）</summary>
        public string? Theme { get; set; }

        /// <summary>ウィンドウの左・上・幅・高さ（最大化しているときは、最大化する前の大きさ）</summary>
        public double? WindowLeft { get; set; }
        public double? WindowTop { get; set; }
        public double? WindowWidth { get; set; }
        public double? WindowHeight { get; set; }

        /// <summary>最大化していたか</summary>
        public bool IsMaximized { get; set; }

        /// <summary>テキストエリアの文字の大きさ（Ctrl+マウスホイール・「表示 → フォント…」で変えた大きさ）</summary>
        public double? EditorFontSize { get; set; }

        /// <summary>テキストエリアのフォントの名前（「表示 → フォント…」で選んだもの。例：Consolas）</summary>
        public string? EditorFontFamily { get; set; }

        /// <summary>タブごとの境目（スプリッター）で決まる行の高さ（行の名前 → 高さ。Vehicle は上の参照欄、ほかは下のテキスト）</summary>
        public Dictionary<string, double>? SplitterHeights { get; set; }

        /// <summary>車両ファイルごとの、タブのテキストのカーソルの位置（新しい順。MaxCaretVehicles まで）</summary>
        public List<VehicleCaretPositions>? CaretPositions { get; set; }

        /// <summary>BVE5・BVE6 の exe の場所（「デバッグ → BVE を起動」で初めに選ぶ。まだ選んでいなければ null）</summary>
        public string? Bve5Path { get; set; }
        public string? Bve6Path { get; set; }

        /// <summary>起動時に、最後に開いていた車両ファイル（CaretPositions の先頭）を開くか（「ファイル」メニューのチェック。初めは開かない）</summary>
        public bool OpenLastVehicleOnStartup { get; set; }

        /// <summary>「すべて保存」で上書きする前に、前の中身を控えとして残すか（「ファイル → バックアップの設定…」。初めは残す）</summary>
        public bool KeepBackups { get; set; } = BackupManager.DefaultEnabled;

        /// <summary>1 つのファイルにつき残す控えの数（古いものから消す。初めは 10）</summary>
        public int BackupMaxCount { get; set; } = BackupManager.DefaultMaxCount;

        /// <summary>決まった間隔で、編集中の中身を控えとして自動保存するか（車両ファイルは書き換えない。初めはする）</summary>
        public bool AutoSaveEnabled { get; set; } = AutoSaveManager.DefaultEnabled;

        /// <summary>画面の言語（ja / en。「表示 → 言語」で選ぶ。null なら Windows の表示言語に合わせる）</summary>
        public string? Language { get; set; }

        /// <summary>自動保存の間隔（分。初めは 5）</summary>
        public int AutoSaveMinutes { get; set; } = AutoSaveManager.DefaultMinutes;

        /// <summary>カーソルの位置を覚える車両ファイルの数（古いものから消す。「最近使った車両ファイル」の一覧にも使う）</summary>
        public const int MaxCaretVehicles = 20;

        /// <summary>設定のファイルを置くフォルダー（クラッシュログを書けないときに使うフォルダーと同じ。「ヘルプ → 設定のフォルダーを開く」でも使う）</summary>
        internal static string FolderPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BveVehicleEditor");

        // 設定のファイルの場所
        private static string FilePath => Path.Combine(FolderPath, "settings.json");

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        /// <summary>
        /// 設定を読む。ファイルがない・読めない・壊れているときは、何も入っていない設定を返す
        /// </summary>
        public static AppSettings Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return new();
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                OperationHistory.Add($"設定を読めない（初めの値で開く）: {ex.GetType().Name}: {ex.Message}");
                return new();
            }
        }

        /// <summary>
        /// 設定を書く。書けなくてもメッセージは出さない（終了の邪魔をしないため。操作履歴に残す）
        /// </summary>
        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                OperationHistory.Add($"設定を書けない: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
