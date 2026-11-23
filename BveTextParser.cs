using System.IO;

namespace BveVehicleEditor
{
    /// <summary>
    /// BVE のテキストファイルの1行の種類
    /// </summary>
    internal enum BveLineKind
    {
        // [Cab] のようなセクション名の行
        Section,
        // 「キー = 値」の行
        KeyValue,
        // それ以外（ヘッダー、CSV の行など）
        Other,
    }

    /// <summary>
    /// BVE のテキストファイルから読み取った1行（コメントと空行は入らない）
    /// </summary>
    /// <param name="LineNumber">行番号（1 から）</param>
    /// <param name="Kind">行の種類</param>
    /// <param name="Section">この行が入っているセクション名（[ ] の中身）。セクションの前なら null。セクションの行ではそのセクション名</param>
    /// <param name="Key">キー（「キー = 値」の行だけ）。それ以外は null</param>
    /// <param name="Value">値（「キー = 値」の行は = の右側、それ以外はコメントを除いた行全体）。前後の空白は取る</param>
    internal sealed record BveTextLine(int LineNumber, BveLineKind Kind, string? Section, string? Key, string Value);

    /// <summary>
    /// パラメーター・パネル・サウンド・ノッチなど、BVE のテキストファイルを読み取る共通の部品
    /// ; と # から後ろはコメントとして無視する
    /// </summary>
    internal static class BveTextParser
    {
        // コメントの始まりを表す文字（ここから後ろは無視する）
        private static readonly char[] CommentChars = [';', '#'];

        // 古い形式「Version 1.0」のパネルでは、# は色（#00ffff など）に使えたのでコメントにしない
        private static readonly char[] SemicolonOnly = [';'];

        /// <summary>
        /// コメント（; や # から後ろ）を取り除く
        /// </summary>
        /// <param name="line">1行</param>
        /// <param name="hashIsComment"># をコメントとみなすか（古い形式のパネルでは false）</param>
        public static string RemoveComment(string line, bool hashIsComment = true)
        {
            int commentIndex = FindCommentIndex(line, hashIsComment);
            return commentIndex < 0 ? line : line[..commentIndex];
        }

        /// <summary>
        /// コメントが始まる位置。コメントがなければ -1
        /// </summary>
        public static int FindCommentIndex(string line, bool hashIsComment = true)
        {
            return line.IndexOfAny(hashIsComment ? CommentChars : SemicolonOnly);
        }

        /// <summary>
        /// 同じ名前のセクションが 2 回以上書かれていれば、お知らせの文を作る（大文字・小文字と空白は区別しない。[Pilot Lamp] ＝ [PilotLamp]）
        /// このアプリは同じ名前のセクションを合わせて読み、同じキーは下の行の値を使う（BVE の動きはページに書かれていない。サンプルのサウンドには分けて書いたものがある）
        /// </summary>
        /// <param name="lines">Parse で読み取った行</param>
        /// <param name="include">知らせるセクションか（null ならすべて。パネルの計器のように何個でも書けるものを除くときに使う）</param>
        /// <param name="messageKey">文の名前（パネルは別々のセクションとして扱うので Common.DuplicateSectionSeparate）</param>
        public static List<string> DescribeDuplicateSections(List<BveTextLine> lines, Func<string, bool>? include = null, string messageKey = "Common.DuplicateSection")
        {
            return [.. lines
                .Where(line => line.Kind == BveLineKind.Section && line.Section != null && (include == null || include(line.Section)))
                .GroupBy(line => NormalizeSectionName(line.Section!))
                .Where(group => group.Count() > 1)
                .Select(group => Loc.F(messageKey, group.First().Section!, group.Count(),
                    string.Join(Loc.T("Common.LineSeparator"), group.Select(line => line.LineNumber))))];
        }

        // セクション名を比べるための形（空白を除いて大文字に）
        private static string NormalizeSectionName(string name) => string.Concat(name.Where(c => !char.IsWhiteSpace(c))).ToUpperInvariant();

        /// <summary>
        /// 公式の一覧にないセクション名とキーを探して、警告の文を作る（書き間違いに気づけるように。大文字・小文字と空白は区別しない）
        /// 同じ名前は 1 つの文にまとめて行番号を並べる。一覧にないセクションの中のキーは調べない
        /// </summary>
        /// <param name="lines">Parse で読み取った行</param>
        /// <param name="knownKeys">公式の一覧（セクション名 → 書けるキー。セクションの前に書くキーは空の名前 ""）</param>
        public static List<string> FindUnknownNames(List<BveTextLine> lines, IReadOnlyDictionary<string, string[]> knownKeys)
        {
            Dictionary<string, HashSet<string>> known = knownKeys.ToDictionary(
                pair => NormalizeSectionName(pair.Key),
                pair => pair.Value.Select(NormalizeSectionName).ToHashSet());
            string separator = Loc.T("Common.LineSeparator");

            // 一覧にないセクション（名前ごとに行番号をまとめる）
            List<string> problems = [.. lines
                .Where(line => line.Kind == BveLineKind.Section && line.Section != null && !known.ContainsKey(NormalizeSectionName(line.Section)))
                .GroupBy(line => NormalizeSectionName(line.Section!))
                .Select(group => Loc.F("Common.UnknownSection", group.First().Section!, string.Join(separator, group.Select(line => line.LineNumber))))];

            // 一覧にあるセクション（と先頭）の中の、一覧にないキー
            problems.AddRange(lines
                .Where(line => line.Kind == BveLineKind.KeyValue && line.Key != null
                    && known.TryGetValue(NormalizeSectionName(line.Section ?? string.Empty), out HashSet<string>? keys)
                    && !keys.Contains(NormalizeSectionName(line.Key)))
                .GroupBy(line => (NormalizeSectionName(line.Section ?? string.Empty), NormalizeSectionName(line.Key!)))
                .Select(group =>
                {
                    BveTextLine first = group.First();
                    string numbers = string.Join(separator, group.Select(line => line.LineNumber));
                    return first.Section == null
                        ? Loc.F("Common.UnknownKeyTop", first.Key!, numbers)
                        : Loc.F("Common.UnknownKey", first.Section, first.Key!, numbers);
                }));
            return problems;
        }

        /// <summary>
        /// テキスト全体を1行ずつ読み取る。コメントだけの行と空行は飛ばす
        /// </summary>
        /// <param name="text">ファイルの中身</param>
        /// <param name="hashIsComment"># をコメントとみなすか（古い形式のパネルでは false）</param>
        public static List<BveTextLine> Parse(string text, bool hashIsComment = true)
        {
            List<BveTextLine> lines = [];
            string? section = null;
            int lineNumber = 0;

            using var reader = new StringReader(text);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                lineNumber++;
                string content = RemoveComment(line, hashIsComment).Trim();
                if (content.Length == 0) continue;

                // [Cab] のようなセクション名
                if (content.StartsWith('[') && content.EndsWith(']'))
                {
                    section = content[1..^1].Trim();
                    lines.Add(new(lineNumber, BveLineKind.Section, section, null, section));
                    continue;
                }

                int equalIndex = content.IndexOf('=');
                if (equalIndex > 0)
                {
                    string key = content[..equalIndex].Trim();
                    string value = content[(equalIndex + 1)..].Trim();
                    lines.Add(new(lineNumber, BveLineKind.KeyValue, section, key, value));
                    continue;
                }

                lines.Add(new(lineNumber, BveLineKind.Other, section, null, content));
            }

            return lines;
        }

        /// <summary>
        /// テキストの中の「キー = 値」の値を書き換える。なければ1行足す
        /// ・同じキーが何回もあれば、読み取りと同じく最後の行を書き換える
        /// ・字下げ・キーの書き方・行末のコメントは残す
        /// ・足すとき：セクションがあればその最後の行の下、section が null ならヘッダーと先頭の項目の下、
        ///   セクションがなければ最後に [セクション] ごと足す
        /// </summary>
        /// <param name="text">ファイルの中身</param>
        /// <param name="section">セクション名（[ ] の中身）。ファイルの先頭（セクションの前）なら null</param>
        /// <param name="key">キー（大文字小文字は区別しない）</param>
        /// <param name="value">新しい値</param>
        /// <param name="hashIsComment"># をコメントとみなすか（古い形式のパネルでは false）</param>
        /// <returns>書き換えたあとの中身</returns>
        public static string SetValue(string text, string? section, string key, string value, bool hashIsComment = true)
        {
            // 元の改行コードに合わせる。行は \r を取り除いて扱い、最後に同じ改行コードでつなぐ
            string newLine = text.Contains("\r\n") ? "\r\n" : "\n";
            List<string> rawLines = text.Split('\n').Select(line => line.TrimEnd('\r')).ToList();
            List<BveTextLine> lines = Parse(text, hashIsComment);

            // すでにある行の値を書き換える
            BveTextLine? target = lines.LastOrDefault(line => line.Kind == BveLineKind.KeyValue
                && string.Equals(line.Section, section, StringComparison.OrdinalIgnoreCase)
                && string.Equals(line.Key, key, StringComparison.OrdinalIgnoreCase));
            if (target != null)
            {
                int index = target.LineNumber - 1;
                rawLines[index] = ReplaceValueInLine(rawLines[index], value, hashIsComment);
                return string.Join(newLine, rawLines);
            }

            // ない場合は足す場所を決める（そのセクションの最後の行。先頭ならヘッダーと先頭の項目の最後の行）
            string newEntry = $"{key} = {value}";
            BveTextLine? anchor = lines.LastOrDefault(line => string.Equals(line.Section, section, StringComparison.OrdinalIgnoreCase));
            if (anchor != null)
            {
                rawLines.Insert(anchor.LineNumber, newEntry);
                return string.Join(newLine, rawLines);
            }

            // ファイルの先頭に書く項目で、何も書かれていない（空のファイル）なら一番上に足す
            if (section == null)
            {
                rawLines.Insert(0, newEntry);
                return string.Join(newLine, rawLines);
            }

            // セクションがないので、最後に [セクション] ごと足す（最後が空行でなければ1行あける）
            int insertIndex = rawLines.Count > 0 && rawLines[^1].Length == 0 ? rawLines.Count - 1 : rawLines.Count;
            List<string> block = [$"[{section}]", newEntry];
            if (insertIndex > 0 && rawLines[insertIndex - 1].Trim().Length > 0) block.Insert(0, string.Empty);
            rawLines.InsertRange(insertIndex, block);
            return string.Join(newLine, rawLines);
        }

        /// <summary>
        /// 同じ名前のセクションが何個もあるとき（パネルの [Needle] など）、occurrence 個目のセクションの中だけで「キー = 値」を書き換える。なければ1行足す
        /// ・そのセクションの中に同じキーが何回もあれば、最後の行を書き換える
        /// ・足すときは、そのセクションの最後の行（コメントと空行は除く）の下
        /// ・字下げ・キーの書き方・行末のコメントは残す
        /// ・occurrence 個目のセクションがなければ、何も変えずに返す
        /// </summary>
        /// <param name="text">ファイルの中身</param>
        /// <param name="section">セクション名（[ ] の中身。大文字小文字は区別しない）</param>
        /// <param name="occurrence">同じ名前のセクションの何個目か（1 から）</param>
        /// <param name="key">キー（大文字小文字は区別しない）</param>
        /// <param name="value">新しい値</param>
        /// <param name="hashIsComment"># をコメントとみなすか（古い形式のパネルでは false）</param>
        public static string SetValueInSection(string text, string section, int occurrence, string key, string value, bool hashIsComment = true)
        {
            string newLine = text.Contains("\r\n") ? "\r\n" : "\n";
            List<string> rawLines = text.Split('\n').Select(line => line.TrimEnd('\r')).ToList();
            List<BveTextLine> lines = Parse(text, hashIsComment);

            // occurrence 個目の [セクション] の行と、次のセクションの行
            List<BveTextLine> sectionLines = lines.Where(line => line.Kind == BveLineKind.Section).ToList();
            List<int> matches = [.. sectionLines.Select((line, i) => (line, i))
                .Where(item => string.Equals(item.line.Section, section, StringComparison.OrdinalIgnoreCase))
                .Select(item => item.i)];
            if (occurrence < 1 || occurrence > matches.Count) return text;

            BveTextLine start = sectionLines[matches[occurrence - 1]];
            int nextIndex = matches[occurrence - 1] + 1;
            int endLineNumber = nextIndex < sectionLines.Count ? sectionLines[nextIndex].LineNumber : int.MaxValue;
            List<BveTextLine> inside = [.. lines.Where(line => line.LineNumber > start.LineNumber && line.LineNumber < endLineNumber)];

            // すでにある行の値を書き換える
            BveTextLine? target = inside.LastOrDefault(line => line.Kind == BveLineKind.KeyValue && string.Equals(line.Key, key, StringComparison.OrdinalIgnoreCase));
            if (target != null)
            {
                int index = target.LineNumber - 1;
                rawLines[index] = ReplaceValueInLine(rawLines[index], value, hashIsComment);
                return string.Join(newLine, rawLines);
            }

            // ない場合は、そのセクションの最後の行（なければ [セクション] の行）の下に足す
            int anchorLineNumber = inside.Count > 0 ? inside[^1].LineNumber : start.LineNumber;
            rawLines.Insert(anchorLineNumber, $"{key} = {value}");
            return string.Join(newLine, rawLines);
        }

        /// <summary>
        /// セクション名を書き換える（例: [Cab] → [OneLeverCab]）。中の「キー = 値」はそのまま
        /// ・oldSection が null か見つからなければ、最後に [newSection] を足す
        /// ・同じセクションが何回もあれば、最初のものだけ書き換える。字下げと行末のコメントは残す
        /// </summary>
        public static string RenameSection(string text, string? oldSection, string newSection, bool hashIsComment = true)
        {
            string newLine = text.Contains("\r\n") ? "\r\n" : "\n";
            List<string> rawLines = text.Split('\n').Select(line => line.TrimEnd('\r')).ToList();

            BveTextLine? target = oldSection == null ? null : Parse(text, hashIsComment).FirstOrDefault(line => line.Kind == BveLineKind.Section
                && string.Equals(line.Section, oldSection, StringComparison.OrdinalIgnoreCase));
            if (target != null)
            {
                int index = target.LineNumber - 1;
                string line = rawLines[index];
                int commentIndex = FindCommentIndex(line, hashIsComment);
                string content = commentIndex < 0 ? line : line[..commentIndex];
                string comment = commentIndex < 0 ? string.Empty : line[commentIndex..];
                // 字下げは残し、[ ] の中だけを入れ替える
                string indent = content[..(content.Length - content.TrimStart().Length)];
                string commentPart = comment.Length == 0 ? string.Empty : " " + comment;
                rawLines[index] = $"{indent}[{newSection}]{commentPart}";
                return string.Join(newLine, rawLines);
            }

            // セクションがないので最後に足す（最後が空行でなければ1行あける）
            int insertIndex = rawLines.Count > 0 && rawLines[^1].Length == 0 ? rawLines.Count - 1 : rawLines.Count;
            List<string> block = [$"[{newSection}]"];
            if (insertIndex > 0 && rawLines[insertIndex - 1].Trim().Length > 0) block.Insert(0, string.Empty);
            rawLines.InsertRange(insertIndex, block);
            return string.Join(newLine, rawLines);
        }

        /// <summary>
        /// セクションを中の行ごと消す（[セクション] の行から、次のセクションの手前まで。最後のセクションなら最後まで）
        /// 同じセクションが何回もあれば全部消す。なければそのまま返す
        /// </summary>
        public static string RemoveSection(string text, string section, bool hashIsComment = true)
        {
            string newLine = text.Contains("\r\n") ? "\r\n" : "\n";
            List<string> rawLines = text.Split('\n').Select(line => line.TrimEnd('\r')).ToList();
            List<BveTextLine> sectionLines = Parse(text, hashIsComment).Where(line => line.Kind == BveLineKind.Section).ToList();

            // 後ろから消す（前から消すと、後ろの行番号がずれるため）
            for (int i = sectionLines.Count - 1; i >= 0; i--)
            {
                if (!string.Equals(sectionLines[i].Section, section, StringComparison.OrdinalIgnoreCase)) continue;

                int start = sectionLines[i].LineNumber - 1;
                // 次のセクションの手前まで。最後のセクションなら、最後の改行（空の要素）を残して最後まで
                bool isLastSection = i + 1 >= sectionLines.Count;
                int end = !isLastSection ? sectionLines[i + 1].LineNumber - 1 : rawLines.Count - (rawLines[^1].Length == 0 ? 1 : 0);
                rawLines.RemoveRange(start, end - start);

                // 最後のセクションを消したとき、手前の空行が最後に余るので1つ消す
                bool isBlankLeftAtEnd = isLastSection && start > 0 && rawLines[start - 1].Trim().Length == 0 && start >= rawLines.Count - 1;
                if (isBlankLeftAtEnd) rawLines.RemoveAt(start - 1);
            }
            return string.Join(newLine, rawLines);
        }

        /// <summary>
        /// 同じ名前のセクションが何個もあるとき（パネルの [Needle] など）、occurrence 個目のセクションだけを消す
        /// ・消すのは [セクション] の行から、そのセクションの最後の行（コメントと空行を除いた最後の行）まで
        ///   （次のセクションの前にある「;---- 速度計 ----」のような区切りのコメントは、次のセクションのものなので残す）
        /// ・消したあとに空行が2つ以上続いたら、1つに詰める
        /// ・occurrence 個目のセクションがなければ、何も変えずに返す
        /// </summary>
        public static string RemoveSectionAt(string text, string section, int occurrence, bool hashIsComment = true)
        {
            string newLine = text.Contains("\r\n") ? "\r\n" : "\n";
            List<string> rawLines = text.Split('\n').Select(line => line.TrimEnd('\r')).ToList();
            List<BveTextLine> lines = Parse(text, hashIsComment);

            List<BveTextLine> sectionLines = lines.Where(line => line.Kind == BveLineKind.Section).ToList();
            List<int> matches = [.. sectionLines.Select((line, i) => (line, i))
                .Where(item => string.Equals(item.line.Section, section, StringComparison.OrdinalIgnoreCase))
                .Select(item => item.i)];
            if (occurrence < 1 || occurrence > matches.Count) return text;

            BveTextLine start = sectionLines[matches[occurrence - 1]];
            int nextIndex = matches[occurrence - 1] + 1;
            int endLineNumber = nextIndex < sectionLines.Count ? sectionLines[nextIndex].LineNumber : int.MaxValue;
            BveTextLine last = lines.LastOrDefault(line => line.LineNumber >= start.LineNumber && line.LineNumber < endLineNumber) ?? start;

            int startIndex = start.LineNumber - 1;
            rawLines.RemoveRange(startIndex, last.LineNumber - start.LineNumber + 1);

            // 消したところの前後が空行どうしなら、空行を詰める（前が空行か先頭で、消したところが空行なら消す）
            while (startIndex < rawLines.Count && rawLines[startIndex].Trim().Length == 0 && (startIndex == 0 || rawLines[startIndex - 1].Trim().Length == 0) && startIndex < rawLines.Count - 1)
            {
                rawLines.RemoveAt(startIndex);
            }

            // 最後のセクションを消したとき、最後に空行が余るので詰める（最後の改行（空の要素）は残す）
            bool isRemovedAtEnd = startIndex >= rawLines.Count - 1;
            while (isRemovedAtEnd && rawLines.Count >= 2 && rawLines[^1].Length == 0 && rawLines[^2].Trim().Length == 0)
            {
                rawLines.RemoveAt(rawLines.Count - 2);
            }
            return string.Join(newLine, rawLines);
        }

        /// <summary>
        /// 指定した行番号（1 から。Parse の LineNumber）の行を消す（セクションの行は残したまま、「キー = 値」の行だけ消すときに使う）
        /// 範囲の外の行番号は無視する。改行コードは元のまま
        /// </summary>
        public static string RemoveLines(string text, IEnumerable<int> lineNumbers)
        {
            string newLine = text.Contains("\r\n") ? "\r\n" : "\n";
            List<string> rawLines = text.Split('\n').Select(line => line.TrimEnd('\r')).ToList();
            // 後ろから消す（前から消すと、後ろの行番号がずれるため）
            foreach (int lineNumber in lineNumbers.Distinct().OrderDescending())
            {
                if (lineNumber < 1 || lineNumber > rawLines.Count) continue;
                rawLines.RemoveAt(lineNumber - 1);
            }
            return string.Join(newLine, rawLines);
        }

        // 「キー = 値 ; コメント」の値だけを入れ替える（= の左側と行末のコメントはそのまま）
        private static string ReplaceValueInLine(string line, string value, bool hashIsComment)
        {
            int commentIndex = FindCommentIndex(line, hashIsComment);
            string content = commentIndex < 0 ? line : line[..commentIndex];
            string comment = commentIndex < 0 ? string.Empty : line[commentIndex..];

            int equalIndex = content.IndexOf('=');
            string commentPart = comment.Length == 0 ? string.Empty : " " + comment;
            return $"{content[..(equalIndex + 1)]} {value}{commentPart}";
        }

        /// <summary>
        /// そのセクション（[Power] など。大文字小文字は区別しない）が1つでもあるか
        /// </summary>
        public static bool HasSection(List<BveTextLine> lines, string sectionName)
        {
            return lines.Any(line => line.Kind == BveLineKind.Section && string.Equals(line.Section, sectionName, StringComparison.OrdinalIgnoreCase));
        }
    }
}
