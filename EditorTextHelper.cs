using ICSharpCode.AvalonEdit;

namespace BveVehicleEditor
{
    /// <summary>
    /// テキストエディタ（AvalonEdit）の中身を書き換えるときの共通の処理
    /// （参照ボタン、入力フォーム、文字コードのメニューなどから使う）
    /// </summary>
    internal static class EditorTextHelper
    {
        /// <summary>
        /// テキストエディタの中身を、変わった部分だけ置き換える
        /// （Text = ... で丸ごと入れ替えると、AvalonEdit は「元に戻す」の履歴を消してしまうため）
        /// </summary>
        public static void ReplaceChangedText(TextEditor editor, string newText)
        {
            string oldText = editor.Document.Text;

            // 先頭から同じ文字が続く長さ
            int maxLength = Math.Min(oldText.Length, newText.Length);
            int prefix = 0;
            while (prefix < maxLength && oldText[prefix] == newText[prefix])
            {
                prefix++;
            }

            // 末尾から同じ文字が続く長さ（先頭の一致部分とは重ならないようにする）
            int suffix = 0;
            while (suffix < oldText.Length - prefix
                && suffix < newText.Length - prefix
                && oldText[oldText.Length - 1 - suffix] == newText[newText.Length - 1 - suffix])
            {
                suffix++;
            }

            int removeLength = oldText.Length - prefix - suffix;
            string insertText = newText.Substring(prefix, newText.Length - prefix - suffix);
            if (removeLength == 0 && insertText.Length == 0) return;
            // Document.Replace は「元に戻す」の履歴に残る。カーソルも書き換えた場所へ移す
            editor.Document.Replace(prefix, removeLength, insertText);
            editor.CaretOffset = prefix + insertText.Length;
            editor.ScrollTo(editor.TextArea.Caret.Line, editor.TextArea.Caret.Column);
        }
    }
}
