using System.IO;
using System.Text;

namespace BveVehicleEditor
{
    /// <summary>
    /// 開くときに読み込めなかった1つのファイル（ヘッダー不一致など）
    /// </summary>
    /// <param name="Key">車両ファイルのキー（Parameters など）</param>
    /// <param name="FullPath">読み込もうとしたフルパス</param>
    /// <param name="Message">読み込めなかった理由</param>
    public sealed record FileLoadError(string Key, string? FullPath, string Message);

    public class VehicleProject
    {
        public string BaseDirectory { get; set; } = string.Empty;

        // 各ファイルに期待されるヘッダーを指定
        // バージョン番号（2.01 など）は比べない。古いバージョンのファイルも開けるようにするため
        // （バージョンごとの制限は Plan.md の「バージョンに合わせて設定できる項目を制限する」で行う）
        public FileItem VehicleFile { get; set; } = new("Vehicle.txt", "BveTs Vehicle");
        public FileItem ParamFile { get; set; } = new("Parameters.txt", "BveTs Vehicle Parameters");
        // パネルとサウンドは、古い形式「Version 1.0」も読み込む（開いたあとに知らせる）
        public FileItem PanelFile { get; set; } = new("Panel.txt", "BveTs Instrument Panel", allowsLegacyHeader: true);
        public FileItem NotchFile { get; set; } = new("Notch.txt", ""); // ヘッダーがないので、どうするか考える必要ある
        public FileItem MotorNoiseFile { get; set; } = new("MotorNoise.txt", ""); // ヘッダーがないので、どうするか考える必要ある
        public FileItem SoundFile { get; set; } = new("Sound.txt", "BveTs Vehicle Sound", allowsLegacyHeader: true);

        public bool IsLoaded { get; set; } = false;

        // 開くときに読み込めなかったファイルの一覧（そのタブは空のまま。開いた直後に知らせる）
        public List<FileLoadError> LoadErrors { get; } = [];

        // vehicleFilePath: ダイアログで選んだ車両ファイル（Vehicle_A0.txt など名前は自由）
        public void Load(string vehicleFilePath)
        {
            string? baseDir = Path.GetDirectoryName(vehicleFilePath);
            if (string.IsNullOrEmpty(baseDir))
            {
                throw new ArgumentException($"ファイルの場所が分かりません: {vehicleFilePath}");
            }

            // 途中で失敗したときに、前のプロジェクトのまま保存されないようにする
            IsLoaded = false;
            BaseDirectory = baseDir;
            LoadErrors.Clear();

            // ここでヘッダー不一致による InvalidDataException が飛ぶ可能性があるため、
            // 呼び出し元（UI側）で try-catch して MessageBox で表示する
            // 選んだファイルそのものを Vehicle として読む
            VehicleFile.Load(vehicleFilePath);
            if (!VehicleFile.IsLoaded)
            {
                throw new FileNotFoundException($"車両ファイルが見つかりません: {vehicleFilePath}");
            }

            // 車両ファイルに書かれたパスで、それぞれのファイルを読む（書かれていない・見つからないものは空のまま）
            // 1つ読めなくても、ほかのファイルは開く（読めなかったものは LoadErrors に残す）
            List<VehiclePathEntry> entries = VehicleFileParser.Parse(VehicleFile.TextContent, BaseDirectory);
            LoadOrRecordError(ParamFile, "Parameters", FindFullPath(entries, "Parameters"));
            LoadOrRecordError(PanelFile, "Panel", FindFullPath(entries, "Panel"));
            LoadOrRecordError(NotchFile, "PerformanceCurve", FindFullPath(entries, "PerformanceCurve"));
            LoadOrRecordError(MotorNoiseFile, "MotorNoise", FindFullPath(entries, "MotorNoise"));
            LoadOrRecordError(SoundFile, "Sound", FindFullPath(entries, "Sound"));
            IsLoaded = true;
        }

        // 1つのファイルを読む。ヘッダー不一致・読み取りエラーなら、空のまま理由を覚えておく
        // （FileItem.Load は失敗すると「読み込んでいない」状態のままなので、保存もされない）
        private void LoadOrRecordError(FileItem file, string key, string? fullPath)
        {
            try
            {
                file.Load(fullPath);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                // 画面に出す理由は日本語にする（英語の詳しい文は操作履歴に残す）
                OperationHistory.Add($"読み込みエラー: {key}（{ex.GetType().Name}: {ex.Message}）");
                LoadErrors.Add(new(key, fullPath, ErrorText.Describe(ex)));
            }
        }

        // キー（大文字小文字は区別しない）に対応するフルパスを探す。同じキーが複数あれば最後のものを使う
        private static string? FindFullPath(List<VehiclePathEntry> entries, string key)
        {
            return entries.LastOrDefault(entry => string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase))?.FullPath;
        }

        /// <summary>
        /// キー（Parameters など）に対応するファイルを返す。対応がなければ null
        /// </summary>
        public FileItem? GetFileByKey(string key)
        {
            return key.ToLowerInvariant() switch
            {
                "parameters" => ParamFile,
                "panel" => PanelFile,
                "performancecurve" => NotchFile,
                "motornoise" => MotorNoiseFile,
                "sound" => SoundFile,
                _ => null,
            };
        }

        /// <summary>
        /// キーに対応するファイルを、別のファイル（参照ボタンで選んだもの）に入れ替えて読み込む。
        /// 読み込みに失敗したとき（ヘッダー不一致など）は例外を出し、今のファイルはそのまま残す
        /// </summary>
        public FileItem ReplaceFile(string key, string fullPath)
        {
            FileItem current = GetFileByKey(key) ?? throw new ArgumentException($"知らないキーです: {key}");

            // 新しい入れ物に読み込んでから差し替える（失敗しても今のファイルを壊さないため）
            FileItem newFile = new(current.FileName, current.ExpectedHeader, current.AllowsLegacyHeader);
            newFile.Load(fullPath);
            if (!newFile.IsLoaded)
            {
                throw new FileNotFoundException($"ファイルが見つかりません: {fullPath}");
            }

            SetFileByKey(key, newFile);
            return newFile;
        }

        /// <summary>
        /// キーに対応するファイルを「読み込んでいない」状態にする（車両ファイルからその行が消えたときなど）。
        /// 空にしたファイルは保存されない
        /// </summary>
        public void ClearFile(string key)
        {
            FileItem current = GetFileByKey(key) ?? throw new ArgumentException($"知らないキーです: {key}");
            SetFileByKey(key, new FileItem(current.FileName, current.ExpectedHeader, current.AllowsLegacyHeader));
        }

        /// <summary>
        /// 古い形式「Version 1.0」で読み込んだファイルの一覧（開いたあとに知らせる用）
        /// </summary>
        public List<FileItem> GetLegacyFormatFiles()
        {
            FileItem[] files = [PanelFile, SoundFile];
            return files.Where(file => file.IsLegacyFormat).ToList();
        }

        /// <summary>
        /// ヘッダーの文字コード指定（:utf-8 など）と、実際の文字コードが合わないファイルの一覧（開いたあとに警告する用）
        /// </summary>
        public List<FileItem> GetEncodingWarningFiles()
        {
            FileItem[] files = [VehicleFile, ParamFile, PanelFile, NotchFile, MotorNoiseFile, SoundFile];
            return files.Where(file => file.EncodingWarning != null).ToList();
        }

        /// <summary>
        /// ノッチ・モーター音のうち、[Power] も [Brake] もなかったファイルの一覧（開いたあとに警告する用）
        /// </summary>
        public List<FileItem> GetFilesWithoutPowerBrakeSection()
        {
            FileItem[] files = [NotchFile, MotorNoiseFile];
            return files.Where(file => file.LacksPowerBrakeSection).ToList();
        }

        /// <summary>
        /// 新しく作るファイルの中身（ヘッダーだけ）。知らないキーなら null
        /// ノッチ・モーター音はヘッダーがないので、[Power] と [Brake] のセクションだけ入れる
        /// 車両ファイル（"vehicle"。「ファイル → 新規作成」で使う）は、ヘッダーとパスが空のキーを入れる（パスは参照ボタンで選ぶ）
        /// </summary>
        public static string? GetNewFileTemplate(string key)
        {
            return key.ToLowerInvariant() switch
            {
                "vehicle" => "BveTs Vehicle 2.00\r\n\r\nParameters =\r\nPerformanceCurve =\r\nMotorNoise =\r\nSound =\r\nPanel =\r\nAts32 =\r\nAts64 =\r\n",
                "parameters" => "BveTs Vehicle Parameters 2.01\r\n",
                "panel" => "BveTs Instrument Panel 2.01\r\n",
                "sound" => "BveTs Vehicle Sound 3.01\r\n",
                "performancecurve" or "motornoise" => "[Power]\r\n\r\n[Brake]\r\n",
                _ => null,
            };
        }

        /// <summary>
        /// ヘッダーだけ入れた新しいファイルを作る（UTF-8・BOM なし。フォルダーがなければ一緒に作る）。
        /// すでにファイルがあるときは上書きせず、例外を出す
        /// </summary>
        public static void CreateNewFile(string key, string fullPath)
        {
            string template = GetNewFileTemplate(key) ?? throw new ArgumentException($"知らないキーです: {key}");

            string? directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            // CreateNew：同じ名前のファイルがあれば IOException（間違って上書きしないため）
            using FileStream stream = new(fullPath, FileMode.CreateNew, FileAccess.Write);
            using StreamWriter writer = new(stream, new UTF8Encoding(false));
            writer.Write(template);
        }

        // キーに対応するファイルを差し替える
        private void SetFileByKey(string key, FileItem file)
        {
            switch (key.ToLowerInvariant())
            {
                case "parameters": ParamFile = file; break;
                case "panel": PanelFile = file; break;
                case "performancecurve": NotchFile = file; break;
                case "motornoise": MotorNoiseFile = file; break;
                case "sound": SoundFile = file; break;
            }
        }

        /// <summary>
        /// すべてのファイルを保存する。読み込めて、かつ変更があったファイルだけが書き出される
        /// （FileItem.Save の中で判定）。実際に書き出したファイル名の一覧を返す
        /// </summary>
        public List<string> Save()
        {
            List<string> savedFileNames = [];
            if (!IsLoaded) return savedFileNames;

            FileItem[] files = [VehicleFile, ParamFile, PanelFile, NotchFile, MotorNoiseFile, SoundFile];
            foreach (FileItem file in files)
            {
                // 上書きする前に、前の中身を控えとして残す（書き出すファイルだけ。残せなくても保存は続ける）
                if (file.IsModified && file.FullPath != null) BackupManager.KeepCopy(file.FullPath, VehicleFile.FullPath);
                if (file.Save())
                {
                    savedFileNames.Add(file.FileName);
                }
            }

            return savedFileNames;
        }
    }
}