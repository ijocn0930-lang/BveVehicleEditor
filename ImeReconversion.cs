using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;

namespace BveVehicleEditor
{
    /// <summary>
    /// テキストエリア（AvalonEdit）で、日本語入力（IME）の再変換（確定した文字を選ぶか、その上にカーソルを置いて変換キー）を使えるようにする
    /// AvalonEdit は WPF の標準の入力の仕組みを止めて古い形の IME（IMM32）を使うので、IME からの問い合わせ（WM_IME_REQUEST）にアプリが答える必要がある
    /// ・IMR_RECONVERTSTRING：カーソルのある行の文字と、選んでいる範囲を渡す（選んでいなければ範囲を空にして、カーソル周りの言葉を IME に選んでもらう）
    /// ・IMR_CONFIRMRECONVERTSTRING：IME が決めた範囲を選んだ状態にする（確定すると、選んだ文字が新しい文字に置き換わる。Ctrl+Z で戻せる）
    /// 2 行以上にまたがって選んでいるときは答えない（IME に渡すのは 1 行だけ）
    /// 再変換の途中（IME が範囲を決めてから、変換が終わるまで）だけ、Tab で確定・マウスのクリックで取り消し（元の文字のまま）にする（ユーザーの判断）
    /// ふつうに打って変換しているときは、今までどおり IME に任せる
    /// 再変換で確定した文字は、IME から一度にまとめて受け取り（WM_IME_COMPOSITION の GCS_RESULTSTR）、選んでいた文字と 1 回で置き換える
    /// （ふつうは 1 文字ずつ入るので、Ctrl+Z も 1 文字ずつしか戻らなかった。ユーザーの指摘。Ctrl+Z 1 回で元の文字に全部戻る）
    /// </summary>
    internal static class ImeReconversion
    {
        // Windows のメッセージと問い合わせの種類
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_IME_ENDCOMPOSITION = 0x010E;
        private const int WM_IME_COMPOSITION = 0x010F;
        private const int GCS_RESULTSTR = 0x0800;
        private const int WM_IME_REQUEST = 0x0288;
        private const int IMR_RECONVERTSTRING = 0x0004;
        private const int IMR_CONFIRMRECONVERTSTRING = 0x0005;

        // キー（IME が受け取ったキーは VK_PROCESSKEY になり、元のキーは ImmGetVirtualKey で分かる）
        private const int VK_TAB = 0x09;
        private const int VK_PROCESSKEY = 0xE5;

        // IME に変換中の文字を確定・取り消しさせる（ImmNotifyIME の NI_COMPOSITIONSTR と CPS_COMPLETE / CPS_CANCEL）
        private const int NI_COMPOSITIONSTR = 0x0015;
        private const int CPS_COMPLETE = 0x0001;
        private const int CPS_CANCEL = 0x0004;

        [DllImport("imm32.dll")]
        private static extern IntPtr ImmGetContext(IntPtr hWnd);

        [DllImport("imm32.dll")]
        private static extern bool ImmReleaseContext(IntPtr hWnd, IntPtr hIMC);

        [DllImport("imm32.dll")]
        private static extern bool ImmNotifyIME(IntPtr hIMC, int dwAction, int dwIndex, int dwValue);

        [DllImport("imm32.dll")]
        private static extern int ImmGetVirtualKey(IntPtr hWnd);

        [DllImport("imm32.dll", CharSet = CharSet.Unicode)]
        private static extern int ImmGetCompositionStringW(IntPtr hIMC, int dwIndex, byte[]? lpBuf, int dwBufLen);

        // 再変換の途中のウィンドウ（途中でなければ IntPtr.Zero）
        private static IntPtr _reconvertingWindow = IntPtr.Zero;

        // 再変換で置き換える範囲（テキストエリア・文書の何文字目から・元の文字）。確定した文字で置き換えたら null
        private sealed record ReplaceTarget(TextArea TextArea, int Start, string OriginalText);
        private static ReplaceTarget? _replaceTarget;

        // RECONVERTSTRING の作り（4 バイトの数が 8 つ。その後ろに文字を並べる）
        private const int OffsetSize = 0;
        private const int OffsetVersion = 4;
        private const int OffsetStrLen = 8;
        private const int OffsetStrOffset = 12;
        private const int OffsetCompStrLen = 16;
        private const int OffsetCompStrOffset = 20;
        private const int OffsetTargetStrLen = 24;
        private const int OffsetTargetStrOffset = 28;
        private const int HeaderSize = 32;

        /// <summary>
        /// ウィンドウに、IME の問い合わせを受け取る処理をつなぐ（ウィンドウのハンドルができたとき）
        /// </summary>
        public static void Attach(Window window)
        {
            window.SourceInitialized += (s, e) =>
            {
                if (PresentationSource.FromVisual(window) is HwndSource source) source.AddHook(WndProc);
            };
            // Tab は IME が受け取る前に見る（IME が Tab で予測の候補を動かす前に確定させる）
            ComponentDispatcher.ThreadPreprocessMessage += ThreadPreprocessMessage;
            // 再変換の途中にマウスで押したら、取り消して元の文字のままにする（押した場所へのカーソル移動はそのまま行う）
            window.PreviewMouseDown += (s, e) => FinishReconversion(CPS_CANCEL, "マウスで押したので取り消し");
        }

        // 再変換の途中に Tab を押したら、IME に渡さずに確定する（Enter と同じ。タブの文字は入れない）
        private static void ThreadPreprocessMessage(ref MSG msg, ref bool handled)
        {
            if (_reconvertingWindow == IntPtr.Zero || msg.message != WM_KEYDOWN || msg.hwnd != _reconvertingWindow) return;
            int key = (int)msg.wParam.ToInt64();
            if (key == VK_PROCESSKEY) key = ImmGetVirtualKey(msg.hwnd);
            if (key != VK_TAB) return;
            handled = true;
            FinishReconversion(CPS_COMPLETE, "Tab で確定");
        }

        // 再変換の途中なら、IME に変換中の文字を確定（CPS_COMPLETE）・取り消し（CPS_CANCEL）させる
        // 確定するときは、IME がこの中で確定した文字（WM_IME_COMPOSITION）を送ってくるので、送り終わってから途中でなくする
        private static void FinishReconversion(int action, string reason)
        {
            IntPtr hwnd = _reconvertingWindow;
            if (hwnd == IntPtr.Zero) return;
            IntPtr context = ImmGetContext(hwnd);
            if (context != IntPtr.Zero)
            {
                ImmNotifyIME(context, NI_COMPOSITIONSTR, action, 0);
                ImmReleaseContext(hwnd, context);
            }
            _reconvertingWindow = IntPtr.Zero;
            _replaceTarget = null;
            OperationHistory.Add($"再変換: {reason}");
        }

        // 再変換で確定した文字を IME から一度に受け取り、選んでいた文字と 1 回で置き換える（Ctrl+Z 1 回で全部戻る）
        // 受け取ったら true（WPF に渡さないので、1 文字ずつの入力は来ない）。置き換えられないときは false で、今までどおり 1 文字ずつ入る
        private static bool ReplaceWithResult(IntPtr hwnd)
        {
            ReplaceTarget? target = _replaceTarget;
            if (target == null) return false;
            TextDocument document = target.TextArea.Document;
            // 再変換を始めてから文書が変わっていたら（元の文字が同じ場所にない）置き換えない
            if (target.Start + target.OriginalText.Length > document.TextLength || document.GetText(target.Start, target.OriginalText.Length) != target.OriginalText) return false;

            IntPtr context = ImmGetContext(hwnd);
            if (context == IntPtr.Zero) return false;
            string result;
            try
            {
                int byteLength = ImmGetCompositionStringW(context, GCS_RESULTSTR, null, 0);
                if (byteLength <= 0) return false;
                byte[] buffer = new byte[byteLength];
                ImmGetCompositionStringW(context, GCS_RESULTSTR, buffer, byteLength);
                result = System.Text.Encoding.Unicode.GetString(buffer);
            }
            finally
            {
                ImmReleaseContext(hwnd, context);
            }

            _replaceTarget = null;
            document.Replace(target.Start, target.OriginalText.Length, result);
            target.TextArea.ClearSelection();
            target.TextArea.Caret.Offset = target.Start + result.Length;
            OperationHistory.Add($"再変換: 「{target.OriginalText}」を「{result}」に置き換え");
            return true;
        }

        // カーソルのある行と、その中で変換し直す範囲（行の頭から何文字目・何文字）
        private sealed record LineRange(DocumentLine Line, string Text, int CompStart, int CompLength);

        // IME の再変換の問い合わせだけを受け取る（ほかのメッセージは何もしない）
        private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            // 変換が終わったら（確定・取り消し）、再変換の途中ではなくなる（WPF の処理はそのまま続ける）
            if (msg == WM_IME_ENDCOMPOSITION && hwnd == _reconvertingWindow)
            {
                _reconvertingWindow = IntPtr.Zero;
                _replaceTarget = null;
            }
            // 再変換の途中に確定した文字が来たら、まとめて置き換える（置き換えたら WPF に渡さない）
            if (msg == WM_IME_COMPOSITION && hwnd == _reconvertingWindow && (lParam.ToInt64() & GCS_RESULTSTR) != 0)
            {
                if (ReplaceWithResult(hwnd)) handled = true;
                return IntPtr.Zero;
            }
            if (msg != WM_IME_REQUEST) return IntPtr.Zero;
            int request = (int)wParam.ToInt64();
            if (request != IMR_RECONVERTSTRING && request != IMR_CONFIRMRECONVERTSTRING) return IntPtr.Zero;
            // テキストエリアにカーソルがあるときだけ（入力フォームの欄などは WPF が答える）
            if (Keyboard.FocusedElement is not TextArea textArea || textArea.Document == null) return IntPtr.Zero;
            LineRange? range = ReadCaretLine(textArea);
            if (range == null) return IntPtr.Zero;

            handled = true;
            if (request == IMR_RECONVERTSTRING) return Provide(range, lParam);
            IntPtr accepted = Confirm(textArea, range, lParam);
            // 受け入れたら、ここから変換が終わるまでが再変換の途中
            if (accepted != IntPtr.Zero) _reconvertingWindow = hwnd;
            return accepted;
        }

        // カーソルのある行の文字と、選んでいる範囲。選んだ範囲が 2 行以上にまたがるときは null
        private static LineRange? ReadCaretLine(TextArea textArea)
        {
            TextDocument document = textArea.Document;
            DocumentLine line = document.GetLineByOffset(textArea.Caret.Offset);
            string text = document.GetText(line.Offset, line.Length);
            if (textArea.Selection.IsEmpty) return new(line, text, textArea.Caret.Offset - line.Offset, 0);

            ISegment selected = textArea.Selection.SurroundingSegment;
            if (selected.Offset < line.Offset || selected.EndOffset > line.EndOffset) return null;
            return new(line, text, selected.Offset - line.Offset, selected.Length);
        }

        // IMR_RECONVERTSTRING：lParam が 0 なら要る大きさだけ返す。0 でなければ、そこに行の文字と範囲を書いて大きさを返す
        private static IntPtr Provide(LineRange range, IntPtr lParam)
        {
            // 文字の後ろに終わりの印（0）を 1 文字分置く
            int size = HeaderSize + (range.Text.Length + 1) * sizeof(char);
            if (lParam == IntPtr.Zero) return size;

            Marshal.WriteInt32(lParam, OffsetSize, size);
            Marshal.WriteInt32(lParam, OffsetVersion, 0);
            Marshal.WriteInt32(lParam, OffsetStrLen, range.Text.Length);
            Marshal.WriteInt32(lParam, OffsetStrOffset, HeaderSize);
            // 範囲の場所は、文字の頭からのバイト数（1 文字 2 バイト）。選んでいなければ長さ 0 で、IME がカーソル周りの言葉を選ぶ
            Marshal.WriteInt32(lParam, OffsetCompStrLen, range.CompLength);
            Marshal.WriteInt32(lParam, OffsetCompStrOffset, range.CompStart * sizeof(char));
            Marshal.WriteInt32(lParam, OffsetTargetStrLen, range.CompLength);
            Marshal.WriteInt32(lParam, OffsetTargetStrOffset, range.CompStart * sizeof(char));
            char[] chars = [.. range.Text, '\0'];
            Marshal.Copy(chars, 0, lParam + HeaderSize, chars.Length);
            OperationHistory.Add($"再変換: 行 {range.Line.LineNumber} を IME に渡す（{range.CompStart} 文字目から {range.CompLength} 文字）");
            return size;
        }

        // IMR_CONFIRMRECONVERTSTRING：IME が決めた範囲を、テキストエリアで選んだ状態にする。受け入れたら 1
        private static IntPtr Confirm(TextArea textArea, LineRange range, IntPtr lParam)
        {
            if (lParam == IntPtr.Zero) return IntPtr.Zero;
            int start = Marshal.ReadInt32(lParam, OffsetCompStrOffset) / sizeof(char);
            int length = Marshal.ReadInt32(lParam, OffsetCompStrLen);
            // 行の外を指していたら受け入れない
            if (start < 0 || length < 0 || start + length > range.Text.Length) return IntPtr.Zero;

            int documentStart = range.Line.Offset + start;
            // 確定したら、この範囲の文字を 1 回で置き換える（ReplaceWithResult）
            _replaceTarget = new(textArea, documentStart, range.Text.Substring(start, length));
            textArea.Selection = Selection.Create(textArea, documentStart, documentStart + length);
            textArea.Caret.Offset = documentStart + length;
            OperationHistory.Add($"再変換: 行 {range.Line.LineNumber} の {start} 文字目から {length} 文字を選ぶ");
            return 1;
        }
    }
}
