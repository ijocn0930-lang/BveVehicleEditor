using System.Windows;
using System.Windows.Controls;

namespace BveVehicleEditor
{
    /// <summary>
    /// 入力フォーム（パラメーター・パネル）の数字・文字の欄に共通の処理
    /// 書かれていない欄には「（既定: ○○）」などを薄い斜めの文字で出し、Tag = true を「書かれていない表示中」の印にする
    /// </summary>
    internal static class FormFieldHelper
    {
        /// <summary>
        /// 値を欄に出す。書かれていなければ placeholder（「（既定: ○○）」）を薄い斜めの文字で出す
        /// </summary>
        public static void ShowText(TextBox box, string? value, string placeholder)
        {
            if (value != null)
            {
                box.Text = value;
                ClearPlaceholderStyle(box);
                return;
            }

            box.Text = placeholder;
            box.Tag = true;
            box.SetResourceReference(Control.ForegroundProperty, "TextFillColorTertiaryBrush");
            box.FontStyle = FontStyles.Italic;
        }

        /// <summary>
        /// 欄に「（既定: ○○）」の薄い文字を出しているところか
        /// </summary>
        public static bool IsShowingPlaceholder(TextBox box) => box.Tag is true;

        /// <summary>
        /// 欄に入ったとき：「（既定: ○○）」を出していたら消して、ふつうの文字で打てるようにする
        /// </summary>
        /// <returns>書かれていなかった欄なら true（欄に入ったときの値は null として覚える）</returns>
        public static bool ClearPlaceholder(TextBox box)
        {
            if (!IsShowingPlaceholder(box)) return false;

            box.Text = string.Empty;
            ClearPlaceholderStyle(box);
            return true;
        }

        /// <summary>
        /// 空のまま書き込もうとしたとき：書かれていなかった欄に Enter なら、空のまま打てる状態にしておく
        /// （ここで「（既定: ○○）」を出すと、欄に入ったまま打った文字とつながったり、値と取り違えたりするため）
        /// それ以外は元の表示（値、または「（既定: ○○）」）に戻す
        /// </summary>
        /// <param name="isLeaving">欄から出たとき true、Enter のとき false（欄に入ったまま）</param>
        public static void RestoreEmptyBox(TextBox box, string? original, string placeholder, bool isLeaving)
        {
            if (original == null && !isLeaving)
            {
                box.Text = string.Empty;
                return;
            }
            ShowText(box, original, placeholder);
        }

        // 薄い斜めの文字と「書かれていない表示中」の印を消す
        private static void ClearPlaceholderStyle(TextBox box)
        {
            box.Tag = null;
            box.ClearValue(Control.ForegroundProperty);
            box.ClearValue(Control.FontStyleProperty);
        }
    }
}
