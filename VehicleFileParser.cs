using System.IO;

namespace BveVehicleEditor
{
    /// <summary>
    /// 車両ファイル（Vehicle.txt）に書かれた1つのファイルパス
    /// </summary>
    /// <param name="Key">キー（Parameters、Panel など。書かれたとおり）</param>
    /// <param name="RawPath">書かれたままのパス（例: Notch\Notch.txt）</param>
    /// <param name="FullPath">車両ファイルの場所を基準にしたフルパス。使えないパスなら null</param>
    /// <param name="Exists">ファイルが見つかったかどうか</param>
    /// <param name="Problem">パスに問題があって探さなかったときの理由（使えない文字など）。問題なければ null</param>
    internal sealed record VehiclePathEntry(string Key, string RawPath, string? FullPath, bool Exists, string? Problem = null);

    /// <summary>
    /// 車両ファイル（Vehicle.txt）の中から「キー = パス」を取り出す
    /// </summary>
    internal static class VehicleFileParser
    {
        // BVE で車両を読み込むのに必ず必要なファイル（キーと、画面に出す名前の Strings の名前）
        // Sound・MotorNoise・Ats32/Ats64・Panel はなくても走れる
        public static readonly (string Key, string NameKey)[] RequiredKeys =
        [
            ("Parameters", "Vehicle.Parameters"),
            ("PerformanceCurve", "Vehicle.Notch"),
        ];

        // 車両ファイルにファイルパスとして書くキー（https://bvets.net/jp/edit/formats/vehicle/vehicle.html の 7 つと、1.00 以前の Ats）
        public static readonly string[] PathKeys = ["PerformanceCurve", "Parameters", "Panel", "Sound", "MotorNoise", "Ats", "Ats32", "Ats64"];

        // BveTs Vehicle 0.03 だけで書けた概要（;[Summary]）のキー。1.00 以降は BveTs Scenario のファイルへ移った（ユーザーのメモ。Bve5.8 では無視される）
        public static readonly string[] SummaryKeys = ["Title", "Author", "Image", "Comment"];

        // この版から ATS プラグインを Ats32 / Ats64 で書く（1.00 以前は Ats。Plan.md のメモ）
        private const decimal FirstVersionWithAts32 = 2.00m;

        // この版から概要（Title など）は書けない
        private const decimal FirstVersionWithoutSummary = 1.00m;

        /// <summary>
        /// 車両ファイルのキーの書き方を、ヘッダーのバージョンと合わせて調べる。警告とお知らせの文を返す（行番号付き）
        /// ・1.00 以降に Title などの概要 → 警告（使われない）。0.03 なら、1.00 以降は BveTs Scenario へ移ったことをお知らせ
        /// ・2.00 に Ats → 警告（Ats32 / Ats64 で書く）。2.00 より前に Ats32 / Ats64 → 警告（Ats で書く）
        /// ・車両ファイルのキーでない → 警告（書き間違いかもしれない）。「=」のない行 → 警告（BVE でエラー）。パスが空の行 → 警告（BVE でエラー）
        /// ・同じキーが 2 回以上 → 警告（BVE でエラーになり読めない）
        /// ・2.00 より前、または Ats32 だけで Ats64 がない → お知らせ（BVE6 では読めない）
        /// ヘッダーのバージョンが読めなければ、バージョンで決まるものは調べない
        /// </summary>
        public static (List<string> Warnings, List<string> Notices) FindKeyProblems(string text)
        {
            List<string> warnings = [];
            List<string> notices = [];
            (decimal? version, string versionText) = ReadVersion(text);

            List<BveTextLine> keyLines = [.. BveTextParser.Parse(text).Where(line => line.Kind == BveLineKind.KeyValue)];
            foreach (BveTextLine line in keyLines)
            {
                string key = line.Key!;
                string where = Loc.F("VP.LineAt", line.LineNumber);
                // パスが空の行。ユーザーが BVE で確かめた：「指定されたファイルが見つかりません。''」のエラーになる（新規作成のファイルはこの形）
                if (line.Value.Length == 0 && PathKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
                    warnings.Add(Loc.F("VP.EmptyPath", key, where));
                if (SummaryKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
                {
                    if (version >= FirstVersionWithoutSummary) warnings.Add(Loc.F("VP.SummaryKey", key, where, versionText));
                    continue;
                }
                if (string.Equals(key, "Ats", StringComparison.OrdinalIgnoreCase))
                {
                    if (version >= FirstVersionWithAts32) warnings.Add(Loc.F("VP.AtsInNew", where, versionText));
                    continue;
                }
                if (string.Equals(key, "Ats32", StringComparison.OrdinalIgnoreCase) || string.Equals(key, "Ats64", StringComparison.OrdinalIgnoreCase))
                {
                    // ユーザーが BVE で確かめた：1.00 に Ats32 / Ats64 を書くと「The key 'ats32' is not valid.」のエラーになる
                    if (version < FirstVersionWithAts32) warnings.Add(Loc.F("VP.Ats32InOld", key, where, versionText, key.ToLowerInvariant()));
                    continue;
                }
                if (!PathKeys.Contains(key, StringComparer.OrdinalIgnoreCase)) warnings.Add(Loc.F("VP.UnknownKey", key, where));
            }

            // 同じキーが 2 回以上。ユーザーが BVE で確かめた：「同じキーのエントリが既に存在します。」のエラーになり読めない
            foreach (IGrouping<string, BveTextLine> group in keyLines.GroupBy(line => line.Key!, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
            {
                string lineNumbers = string.Join(Loc.T("VP.LineSeparator"), group.Select(line => Loc.F("VP.LineAt", line.LineNumber)));
                warnings.Add(Loc.F("VP.DuplicateKey", group.First().Key!, group.Count(), lineNumbers));
            }

            // 「=」のない行（ヘッダーの 1 行目を除く）。ユーザーが BVE で確かめた：「The key '112345' is not valid.」のエラーになる
            foreach (BveTextLine line in BveTextParser.Parse(text).Where(line => line.Kind == BveLineKind.Other && line.LineNumber > 1))
            {
                warnings.Add(Loc.F("VP.NotKeyValue", line.LineNumber, line.Value));
            }

            bool hasSummary = keyLines.Any(line => SummaryKeys.Contains(line.Key!, StringComparer.OrdinalIgnoreCase));
            if (hasSummary && version < FirstVersionWithoutSummary) notices.Add(Loc.T("VP.SummaryMoved"));

            // BVE6 は 64 ビットのアプリなので、2.00 より前（Ats64 を書けない）の車両は読めない（ユーザーが確かめた）
            // 2.00 でも、ATS プラグインを Ats32 だけ書いて Ats64 がなければ、BVE6 では読めない（ユーザーのメモ「Ats64 がないと読めない」）
            bool hasAts32 = keyLines.Any(line => string.Equals(line.Key, "Ats32", StringComparison.OrdinalIgnoreCase));
            bool hasAts64 = keyLines.Any(line => string.Equals(line.Key, "Ats64", StringComparison.OrdinalIgnoreCase));
            if (version < FirstVersionWithAts32) notices.Add(Loc.F("VP.OldNoBve6", versionText));
            else if (hasAts32 && !hasAts64) notices.Add(Loc.T("VP.NoAts64"));
            return (warnings, notices);
        }

        /// <summary>
        /// 古いバージョン（0.03・1.00）の車両ファイルを BveTs Vehicle 2.00 に書き直した結果
        /// </summary>
        /// <param name="Text">書き直したテキスト</param>
        /// <param name="Header">新しいヘッダー</param>
        /// <param name="CommentedSummaryCount">コメントにした Title などの行の数</param>
        /// <param name="RenamedAtsCount">Ats を Ats32 にした行の数</param>
        /// <param name="HasAts64">書き直したあとに Ats64 があるか（なければ BVE6 では読めない）</param>
        /// <param name="NotKeyValueLines">「=」のない行の行番号（BVE でエラーになるが、勝手に直さない）</param>
        internal sealed record UpgradeResult(string Text, string Header, int CommentedSummaryCount, int RenamedAtsCount, bool HasAts64, List<int> NotKeyValueLines);

        /// <summary>
        /// 古いバージョンの車両ファイルを BveTs Vehicle 2.00 に書き直す（画面にはまだ反映しない）
        /// ・1 行目を「BveTs Vehicle 2.00」にする（Shift_JIS のファイルなら「:shift_jis」を付ける。付けないと BVE が UTF-8 として読んで文字化けする）
        /// ・Title・Author・Image・Comment の行は、頭に「; 」を付けてコメントにする（2.00 では使われない。中身は残す。ユーザー判断）
        /// ・「Ats =」は「Ats32 =」にする（古い車両の ATS プラグインはほとんど 32 ビット版。ユーザー判断）。Ats64 は DLL がないので書かない
        /// ・「=」のない行は直さず、行番号を返す
        /// </summary>
        public static UpgradeResult BuildUpgrade(string text, System.Text.Encoding encoding)
        {
            string newLine = text.Contains("\r\n") ? "\r\n" : "\n";
            List<string> lines = [.. text.Split('\n').Select(line => line.TrimEnd('\r'))];
            string header = "BveTs Vehicle 2.00" + (encoding.CodePage == 932 ? ":shift_jis" : string.Empty);
            lines[0] = header;

            int commented = 0;
            int renamed = 0;
            foreach (BveTextLine line in BveTextParser.Parse(text).Where(line => line.Kind == BveLineKind.KeyValue))
            {
                int index = line.LineNumber - 1;
                if (SummaryKeys.Contains(line.Key!, StringComparer.OrdinalIgnoreCase))
                {
                    lines[index] = "; " + lines[index];
                    commented++;
                    continue;
                }
                if (!string.Equals(line.Key, "Ats", StringComparison.OrdinalIgnoreCase)) continue;

                // キーの前の字下げと「=」から後ろ（値・行末のコメント）はそのまま残し、キーだけ Ats32 にする
                lines[index] = System.Text.RegularExpressions.Regex.Replace(lines[index], @"^(\s*)Ats(\s*=)", "${1}Ats32${2}", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                renamed++;
            }

            string newText = string.Join(newLine, lines);
            List<BveTextLine> newLines = BveTextParser.Parse(newText);
            bool hasAts64 = newLines.Any(line => line.Kind == BveLineKind.KeyValue && string.Equals(line.Key, "Ats64", StringComparison.OrdinalIgnoreCase));
            List<int> notKeyValue = [.. newLines.Where(line => line.Kind == BveLineKind.Other && line.LineNumber > 1).Select(line => line.LineNumber)];
            return new(newText, header, commented, renamed, hasAts64, notKeyValue);
        }

        /// <summary>
        /// ヘッダー（1 行目の「BveTs Vehicle 2.00」など）のバージョンを読む。読めなければ null
        /// </summary>
        public static (decimal? Version, string Text) ReadVersion(string text)
        {
            string firstLine = text.TrimStart('﻿').Split('\n')[0].Trim();
            System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(firstLine, @"Vehicle\s+(\d+\.\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!match.Success) return (null, string.Empty);
            return (decimal.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), match.Groups[1].Value);
        }

        /// <summary>
        /// キーが必須ファイルかどうか
        /// </summary>
        public static bool IsRequiredKey(string key)
        {
            return RequiredKeys.Any(required => string.Equals(required.Key, key, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 必須ファイルのうち、書かれていない・見つからないものを知らせる文を返す
        /// </summary>
        public static List<string> FindMissingRequired(List<VehiclePathEntry> entries)
        {
            List<string> messages = [];

            foreach ((string key, string nameKey) in RequiredKeys)
            {
                // 同じキーが何回も書かれていたら、最後の行が使われる
                VehiclePathEntry? entry = entries.LastOrDefault(e => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase));
                if (entry is { Exists: true }) continue;

                // パスに問題があって探さなかったときは、その理由を出す
                string displayName = Loc.T(nameKey);
                string reason = entry?.Problem ?? Loc.T("VP.FileNotFound");
                messages.Add(entry == null
                    ? Loc.F("VP.RequiredNotWritten", displayName, key)
                    : Loc.F("VP.RequiredProblem", displayName, key, entry.RawPath, reason));
            }
            return messages;
        }

        /// <summary>
        /// 車両ファイルの文字列からパスを取り出す
        /// ファイルパスのキー（PathKeys）だけを取り出す。Title などの概要や、知らないキーはファイルとして探さない（FindKeyProblems で知らせる）
        /// </summary>
        /// <param name="text">車両ファイルの中身</param>
        /// <param name="baseDirectory">車両ファイルがあるフォルダー（相対パスの基準）</param>
        public static List<VehiclePathEntry> Parse(string text, string baseDirectory)
        {
            List<VehiclePathEntry> entries = [];

            using var reader = new StringReader(text);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                VehiclePathEntry? entry = ParseLine(line, baseDirectory);
                if (entry == null || !PathKeys.Contains(entry.Key, StringComparer.OrdinalIgnoreCase)) continue;
                entries.Add(entry);
            }

            return entries;
        }

        /// <summary>
        /// 車両ファイルの文字列の中の「キー = パス」を書き換える。キーがなければ最後に1行足す
        /// </summary>
        /// <param name="text">車両ファイルの中身</param>
        /// <param name="key">キー（Parameters など。大文字小文字は区別しない）</param>
        /// <param name="newPath">新しいパス（車両ファイルからの相対パス）</param>
        /// <returns>書き換えたあとの中身</returns>
        public static string SetPath(string text, string key, string newPath)
        {
            // 元の改行コードに合わせる
            string newLine = text.Contains("\r\n") ? "\r\n" : "\n";
            string[] lines = text.Split('\n');

            // 同じキーが複数あるときは、読み込みと同じく最後の行を書き換える
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                bool hasCr = lines[i].EndsWith('\r');
                string line = hasCr ? lines[i][..^1] : lines[i];

                string? replaced = TryReplaceLine(line, key, newPath);
                if (replaced == null) continue;

                lines[i] = hasCr ? replaced + "\r" : replaced;
                return string.Join('\n', lines);
            }

            // キーがなかったので最後に足す（最後が改行で終わっていなければ改行を入れる）
            string separator = text.Length == 0 || text.EndsWith('\n') ? string.Empty : newLine;
            return text + separator + $"{key} = {newPath}" + newLine;
        }

        // 1行がそのキーの行なら、パスを書き換えた行を返す。違えば null。行末のコメントは残す
        private static string? TryReplaceLine(string line, string key, string newPath)
        {
            // コメント（; や #）の見分け方は、ほかのファイルと同じ共通の部品を使う
            int commentIndex = BveTextParser.FindCommentIndex(line);
            string content = commentIndex < 0 ? line : line[..commentIndex];
            string comment = commentIndex < 0 ? string.Empty : line[commentIndex..];

            int equalIndex = content.IndexOf('=');
            if (equalIndex < 0) return null;

            // 字下げやキーの大文字小文字は元のまま残す
            string keyPart = content[..equalIndex].TrimEnd();
            if (!string.Equals(keyPart.Trim(), key, StringComparison.OrdinalIgnoreCase)) return null;

            string commentPart = comment.Length == 0 ? string.Empty : " " + comment;
            return $"{keyPart} = {newPath}{commentPart}";
        }

        // 1行を読む。「キー = パス」でない行（ヘッダー、空行、コメントだけの行）は null
        private static VehiclePathEntry? ParseLine(string line, string baseDirectory)
        {
            string content = BveTextParser.RemoveComment(line);

            int equalIndex = content.IndexOf('=');
            if (equalIndex < 0) return null;

            string key = content[..equalIndex].Trim();
            string rawPath = content[(equalIndex + 1)..].Trim();
            if (key.Length == 0 || rawPath.Length == 0) return null;

            // 使えない文字・長すぎるパスなどは探さない（フルパスは null にして、読み込みも保存もしない）
            string? problem = FindPathProblem(rawPath, baseDirectory);
            if (problem != null) return new VehiclePathEntry(key, rawPath, null, false, problem);

            string? fullPath = TryGetFullPath(baseDirectory, rawPath);
            bool exists = fullPath != null && File.Exists(fullPath);

            return new VehiclePathEntry(key, rawPath, fullPath, exists);
        }

        // Windows のパスで使えない文字（: は「C:\」のドライブ名のところだけ使える）
        private static readonly char[] InvalidPathChars = ['<', '>', '"', '|', '?', '*'];

        // これ以上の長さのフルパスは、BVE やほかのアプリが開けないことがある（Windows の昔からの上限）
        private const int MaxFullPathLength = 260;

        /// <summary>
        /// DLL の種類（PE ヘッダーの Machine で見分ける）
        /// </summary>
        internal enum DllKind { X86, X64, Other, NotDll, Unreadable }

        // DLL の種類の覚え（フルパス → 更新日時と種類。同じ DLL を表示し直すたびに読まないため）
        private static readonly Dictionary<string, (DateTime WriteTime, DllKind Kind)> DllKindCache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// DLL の先頭（PE ヘッダー）を読んで、32 ビット用（x86）か 64 ビット用（x64）かを返す（DLL を読み込んだり動かしたりはしない）
        /// MZ・PE の印がなければ NotDll、x86・x64 以外（ARM64 など）は Other、読めなければ Unreadable
        /// </summary>
        public static DllKind ReadDllKind(string fullPath)
        {
            try
            {
                DateTime writeTime = File.GetLastWriteTimeUtc(fullPath);
                if (DllKindCache.TryGetValue(fullPath, out var cached) && cached.WriteTime == writeTime) return cached.Kind;
                DllKind kind = ReadDllKindFromFile(fullPath);
                DllKindCache[fullPath] = (writeTime, kind);
                return kind;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                OperationHistory.Add($"DLL の種類を読めない: {fullPath}（{ex.GetType().Name}: {ex.Message}）");
                return DllKind.Unreadable;
            }
        }

        // ファイルの先頭の MZ の印 → 0x3C にある PE ヘッダーの場所 → 「PE\0\0」の印 → Machine（2 バイト）
        private static DllKind ReadDllKindFromFile(string fullPath)
        {
            using FileStream stream = new(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using BinaryReader reader = new(stream);
            if (stream.Length < 0x40 || reader.ReadUInt16() != 0x5A4D) return DllKind.NotDll;
            stream.Position = 0x3C;
            int peOffset = reader.ReadInt32();
            if (peOffset <= 0 || peOffset + 6 > stream.Length) return DllKind.NotDll;
            stream.Position = peOffset;
            if (reader.ReadUInt32() != 0x00004550) return DllKind.NotDll;
            return reader.ReadUInt16() switch
            {
                0x014C => DllKind.X86,
                0x8664 => DllKind.X64,
                _ => DllKind.Other,
            };
        }

        /// <summary>
        /// ATS のキーと、指定した DLL の種類が合わないときの理由。合っている・ATS のキーでない・ファイルがないときは null
        /// Ats32 と Ats（2.00 より前の書き方。BVE5 用）は 32 ビット版、Ats64 は 64 ビット版（違うと BVE で Cannot load. になる）
        /// </summary>
        public static string? FindAtsDllProblem(string key, string fullPath)
        {
            bool wants64 = key.Equals("Ats64", StringComparison.OrdinalIgnoreCase);
            bool wants32 = key.Equals("Ats32", StringComparison.OrdinalIgnoreCase) || key.Equals("Ats", StringComparison.OrdinalIgnoreCase);
            if (!wants64 && !wants32) return null;
            if (!File.Exists(fullPath)) return null;

            string name = Path.GetFileName(fullPath);
            string wanted = Loc.T(wants64 ? "VP.Bit64" : "VP.Bit32");
            return ReadDllKind(fullPath) switch
            {
                DllKind.X86 when wants64 => Loc.F("VP.DllIs32", name, key, wanted),
                DllKind.X64 when wants32 => Loc.F("VP.DllIs64", name, key, wanted),
                DllKind.Other => Loc.F("VP.DllOther", name),
                DllKind.NotDll => Loc.F("VP.NotDll", name),
                _ => null,
            };
        }

        /// <summary>
        /// 書かれている ATS（Ats・Ats32・Ats64）の DLL の種類が、キーと合わないものの警告（見つかる DLL だけ調べる）
        /// </summary>
        public static List<string> FindAtsDllProblems(List<VehiclePathEntry> entries)
        {
            List<string> warnings = [];
            foreach (VehiclePathEntry entry in entries)
            {
                if (!entry.Exists || entry.FullPath == null) continue;
                string? problem = FindAtsDllProblem(entry.Key, entry.FullPath);
                if (problem != null) warnings.Add(Loc.F("VP.AtsProblem", entry.Key, entry.RawPath, problem));
            }
            return warnings;
        }

        /// <summary>
        /// 別のキーが同じファイルを指しているものの警告（車両ファイルの中だけ。ノッチ・モーター音・サウンドの中の表や音は調べない）
        /// 特に、ヘッダーのないノッチ（PerformanceCurve）とモーター音（MotorNoise）は、取り違えても読み込めてしまうので気づけるようにする
        /// 同じキーが 2 回のときは FindKeyProblems で警告するので、ここでは別のキーどうしだけ
        /// </summary>
        public static List<string> FindSamePathProblems(List<VehiclePathEntry> entries)
        {
            List<string> warnings = [];
            IEnumerable<IGrouping<string, VehiclePathEntry>> groups = entries
                .Where(entry => entry.FullPath != null && entry.Problem == null)
                .GroupBy(entry => Path.GetFullPath(entry.FullPath!), StringComparer.OrdinalIgnoreCase);
            foreach (IGrouping<string, VehiclePathEntry> group in groups)
            {
                List<string> keys = [.. group.Select(entry => entry.Key).Distinct(StringComparer.OrdinalIgnoreCase)];
                if (keys.Count < 2) continue;
                warnings.Add(Loc.F("VP.SamePath", string.Join(Loc.T("VP.KeyJoin"), keys), group.First().RawPath));
            }
            return warnings;
        }

        /// <summary>
        /// パスに問題があれば理由を返す。問題なければ null
        /// （ASCII 制御文字、使えない文字、名前の最後の空白・ピリオド、長すぎるフルパス）
        /// </summary>
        public static string? FindPathProblem(string rawPath, string baseDirectory)
        {
            foreach (char c in rawPath)
            {
                if (c < 0x20 || c == 0x7F) return Loc.F("VP.ControlChar", (int)c);
                if (InvalidPathChars.Contains(c)) return Loc.F("VP.InvalidChar", c);
            }

            // 「:」はドライブ名（C:\ など）の2文字目だけ使える
            int colonIndex = rawPath.IndexOf(':');
            bool isDriveColon = colonIndex == 1 && char.IsAsciiLetter(rawPath[0]);
            if (colonIndex >= 0 && (!isDriveColon || rawPath.IndexOf(':', colonIndex + 1) >= 0)) return Loc.F("VP.InvalidChar", ':');

            // フォルダー名・ファイル名の最後が空白かピリオドだと、Windows が黙って取り除いてしまい、別のファイルを指すことがある
            foreach (string name in rawPath.Split('\\', '/'))
            {
                if (name is "." or ".." || name.Length == 0) continue;
                if (name.EndsWith(' ') || name.EndsWith('.')) return Loc.F("VP.TrailingSpaceDot", name);
            }

            string? fullPath = TryGetFullPath(baseDirectory, rawPath);
            if (fullPath == null) return Loc.T("VP.PathUnreadable");
            if (fullPath.Length >= MaxFullPathLength) return Loc.F("VP.PathTooLong", fullPath.Length, MaxFullPathLength);

            return null;
        }

        // 車両ファイルの場所を基準にフルパスを作る。使えない文字などで失敗したら null（アプリは落とさない）
        private static string? TryGetFullPath(string baseDirectory, string rawPath)
        {
            try
            {
                return Path.GetFullPath(Path.Combine(baseDirectory, rawPath));
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
