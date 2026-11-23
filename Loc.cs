using System.Globalization;
using System.Windows;

namespace BveVehicleEditor
{
    /// <summary>
    /// 画面に出す文の言語（日本語・英語）を切り替える
    /// 文は Strings\Strings.ja.xaml・Strings\Strings.en.xaml に「名前 → 文」で書く。XAML は {DynamicResource 名前}（切り替えるとすぐ変わる）、C# は T・F で引く
    /// 英語の一覧に名前がないときは、日本語の一覧の文を出す（日本語の一覧はいつも読み込んでおき、英語の一覧を上に重ねる）
    /// 操作履歴とクラッシュログは日本語のまま（ユーザーの判断）
    /// </summary>
    internal static class Loc
    {
        /// <summary>日本語・英語の名前（AppSettings.Language に覚える値）</summary>
        internal const string Japanese = "ja";
        internal const string English = "en";

        /// <summary>今の言語</summary>
        internal static string Current { get; private set; } = Japanese;

        // 文の一覧の場所（撮影プログラムなど、別の exe から使っても読めるよう、アセンブリの名前を付ける）
        private static Uri DictionaryUri(string language) => new($"pack://application:,,,/BveVehicleEditor;component/Strings/Strings.{language}.xaml");

        /// <summary>
        /// 初めの言語：Windows の表示言語が日本語なら日本語、それ以外は英語
        /// </summary>
        public static string DefaultLanguage() => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == Japanese ? Japanese : English;

        /// <summary>
        /// 言語を当てはめる（覚えていない・知らない値なら Windows に合わせる）。アプリの全部の画面の {DynamicResource} がすぐ変わる
        /// </summary>
        public static void Apply(string? language)
        {
            if (Application.Current == null) return;
            string target = language is Japanese or English ? language : DefaultLanguage();
            var dictionaries = Application.Current.Resources.MergedDictionaries;

            // 日本語の一覧はいつも入れておく（英語に名前がないときの代わり）
            if (!dictionaries.Any(dictionary => IsStrings(dictionary, Japanese))) dictionaries.Insert(0, new ResourceDictionary { Source = DictionaryUri(Japanese) });
            // 英語の一覧は、英語のときだけ上に重ねる
            ResourceDictionary? english = dictionaries.FirstOrDefault(dictionary => IsStrings(dictionary, English));
            if (target == English && english == null) dictionaries.Add(new ResourceDictionary { Source = DictionaryUri(English) });
            if (target == Japanese && english != null) dictionaries.Remove(english);

            Current = target;
            OperationHistory.Add($"言語: {target}");
        }

        // 文の一覧か
        private static bool IsStrings(ResourceDictionary dictionary, string language) =>
            dictionary.Source?.OriginalString.EndsWith($"Strings/Strings.{language}.xaml", StringComparison.OrdinalIgnoreCase) == true;

        /// <summary>
        /// 名前の文（見つからなければ名前そのもの）
        /// </summary>
        public static string T(string key) => Application.Current?.TryFindResource(key) as string ?? key;

        /// <summary>
        /// 名前の文の {0}・{1} に値を入れる
        /// </summary>
        public static string F(string key, params object?[] values) => string.Format(CultureInfo.CurrentCulture, T(key), values);
    }
}
