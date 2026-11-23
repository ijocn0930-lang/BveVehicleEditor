using System.IO;
using System.Text;

namespace BveVehicleEditor
{
    public class FileItem
    {
        public string FileName { get; set; }
        public string TextContent { get; set; } = string.Empty;

        // このファイルが持つべき期待されるヘッダー（例: "BveTs Vehicle"）
        public string ExpectedHeader { get; set; }

        // 古い形式のヘッダー（サウンド・パネルの昔の書き方）
        public const string LegacyHeader = "Version 1.0";

        // 古い形式「Version 1.0」も受け付けるか（サウンド・パネルだけ true）
        public bool AllowsLegacyHeader { get; }

        // 読み込んだファイルが古い形式「Version 1.0」だったか
        public bool IsLegacyFormat { get; private set; }

        // ヘッダーのないファイル（ノッチ・モーター音）に、[Power] も [Brake] もなかったか（警告だけ出す）
        public bool LacksPowerBrakeSection { get; private set; }

        // ヘッダーの後ろ（:utf-8 など。書かれていなければ UTF-8）の文字コードと、実際の文字コードが合わないときの説明
        // 合っているときは null（BVE5/BVE6 はこの指定を見て文字コードを決めるため、違うと文字化けする）
        public string? EncodingWarning { get; private set; }

        // 画面（ステータスバー）に出す文字コード名（UTF-8 / UTF-8（BOM 付き） / Shift_JIS）
        public string EncodingDisplayName => GetEncodingDisplayName(DetectedEncoding);

        // ヘッダーのない種類のファイルか（ExpectedHeader が空 = ノッチ・モーター音）
        public bool IsHeaderless => string.IsNullOrEmpty(ExpectedHeader);

        // 読み込み時に検出された文字コードを保持
        public Encoding DetectedEncoding { get; private set; } = Encoding.UTF8;

        // 読み込んだファイルのフルパス（読めなかったときは null）
        public string? FullPath { get; private set; }

        // ファイルを読み込めたかどうか。読めなかったファイルは保存しない（空のファイルを作らないため）
        public bool IsLoaded { get; private set; }

        // ファイルに書かれている（はずの）中身。読み込んだとき・保存したときに覚える
        // TextContent がこれと同じなら変更なしとみなして、保存を飛ばす
        private string _savedContent = string.Empty;

        // ファイルに保存されている（はずの）文字コード。メニューで文字コードを変えたら、これと比べて未保存とみなす
        private Encoding _savedEncoding = new UTF8Encoding(false);

        public FileItem(string fileName, string expectedHeader = "", bool allowsLegacyHeader = false)
        {
            FileName = fileName;
            ExpectedHeader = expectedHeader;
            AllowsLegacyHeader = allowsLegacyHeader;
        }

        // fullPath: 車両ファイルに書かれたパスから作ったフルパス。書かれていなければ null
        public void Load(string? fullPath)
        {
            // 前に開いたファイルの状態を消す
            FullPath = null;
            IsLoaded = false;
            IsLegacyFormat = false;
            LacksPowerBrakeSection = false;
            EncodingWarning = null;
            TextContent = string.Empty;
            _savedContent = string.Empty;
            DetectedEncoding = new UTF8Encoding(false);
            _savedEncoding = DetectedEncoding;

            if (string.IsNullOrEmpty(fullPath) || !File.Exists(fullPath)) return;

            string path = fullPath;
            FileName = Path.GetFileName(path);

            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var sjis = Encoding.GetEncoding(932);

            string fileText;
            Encoding detectedEnc;
            // 読み込んだそのままのバイト（ヘッダーの文字コード指定と比べるときに使う）
            byte[] bytes = [];

            try
            {
                bytes = File.ReadAllBytes(path);
                if (HasUtf8Bom(bytes))
                {
                    // BOM（先頭の EF BB BF）は文字として読まない。保存時も BOM 付きで書き戻す
                    fileText = new UTF8Encoding(false).GetString(bytes, Utf8Bom.Length, bytes.Length - Utf8Bom.Length);
                    detectedEnc = new UTF8Encoding(true);
                }
                else if (TryDecodeUtf8(bytes, out string utf8Text))
                {
                    fileText = utf8Text;
                    detectedEnc = new UTF8Encoding(false);
                }
                else
                {
                    fileText = sjis.GetString(bytes);
                    detectedEnc = sjis;
                }
            }
            catch
            {
                fileText = File.ReadAllText(path, sjis);
                detectedEnc = sjis;
            }

            // --- ヘッダー検証の実施 ---
            bool isLegacy = false;
            string? encodingWarning = null;
            if (!string.IsNullOrEmpty(ExpectedHeader))
            {
                // 先頭の空白やBOMを除外して先頭行をチェック
                using (var reader = new StringReader(fileText))
                {
                    string firstLine = reader.ReadLine()?.Trim() ?? string.Empty;
                    // 古い形式「Version 1.0」（サウンド・パネルだけ）。読み込んで、開いたあとに知らせる
                    isLegacy = AllowsLegacyHeader && firstLine.StartsWith(LegacyHeader, System.StringComparison.OrdinalIgnoreCase);
                    // ヘッダーの後ろの文字コード指定（:utf-8 など。なければ UTF-8）と、実際の文字コードを比べる
                    // 古い形式は既定の文字コードが分からないので比べない
                    encodingWarning = isLegacy ? null : CheckDeclaredEncoding(firstLine, detectedEnc, bytes);
                    if (!isLegacy && !firstLine.StartsWith(ExpectedHeader, System.StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException(
                            Loc.F("FI.001", FileName) +
                            Loc.F("FI.002", ExpectedHeader) +
                            Loc.F("FI.003", (firstLine.Length == 0 ? Loc.T("FI2.001") : firstLine)) +
                            // ユーザーが BVE5 で確かめた：ヘッダーが違うファイルを指定すると、このエラーになる
                            Loc.T("FI.004"));
                    }
                }
            }

            // ヘッダーのないファイル（ノッチ・モーター音）の取り違えを防ぐ
            bool lacksSection = false;
            if (IsHeaderless)
            {
                CheckNotOtherBveFile(fileText);
                lacksSection = !HasPowerOrBrakeSection(fileText);
            }

            // 検証を通過した場合のみコンテンツとエンコーディングを確定
            IsLegacyFormat = isLegacy;
            LacksPowerBrakeSection = lacksSection;
            EncodingWarning = encodingWarning;
            TextContent = fileText;
            DetectedEncoding = detectedEnc;
            FullPath = path;
            IsLoaded = true;
            _savedContent = fileText;
            _savedEncoding = detectedEnc;
        }

        // メニューで文字コードを変えて、まだ保存していないか
        public bool IsEncodingChanged => !IsSameEncoding(DetectedEncoding, _savedEncoding);

        // 読み込んでから（または前に保存してから）中身か文字コードが変わったか
        public bool IsModified => IsLoaded && (TextContent != _savedContent || IsEncodingChanged);

        // 画面（テキストエディタ）の文字か文字コードが、ファイルに保存されているものと違うか（未保存の * 表示用）
        // 読み込めていないファイルは保存されないので、* を付けない
        public bool HasUnsavedChanges(string editorText) => IsLoaded && (editorText != _savedContent || IsEncodingChanged);

        // 保存するときの文字コードを変える（ファイルに書くのは「すべて保存」のとき）
        public void ChangeEncoding(Encoding encoding)
        {
            DetectedEncoding = encoding;
        }

        // メニューで選べる文字コードを作る（name: utf-8 / utf-8-bom / shift_jis）
        public static Encoding CreateEncoding(string name)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return name switch
            {
                "utf-8-bom" => new UTF8Encoding(true),
                "shift_jis" => Encoding.GetEncoding(932),
                _ => new UTF8Encoding(false),
            };
        }

        /// <summary>
        /// ヘッダー行の文字コード指定を、新しい文字コードに合わせて作り直す。変える必要がなければ null
        /// Shift_JIS なら「:shift_jis」を付け（ほかの指定は置き換える）、UTF-8 なら指定を外す（指定なし＝UTF-8 のため）
        /// 例: "BveTs Vehicle 2.00:shift-jis" → UTF-8 なら "BveTs Vehicle 2.00"
        /// </summary>
        public static string? BuildHeaderWithEncoding(string headerLine, Encoding encoding)
        {
            int colonIndex = headerLine.IndexOf(':');
            string basePart = (colonIndex < 0 ? headerLine : headerLine[..colonIndex]).TrimEnd();
            string suffix = encoding.CodePage == 932 ? ":shift_jis" : string.Empty;

            string newLine = basePart + suffix;
            return newLine == headerLine ? null : newLine;
        }

        // 2つの文字コードが同じか（UTF-8 は BOM の有無も比べる）
        public static bool IsSameEncoding(Encoding a, Encoding b)
        {
            return a.CodePage == b.CodePage && a.GetPreamble().Length == b.GetPreamble().Length;
        }

        /// <summary>
        /// その文字コードで書けない文字があれば説明を返す（例: Shift_JIS にない記号や絵文字）。全部書ければ null
        /// </summary>
        public static string? FindUnencodableText(string text, Encoding encoding)
        {
            // 書けない文字があったら例外を出す設定にした、同じ文字コードで試しに変換する
            Encoding strict = Encoding.GetEncoding(encoding.CodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            try
            {
                strict.GetByteCount(text);
                return null;
            }
            catch (EncoderFallbackException ex)
            {
                string unknown = ex.CharUnknownHigh != '\0' ? $"{ex.CharUnknownHigh}{ex.CharUnknownLow}" : ex.CharUnknown.ToString();
                int line = text[..Math.Min(ex.Index, text.Length)].Count(c => c == '\n') + 1;
                return Loc.F("FI.005", line, unknown);
            }
        }

        /// <summary>
        /// ファイルに書き出す。実際に書き出したら true、飛ばしたら false
        /// </summary>
        public bool Save()
        {
            // 読み込めなかったファイルは書き出さない（空のファイルが作られるのを防ぐ）
            if (!IsLoaded || FullPath == null) return false;

            // 変更がなければ書き出さない（保存を速くするため）
            if (!IsModified) return false;

            File.WriteAllText(FullPath, TextContent, DetectedEncoding);
            _savedContent = TextContent;
            _savedEncoding = DetectedEncoding;
            return true;
        }

        // 1行目が「BveTs ～」なら、パラメーターやサウンドなど別の種類のファイルなので読み込まない
        private void CheckNotOtherBveFile(string fileText)
        {
            using var reader = new StringReader(fileText);
            string firstLine = reader.ReadLine()?.Trim() ?? string.Empty;
            if (!firstLine.StartsWith("BveTs", System.StringComparison.OrdinalIgnoreCase)) return;

            throw new InvalidDataException(
                Loc.F("FI.006", FileName) +
                Loc.F("FI.007", firstLine) +
                Loc.T("FI.008"));
        }

        // [Power] か [Brake] のセクションが1つでもあるか（大文字小文字は区別しない）
        // （共通の部品で読むので、「# [Power]」のようにコメントにしたセクションは数えない）
        private static bool HasPowerOrBrakeSection(string fileText)
        {
            List<BveTextLine> lines = BveTextParser.Parse(fileText);
            return BveTextParser.HasSection(lines, "Power") || BveTextParser.HasSection(lines, "Brake");
        }

        // ヘッダー（例: BveTs Vehicle 2.00:utf-8）の「:」の後ろに書かれた文字コードを、実際の文字コードと比べる
        // 書かれていなければ、BVE の既定の UTF-8 として比べる
        // 合っているときは null、合わないときは説明を返す
        private static string? CheckDeclaredEncoding(string headerLine, Encoding detected, byte[] bytes)
        {
            int colonIndex = headerLine.IndexOf(':');
            // 表のファイル（CSV）は「…2.01:shift_jis,,,,,」のように後ろに「,」が付くので外す
            string declaredName = colonIndex < 0 ? string.Empty : headerLine[(colonIndex + 1)..].Trim().TrimEnd(',').Trim();
            bool isDeclared = declaredName.Length > 0;

            int? declaredCodePage = isDeclared ? GetCodePage(declaredName) : 65001;
            if (declaredCodePage == null) return Loc.F("FI.009", declaredName);

            // 半角英数字だけのファイルは、UTF-8 でも Shift_JIS でも同じ中身なので、合っているとみなす
            if (!bytes.Any(b => b >= 0x80)) return null;
            if (declaredCodePage == detected.CodePage) return null;

            string actualName = GetEncodingDisplayName(detected);
            if (!isDeclared) return Loc.F("FI.010", actualName);
            return Loc.F("FI.011", declaredName, actualName);
        }

        // 文字コード名からコードページ番号を求める。分からなければ null
        // （shift-jis / sjis のような書き方の違いも受け付ける）
        private static int? GetCodePage(string name)
        {
            string normalized = name.ToLowerInvariant().Replace("-", string.Empty).Replace("_", string.Empty);
            if (normalized == "utf8") return 65001;
            if (normalized is "shiftjis" or "sjis" or "cp932" or "windows31j" or "mskanji") return 932;

            try
            {
                return Encoding.GetEncoding(name).CodePage;
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        // 画面に出す文字コード名
        private static string GetEncodingDisplayName(Encoding encoding)
        {
            if (encoding.CodePage == 932) return "Shift_JIS";
            if (encoding.CodePage == 65001) return encoding.GetPreamble().Length > 0 ? Loc.T("Encoding.Utf8Bom") : "UTF-8";
            return encoding.WebName;
        }

        // UTF-8 の BOM（ファイル先頭の目印）
        private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

        // ファイルの先頭に UTF-8 の BOM が付いているか
        private static bool HasUtf8Bom(byte[] bytes)
        {
            return bytes.Length >= Utf8Bom.Length
                && bytes[0] == Utf8Bom[0]
                && bytes[1] == Utf8Bom[1]
                && bytes[2] == Utf8Bom[2];
        }

        private bool TryDecodeUtf8(byte[] bytes, out string text)
        {
            text = string.Empty;
            try
            {
                var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
                text = strictUtf8.GetString(bytes);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}