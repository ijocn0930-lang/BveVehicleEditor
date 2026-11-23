using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Highlighting;
using Microsoft.Win32;

namespace BveVehicleEditor
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        //
        // ユーザーからのお願い if(...)などで早期リターンするときはネストしないこと (if(...) return; if(...) continue;) これにより行数を節約できます
        // 一部はユーザーが修正しました。
        private VehicleProject _project = new();

        // Vehicle タブのテキストを書き換えたあと、参照欄を表示し直すまでの待ち時間用タイマー
        // （1文字打つごとにファイルの有無を調べると重くなるので、打ち終わって少し待ってから1回だけ調べる）
        private readonly DispatcherTimer _pathRefreshTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };

        // タブを自動で読み込み直そうとした最後のパス（キーごと）
        // ヘッダー不一致や「いいえ」を選んだときに、打つたびに同じ確認やエラーが何度も出ないようにする
        private readonly Dictionary<string, string?> _lastAutoLoadPaths = new(StringComparer.OrdinalIgnoreCase);

        public MainWindow()
        {
            InitializeComponent();
            // 前に閉じたときのテーマ・ウィンドウの位置と大きさ・テキストの文字の大きさを当てはめる（AppSettings.cs）
            // 覚えた位置が使えないときは、小さい画面（1366x768 など）でウィンドウがはみ出さないようにする（FitToWorkArea）
            ApplySettings(AppSettings.Load());

            // Vehicle タブのテキストが変わったら（手入力・Ctrl+Z・参照ボタン）、少し待って参照欄を追いかけて変える
            VehicleTextEditor.TextChanged += (s, e) =>
            {
                _pathRefreshTimer.Stop();
                _pathRefreshTimer.Start();
            };
            _pathRefreshTimer.Tick += (s, e) =>
            {
                _pathRefreshTimer.Stop();
                if (!_project.IsLoaded) return;
                // 書かれたパスが変わっていたら（Ctrl+Z・手での書き換えなど）、タブの中身もそのファイルに合わせる
                SyncTabsWithVehiclePaths();
                // タブを読み込み直したあとに表示する（必須の枠線は読み込めたかどうかで変わるため）
                ShowVehiclePaths(writeHistory: false);
            };

            // テキストエディタの色分け（ヘッダー・セクション・キー・数字・コメント）
            // テーマが変わるとエディタの背景色が変わるので、そのたびにライト／ダーク用の色分けに切り替える
            ApplySyntaxHighlighting();
            DependencyPropertyDescriptor.FromProperty(Control.BackgroundProperty, typeof(TextEditor))
                .AddValueChanged(VehicleTextEditor, (s, e) => ApplySyntaxHighlighting());

            // パラメーターの入力フォームを、パラメーターのテキストエディタにつなぐ（テキストが変わるとフォームも表示し直す）
            ParamForm.Attach(ParamTextEditor);

            // パネルの入力フォームも、パネルのテキストエディタにつなぐ（Subject の sap・bp・er のチェックにパラメーターのブレーキ方式を使う）
            // 画像があるか・大きさを調べるのに、パネルファイルの場所も渡す（参照ボタンで替わるので、そのつど読む）
            // 古い形式を 2.01 に書き直すとき、Shift_JIS のファイルならヘッダーに :shift_jis を付けるので、今の文字コードも渡す
            PanelInputForm.Attach(PanelTextEditor, () => ParamTextEditor.Text, () => _project.PanelFile.FullPath, () => _project.PanelFile.DetectedEncoding);

            // ノッチの入力フォームを、ノッチのテキストエディタにつなぐ（表のファイルの相対パスの基準に、ノッチのファイルの場所を渡す）
            NotchInputForm.Attach(NotchTextEditor, () => _project.NotchFile.FullPath, () => ParamTextEditor.Text);

            // モーター音の入力フォームも、モーター音のテキストエディタにつなぐ（表のファイルの相対パスの基準に、モーター音のファイルの場所を渡す）
            // 表の音の列が Sound.txt の [Motor] にあるかを調べるのに、サウンドのテキストも渡す（サウンドのファイルを読み込めていなければ null）
            MotorNoiseInputForm.Attach(MotorNoiseTextEditor, () => _project.MotorNoiseFile.FullPath, () => _project.SoundFile.IsLoaded ? SoundTextEditor.Text : null);

            // サウンドの入力フォームも、サウンドのテキストエディタにつなぐ（音のファイルの相対パスの基準に、サウンドのファイルの場所を渡す）
            // 古い形式を 3.01 に書き直すとき、Shift_JIS のファイルならヘッダーに :shift_jis を付けるので、今の文字コードも渡す
            SoundInputForm.Attach(SoundTextEditor, () => _project.SoundFile.FullPath, () => _project.SoundFile.DetectedEncoding);

            // どのタブのテキストが変わっても、未保存の * を付け直す
            foreach ((_, _, TextEditor editor, _) in GetTabInfos())
            {
                editor.TextChanged += (s, e) => UpdateTabHeaders();
                // Ctrl+マウスホイールで文字を大きく・小さくする（6 つのタブでそろえる）
                editor.PreviewMouseWheel += Editor_PreviewMouseWheel;
                // 右クリックメニューはテキストエリアの文字の大きさを受け継ぐので、開くときにウィンドウのふつうの大きさ（上のメニューと同じ）にそろえる（ユーザーの指摘）
                editor.ContextMenuOpening += (s, e) =>
                {
                    if (s is not TextEditor { ContextMenu: ContextMenu menu } openedEditor) return;
                    menu.FontSize = FontSize;
                    // 「定型文を入れる」の中身を、このタブと今のテキストに合わせて作り直す
                    FillTemplateMenu(openedEditor, menu);
                };
            }

            // 起動した直後は何も開いていないので、6 つのタブを灰色にしておく（開くと UpdateTabHeaders で決め直す）
            UpdateTabEnabled();

            // ウィンドウを小さくしたときに、境目の反対側の行がつぶれないように、境目の高さを縮める
            MainFileTabControl.SizeChanged += (s, e) => FitSelectedSplitterRow();

            // タブを切り替えたら、ステータスバーの文字コードをそのタブのファイルに合わせる
            MainFileTabControl.SelectionChanged += (s, e) =>
            {
                // 中の部品から伝わってきた SelectionChanged は無視する（タブの切り替えだけを見る）
                if (e.OriginalSource != MainFileTabControl) return;
                UpdateEncodingStatus();
                // 切り替えたタブの境目の高さが、今のウィンドウに入りきるようにする（表示が終わってから）
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, FitSelectedSplitterRow);
            };

            // プロジェクトの場所は、「…」で省略されているときだけ吹き出しで全部見せる
            TxtProjectStatus.ToolTipOpening += (s, e) =>
            {
                if (!IsTextTrimmed(TxtProjectStatus)) e.Handled = true;
            };

            // テキストエリアで、日本語入力（IME）の再変換（確定した文字を選ぶか、その上にカーソルを置いて変換キー）を使えるようにする（ImeReconversion.cs）
            ImeReconversion.Attach(this);

            // ウィンドウを閉じる前（×ボタン・終了メニュー・Alt+F4）に、未保存の変更があれば確認する
            Closing += MainWindow_Closing;

            // 起動時に前の車両ファイルを開く（「ファイル → 起動時に前の車両ファイルを開く」にチェックがあるとき）
            ContentRendered += MainWindow_ContentRendered;

            // 終了メニューの配線
            MenuExit.Click += (s, e) =>
            {
                OperationHistory.Add("終了メニュー");
                Close();
            };
        }

        // 6つのテキストエディタに、今のテーマに合った色分けと、カーソルのある行（現在行）の強調の色を当てる
        // 現在行の強調は、ファイルを開いた直後などテキストエリアをクリックしていない（カーソルが点滅していない）ときも、どの行にカーソルがあるか分かるように付ける（ユーザーの要望）
        private void ApplySyntaxHighlighting()
        {
            bool isDark = IsDarkBackground(VehicleTextEditor.Background);
            IHighlightingDefinition definition = BveSyntaxHighlighting.Get(isDark);
            // 文字の色分けが見えにくくならないよう、薄い背景と細い枠にする（ライトは黒、ダークは白をうっすら重ねる）
            Brush lineBackground = new SolidColorBrush(isDark ? Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x12, 0x00, 0x00, 0x00));
            Pen lineBorder = new(new SolidColorBrush(isDark ? Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x24, 0x00, 0x00, 0x00)), 1);
            lineBackground.Freeze();
            lineBorder.Freeze();
            foreach ((_, _, TextEditor editor, _) in GetTabInfos())
            {
                editor.SyntaxHighlighting = definition;
                editor.Options.HighlightCurrentLine = true;
                editor.TextArea.TextView.CurrentLineBackground = lineBackground;
                editor.TextArea.TextView.CurrentLineBorder = lineBorder;
            }
        }

        // テキストエリアの文字の大きさの範囲（XAML の初めの大きさは 13。フォントのウィンドウと同じ）
        private const double MinEditorFontSize = FontWindow.MinSize;
        private const double MaxEditorFontSize = FontWindow.MaxSize;

        // 「表示 → フォント…」：フォントのウィンドウを開き、OK なら 6 つのテキストエリアに当てはめる（閉じるときに覚える）
        private void FontMenu_Click(object sender, RoutedEventArgs e)
        {
            FontWindow window = new(VehicleTextEditor.FontFamily, VehicleTextEditor.FontSize) { Owner = this };
            if (window.ShowDialog() != true || window.SelectedFamily == null) return;
            ChangeEditorFontFamily(window.SelectedFamily);
            ChangeEditorFontSize(window.SelectedSize);
        }

        // 6 つのテキストエリアのフォントをそろえる
        private void ChangeEditorFontFamily(FontFamily family)
        {
            if (string.Equals(family.Source, VehicleTextEditor.FontFamily.Source, StringComparison.OrdinalIgnoreCase)) return;
            foreach ((_, _, TextEditor editor, _) in GetTabInfos()) editor.FontFamily = family;
            OperationHistory.Add($"フォント: {family.Source}");
        }

        // Ctrl+マウスホイール：テキストエリアの文字を 1 ずつ大きく（上へ）・小さく（下へ）する。6 つのタブの大きさをそろえる
        // Ctrl を押していないときは、ふつうのスクロールのまま
        private void Editor_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0 || sender is not TextEditor current) return;
            e.Handled = true;
            ChangeEditorFontSize(current.FontSize + Math.Sign(e.Delta));
        }

        // 6 つのテキストエリアの文字の大きさを、範囲の中にしてそろえる
        private void ChangeEditorFontSize(double size)
        {
            size = Math.Clamp(size, MinEditorFontSize, MaxEditorFontSize);
            // どのタブも同じ大きさなので、Vehicle のテキストエリアで比べる（上限・下限で回し続けても何もしない）
            if (size == VehicleTextEditor.FontSize) return;
            foreach ((_, _, TextEditor editor, _) in GetTabInfos()) editor.FontSize = size;
            OperationHistory.Add($"文字の大きさ: {size}");
        }

        // 右クリックメニューの「定型文を入れる」の中身を作る（EditorTemplates.cs。すでに書かれているものは理由を付けて灰色）
        // 右クリックメニューは 6 つのテキストエリアで同じ見た目（Style）なので、開くたびに作り直す
        private void FillTemplateMenu(TextEditor editor, ContextMenu menu)
        {
            if (menu.Items.OfType<MenuItem>().FirstOrDefault(item => item.Tag as string == "Templates") is not MenuItem templateMenu) return;
            templateMenu.Items.Clear();

            EditorTemplates.TabKind[] kinds = [EditorTemplates.TabKind.Vehicle, EditorTemplates.TabKind.Parameters, EditorTemplates.TabKind.Panel,
                EditorTemplates.TabKind.Notch, EditorTemplates.TabKind.MotorNoise, EditorTemplates.TabKind.Sound];
            int index = Array.FindIndex(GetTabInfos(), info => info.Editor == editor);
            if (index < 0) return;

            int caretLine = editor.Document.GetLineByOffset(editor.CaretOffset).LineNumber;
            foreach (EditorTemplates.Template template in EditorTemplates.Build(kinds[index], editor.Text, caretLine))
            {
                // 名前の「_」がアクセスキーにならないよう、文字は TextBlock で出す
                MenuItem item = new()
                {
                    Header = new TextBlock { Text = template.DisabledReason == null ? template.Label : $"{template.Label}（{template.DisabledReason}）" },
                    IsEnabled = template.DisabledReason == null,
                };
                item.Click += (s, e) => InsertTemplate(editor, template.Text);
                templateMenu.Items.Add(item);
            }
            templateMenu.IsEnabled = templateMenu.Items.Count > 0;
        }

        // 定型文を、カーソルのある行の下に 1 行入れて、カーソルをその行の最後に置く（Ctrl+Z で戻せる）
        // カーソルのある行が空（空白だけ）なら、その行に入れる
        private static void InsertTemplate(TextEditor editor, string text)
        {
            DocumentLine line = editor.Document.GetLineByOffset(editor.CaretOffset);
            string lineText = editor.Document.GetText(line.Offset, line.Length);
            int insertAt;
            if (lineText.Trim().Length == 0)
            {
                editor.Document.Replace(line.Offset, line.Length, text);
                insertAt = line.Offset;
            }
            else
            {
                // 改行の形はファイルに合わせる（なければ CRLF）
                string newLine = editor.Document.Text.Contains("\r\n") || !editor.Document.Text.Contains('\n') ? "\r\n" : "\n";
                editor.Document.Insert(line.EndOffset, newLine + text);
                insertAt = line.EndOffset + newLine.Length;
            }
            editor.TextArea.ClearSelection();
            editor.CaretOffset = insertAt + text.Length;
            editor.TextArea.Caret.BringCaretToView();
            editor.Focus();
            OperationHistory.Add($"定型文を入れる: {editor.Name} に「{text.Trim()}」");
        }

        // 背景が暗い色か（明るさが半分より下ならダークテーマとみなす）
        private static bool IsDarkBackground(Brush? background)
        {
            if (background is not SolidColorBrush solid) return false;
            Color color = solid.Color;
            double brightness = (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255;
            return brightness < 0.5;
        }

        // テキストが欄に入りきらず「…」で省略されているか（文字を全部並べたときの幅と、実際の欄の幅を比べる）
        private static bool IsTextTrimmed(TextBlock textBlock)
        {
            FormattedText formatted = new(
                textBlock.Text,
                System.Globalization.CultureInfo.CurrentUICulture,
                textBlock.FlowDirection,
                new Typeface(textBlock.FontFamily, textBlock.FontStyle, textBlock.FontWeight, textBlock.FontStretch),
                textBlock.FontSize,
                textBlock.Foreground,
                VisualTreeHelper.GetDpi(textBlock).PixelsPerDip);
            return formatted.Width > textBlock.ActualWidth;
        }

        // 覚えた設定を当てはめる：テーマ・テキストの文字の大きさ・ウィンドウの位置と大きさ（使えなければ画面に収める）・最大化
        private void ApplySettings(AppSettings settings)
        {
            // 画面の言語（覚えていなければ Windows の表示言語に合わせる。Loc.cs）
            ApplyLanguage(settings.Language);
            // 車両ファイルごとのカーソルの位置は、開くたびに使うので取っておく（最近使った車両ファイルの一覧にも使う）
            _caretPositions = settings.CaretPositions ?? [];
            // BVE5・BVE6 の場所（「デバッグ → BVE を起動」で使う）
            _bvePaths["5"] = settings.Bve5Path;
            _bvePaths["6"] = settings.Bve6Path;
            MenuOpenLastOnStartup.IsChecked = settings.OpenLastVehicleOnStartup;
            // 保存の前に控えを残すか・残す数（「ファイル → バックアップの設定…」。範囲の外なら初めの値に戻す）
            BackupManager.Enabled = settings.KeepBackups;
            BackupManager.MaxCount = settings.BackupMaxCount is >= BackupManager.MinCount and <= BackupManager.MaxCountLimit ? settings.BackupMaxCount : BackupManager.DefaultMaxCount;
            // 自動保存するか・間隔（同じウィンドウで変える。範囲の外なら初めの値に戻す）
            AutoSaveManager.Enabled = settings.AutoSaveEnabled;
            AutoSaveManager.Minutes = settings.AutoSaveMinutes is >= AutoSaveManager.MinMinutes and <= AutoSaveManager.MaxMinutes ? settings.AutoSaveMinutes : AutoSaveManager.DefaultMinutes;
            RestartAutoSaveTimer();
            // 起動時に前の車両ファイルを開く（ウィンドウが出てから MainWindow_ContentRendered で。見つからなければ何もしない）
            if (settings.OpenLastVehicleOnStartup && _caretPositions.Count > 0 && File.Exists(_caretPositions[0].VehiclePath))
                _openOnStartupPath = _caretPositions[0].VehiclePath;
            // 右クリックの「プログラムから開く」・タスクバーの履歴から起動したときは、渡されたファイルを先に開く（App.StartupFilePath）
            if (App.StartupFilePath != null) _openOnStartupPath = App.StartupFilePath;
            if (settings.Theme != null) ApplyTheme(settings.Theme);
            if (settings.EditorFontSize is double fontSize) ChangeEditorFontSize(fontSize);
            // 覚えたフォントを消した（入っていない）ときは、初めの Consolas のまま
            if (settings.EditorFontFamily is string familyName && FontWindow.IsInstalled(familyName)) ChangeEditorFontFamily(new FontFamily(familyName));
            if (!ApplySavedBounds(settings)) FitToWorkArea();
            if (settings.IsMaximized) WindowState = WindowState.Maximized;
            ApplySplitterHeights(settings.SplitterHeights);
        }

        // 境目（スプリッター）で高さが決まる行（Vehicle は上の参照欄、ほかは下のテキスト）。名前は設定のキーにも使う
        private RowDefinition[] SplitterRows => [RowVehicleForm, RowParamText, RowPanelText, RowNotchText, RowMotorNoiseText, RowSoundText];

        // 覚えた境目の高さを当てはめる（60 以上、ウィンドウの高さから 200 を引いた高さまで。上のフォームがつぶれないように）
        private void ApplySplitterHeights(Dictionary<string, double>? heights)
        {
            if (heights == null) return;
            double max = Math.Max(MinSplitterHeight, Height - 200);
            foreach (RowDefinition row in SplitterRows)
            {
                if (!heights.TryGetValue(row.Name, out double height) || double.IsNaN(height)) continue;
                row.Height = new GridLength(Math.Clamp(height, MinSplitterHeight, max));
            }
        }

        // 今の境目の高さ（境目を動かしていない「中身に合わせる」の行は覚えない）
        private Dictionary<string, double> ReadSplitterHeights()
        {
            // 上限（MaxHeight）で止まっているときは高さの値が実際より大きいままなので、表示されたことのある行は実際の高さを超えない値にする
            return SplitterRows.Where(row => row.Height.IsAbsolute)
                .ToDictionary(row => row.Name, row => Math.Round(row.ActualHeight > 0 ? Math.Min(row.Height.Value, row.ActualHeight) : row.Height.Value, 1));
        }

        // 境目で決まる行の低さの下限（XAML の MinHeight と同じ）
        private const double MinSplitterHeight = 60;

        // 境目の反対側の行（Vehicle は下のテキスト、ほかは上のフォーム）の低さの下限（XAML の MinHeight と同じ）
        private const double MinOppositeRowHeight = 150;

        // 今のタブの境目の高さが、タブの表示部分に入りきらなければ縮める（境目を下へ動かしてからウィンドウを小さくすると、反対側の行がつぶれて見えなくなったため）
        // Grid は中の行に合わせて自分を広げる（はみ出した分は切れる）ので、Grid ではなく、Grid を置いている枠の高さで比べる
        // ウィンドウ（タブ）の大きさが変わったときと、タブを切り替えたときに呼ぶ
        private void FitSelectedSplitterRow()
        {
            if (MainFileTabControl.SelectedItem is not TabItem { Content: Grid grid }) return;
            RowDefinition? row = grid.RowDefinitions.FirstOrDefault(definition => SplitterRows.Contains(definition));
            if (row == null) return;
            if (VisualTreeHelper.GetParent(grid) is not FrameworkElement host || host.ActualHeight <= 0) return;

            // 境目の行（5）と反対側の行の下限を残す
            double available = host.ActualHeight - grid.Margin.Top - grid.Margin.Bottom;
            double max = Math.Max(MinSplitterHeight, available - 5 - MinOppositeRowHeight);
            // ドラッグでもこれより高くできないように上限を付ける（Vehicle の参照欄は Grid ごと広がって、ウィンドウの外までドラッグできたため）
            row.MaxHeight = max;
            if (!row.Height.IsAbsolute || row.Height.Value <= max) return;
            row.Height = new GridLength(max);
        }

        // 車両ファイルごとの、タブのテキストのカーソルの位置（新しい順。設定から読み、閉じるときに書く）
        private List<VehicleCaretPositions> _caretPositions = [];

        // 今の車両ファイルの、6 つのタブのカーソルの位置を覚える（別の車両ファイルを開く前と、閉じるときに呼ぶ）
        private void RememberCaretPositions()
        {
            if (!_project.IsLoaded || string.IsNullOrEmpty(_project.VehicleFile.FullPath)) return;
            string path = _project.VehicleFile.FullPath;
            _caretPositions.RemoveAll(item => string.Equals(item.VehiclePath, path, StringComparison.OrdinalIgnoreCase));
            _caretPositions.Insert(0, new VehicleCaretPositions
            {
                VehiclePath = path,
                Offsets = GetTabInfos().ToDictionary(info => info.Editor.Name, info => info.Editor.CaretOffset),
            });
            // 古いものから消す
            if (_caretPositions.Count > AppSettings.MaxCaretVehicles) _caretPositions.RemoveRange(AppSettings.MaxCaretVehicles, _caretPositions.Count - AppSettings.MaxCaretVehicles);
        }

        // 開いた車両ファイルの、覚えたカーソルの位置へ戻す（文の長さを超えていたら最後。その行が見えるようにスクロール）
        private void RestoreCaretPositions()
        {
            string? path = _project.VehicleFile.FullPath;
            VehicleCaretPositions? saved = _caretPositions.FirstOrDefault(item => string.Equals(item.VehiclePath, path, StringComparison.OrdinalIgnoreCase));
            if (saved == null) return;

            foreach ((_, _, TextEditor editor, _) in GetTabInfos())
            {
                if (!saved.Offsets.TryGetValue(editor.Name, out int offset)) continue;
                editor.CaretOffset = Math.Clamp(offset, 0, editor.Document.TextLength);
                // 見えていないタブは大きさが決まっていないので、表示が終わってからスクロールする
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => editor.TextArea.Caret.BringCaretToView());
            }
            OperationHistory.Add($"カーソルの位置を戻す: {path}");
        }

        // 覚えた位置と大きさを当てはめる。覚えていない・今つながっている画面の外（モニターを外したなど）なら使わず false
        private bool ApplySavedBounds(AppSettings settings)
        {
            if (settings.WindowLeft is not double left || settings.WindowTop is not double top
                || settings.WindowWidth is not double width || settings.WindowHeight is not double height) return false;

            // タイトルバーのあたり（上の 40）が、横 100 以上、今の画面のどこかに入っていなければ使わない（つかんで動かせなくなるため）
            Rect screens = new(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            Rect titleBar = Rect.Intersect(screens, new Rect(left, top, Math.Max(width, MinWidth), 40));
            if (titleBar.IsEmpty || titleBar.Width < 100) return false;

            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
            Width = Math.Max(width, MinWidth);
            Height = Math.Max(height, MinHeight);
            return true;
        }

        // 閉じるときに、今のテーマ・ウィンドウの位置と大きさ（最大化しているときは最大化する前）・テキストの文字の大きさを書く
        private void SaveSettings()
        {
            Rect bounds = WindowState == WindowState.Normal || RestoreBounds.IsEmpty ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            RememberCaretPositions();
            new AppSettings
            {
                Theme = MenuThemeLight.IsChecked ? "Light" : MenuThemeDark.IsChecked ? "Dark" : "System",
                WindowLeft = bounds.Left,
                WindowTop = bounds.Top,
                WindowWidth = bounds.Width,
                WindowHeight = bounds.Height,
                IsMaximized = WindowState == WindowState.Maximized,
                EditorFontSize = VehicleTextEditor.FontSize,
                EditorFontFamily = VehicleTextEditor.FontFamily.Source,
                SplitterHeights = ReadSplitterHeights(),
                CaretPositions = _caretPositions,
                OpenLastVehicleOnStartup = MenuOpenLastOnStartup.IsChecked,
                Bve5Path = _bvePaths.GetValueOrDefault("5"),
                Bve6Path = _bvePaths.GetValueOrDefault("6"),
                KeepBackups = BackupManager.Enabled,
                BackupMaxCount = BackupManager.MaxCount,
                AutoSaveEnabled = AutoSaveManager.Enabled,
                AutoSaveMinutes = AutoSaveManager.Minutes,
                Language = _chosenLanguage,
            }.Save();
        }

        // ウィンドウが画面（タスクバーを除いた範囲）より大きければ、収まるように縮める
        private void FitToWorkArea()
        {
            Rect workArea = SystemParameters.WorkArea;

            // 画面に収まっていれば何もしない（大きい画面では XAML のサイズのまま）
            if (Height <= workArea.Height && Width <= workArea.Width) return;
            Height = Math.Min(Height, workArea.Height);
            Width = Math.Min(Width, workArea.Width);

            // 上にはみ出さないよう、画面の一番上・左右中央に置く
            WindowStartupLocation = WindowStartupLocation.Manual;
            Top = workArea.Top;
            Left = workArea.Left + (workArea.Width - Width) / 2;
        }

        // タブ・タブの名前・テキストエディタ・ファイルの組（未保存の * 表示用）
        private (TabItem Tab, string Title, TextEditor Editor, FileItem File)[] GetTabInfos()
        {
            return
            [
                (TabVehicle, Loc.T("Tab.Vehicle"), VehicleTextEditor, _project.VehicleFile),
                (TabParam, Loc.T("Tab.Parameters"), ParamTextEditor, _project.ParamFile),
                (TabPanel, Loc.T("Tab.Panel"), PanelTextEditor, _project.PanelFile),
                (TabNotch, Loc.T("Tab.Notch"), NotchTextEditor, _project.NotchFile),
                (TabMotorNoise, Loc.T("Tab.MotorNoise"), MotorNoiseTextEditor, _project.MotorNoiseFile),
                (TabSound, Loc.T("Tab.Sound"), SoundTextEditor, _project.SoundFile),
            ];
        }

        // 保存していない変更があるタブは、名前の右上に小さな * を出す（Tag = true。MainWindow.xaml の TabItem の見た目で重ねる）
        // マウスを乗せると「保存していない変更があります」と出す。タイトルバーも一緒に付け直す
        private void UpdateTabHeaders()
        {
            bool hasUnsaved = false;
            foreach ((TabItem tab, string title, TextEditor editor, FileItem file) in GetTabInfos())
            {
                bool isUnsaved = file.HasUnsavedChanges(editor.Text);
                tab.Header = title;
                tab.Tag = isUnsaved;
                tab.ToolTip = isUnsaved ? Loc.F("Tip.TabUnsaved", title) : null;
                hasUnsaved |= isUnsaved;
            }

            UpdateTabEnabled();
            UpdateWindowTitle(hasUnsaved);
            // 開いた・読み込み直した・空にしたときも、ここを通るので文字コードの表示も合わせる
            UpdateEncodingStatus();
        }

        // ファイルを読み込めていないタブは、上の入力フォーム（Vehicle は参照欄）と下のテキストを灰色にして、打てないようにする（ユーザーの指摘）
        // 読み込めていないファイルは保存されないので、打っても消えてしまうため。Vehicle は車両ファイルを開く前（起動した直後など）が灰色
        // 参照ボタン・読み込み直し・開く・新規作成のあとも、テキストが変わって UpdateTabHeaders を通るので、ここで決め直す
        private void UpdateTabEnabled()
        {
            (FrameworkElement Form, TextEditor Editor, bool IsLoaded)[] tabs =
            [
                (GrpVehicleForm, VehicleTextEditor, _project.IsLoaded && _project.VehicleFile.IsLoaded),
                (ParamForm, ParamTextEditor, _project.ParamFile.IsLoaded),
                (PanelInputForm, PanelTextEditor, _project.PanelFile.IsLoaded),
                (NotchInputForm, NotchTextEditor, _project.NotchFile.IsLoaded),
                (MotorNoiseInputForm, MotorNoiseTextEditor, _project.MotorNoiseFile.IsLoaded),
                (SoundInputForm, SoundTextEditor, _project.SoundFile.IsLoaded),
            ];
            foreach ((FrameworkElement form, TextEditor editor, bool isLoaded) in tabs)
            {
                form.IsEnabled = isLoaded;
                // テキストは囲みの枠（GroupBox）ごと灰色にする（テキストエディタだけでは見た目が変わらないため、薄くもする）
                if (editor.Parent is UIElement frame) frame.IsEnabled = isLoaded;
                editor.Opacity = isLoaded ? 1.0 : 0.5;
            }
        }

        // 今開いているタブの組（タブ・名前・テキストエディタ・ファイル）。見つからなければ中身が null の組
        private (TabItem Tab, string Title, TextEditor Editor, FileItem File) GetSelectedTabInfo()
        {
            return GetTabInfos().FirstOrDefault(info => info.Tab == MainFileTabControl.SelectedItem);
        }

        // ステータスバーに、今開いているタブのファイルの文字コードを出す（読み込めていないタブは「-」）
        private void UpdateEncodingStatus()
        {
            (_, _, TextEditor editor, FileItem? file) = GetSelectedTabInfo();
            TxtEncodingStatus.Text = Loc.F("Status.Encoding", file is { IsLoaded: true } ? file.EncodingDisplayName : "-");
            // 改行コードも、今のタブのテキストから出す
            TxtNewLineStatus.Text = Loc.F("Status.NewLine", file is { IsLoaded: true } ? DescribeNewLine(editor.Text) : "-");
        }

        /// <summary>
        /// テキストの改行の形（CRLF / LF / CR。2 つ以上混ざっていれば「混在」。改行がなければ「-」）
        /// </summary>
        internal static string DescribeNewLine(string text)
        {
            int crlf = 0, lf = 0, cr = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') { crlf++; i++; }
                else if (text[i] == '\n') lf++;
                else if (text[i] == '\r') cr++;
            }
            int kinds = (crlf > 0 ? 1 : 0) + (lf > 0 ? 1 : 0) + (cr > 0 ? 1 : 0);
            if (kinds == 0) return "-";
            if (kinds > 1) return Loc.T("Status.NewLineMixed");
            return crlf > 0 ? "CRLF" : lf > 0 ? "LF" : "CR";
        }

        // 少しの間だけ出す知らせを消すタイマー
        private readonly DispatcherTimer _statusMessageTimer = new() { Interval = TimeSpan.FromSeconds(5) };

        // ステータスバーに知らせを 5 秒出して消す（保存しました・読み込みました。OK を押すメッセージの代わり。ユーザーの判断）
        private void ShowStatusMessage(string message)
        {
            TxtStatusMessage.Text = message;
            StatusMessageItem.Visibility = Visibility.Visible;
            OperationHistory.Add($"ステータスバー: {message}");
            _statusMessageTimer.Stop();
            _statusMessageTimer.Tick -= StatusMessageTimer_Tick;
            _statusMessageTimer.Tick += StatusMessageTimer_Tick;
            _statusMessageTimer.Start();
        }

        private void StatusMessageTimer_Tick(object? sender, EventArgs e)
        {
            _statusMessageTimer.Stop();
            StatusMessageItem.Visibility = Visibility.Collapsed;
            TxtStatusMessage.Text = string.Empty;
        }

        // 「ファイル → 文字コード」を開いたとき：今のタブの文字コードにチェックを付ける。読み込めていないタブなら選べなくする
        private void EncodingMenu_SubmenuOpened(object sender, RoutedEventArgs e)
        {
            // 中の項目から伝わってきたものは無視する
            if (e.OriginalSource != MenuEncoding) return;

            FileItem? file = GetSelectedTabInfo().File;
            bool isLoaded = file is { IsLoaded: true };
            foreach (MenuItem item in new[] { MenuEncodingUtf8, MenuEncodingUtf8Bom, MenuEncodingShiftJis })
            {
                item.IsEnabled = isLoaded;
                item.IsChecked = isLoaded && FileItem.IsSameEncoding(file!.DetectedEncoding, FileItem.CreateEncoding((string)item.Tag));
            }
        }

        // 「ファイル → 文字コード」で選んだとき：今のタブの保存する文字コードを変える（ファイルに書くのは「すべて保存」のとき）
        private void EncodingMenu_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem clickedItem || clickedItem.Tag is not string encodingName) return;

            (_, string title, TextEditor editor, FileItem file) = GetSelectedTabInfo();
            if (file is not { IsLoaded: true }) return;

            Encoding newEncoding = FileItem.CreateEncoding(encodingName);
            if (FileItem.IsSameEncoding(file.DetectedEncoding, newEncoding)) return;

            // Shift_JIS にない文字（絵文字・一部の記号など）があると、保存すると「?」に化けてしまうので変えない
            string? unencodable = FileItem.FindUnencodableText(editor.Text, newEncoding);
            if (unencodable != null)
            {
                OperationHistory.Add($"文字コード変更: {title} を {encodingName} にできない（{unencodable}）");
                MessageBox.Show(
                    Loc.F("MW.001", title, clickedItem.Header, unencodable),
                    Loc.T("MW.002"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            file.ChangeEncoding(newEncoding);
            OperationHistory.Add($"文字コード変更: {title} を {encodingName} に");
            // ヘッダーの後ろの文字コード指定（:shift_jis など）も合わせる
            UpdateHeaderEncoding(editor, file, newEncoding);
            // 未保存の * とステータスバーの表示を付け直す
            UpdateTabHeaders();
        }

        // テキストエディタの1行目（ヘッダー）の文字コード指定を、新しい文字コードに合わせて書き換える（Ctrl+Z で戻せる）
        // ヘッダーのないノッチ・モーター音と、古い形式「Version 1.0」は書き換えない
        // 古い形式かは、開いたときの印ではなく今の1行目で見る（パネルを 2.01 に書き直したあと・Ctrl+Z で戻したあとも正しく扱うため）
        private static void UpdateHeaderEncoding(TextEditor editor, FileItem file, Encoding encoding)
        {
            if (file.IsHeaderless || editor.Document.LineCount == 0) return;

            DocumentLine headerLine = editor.Document.GetLineByNumber(1);
            if (editor.Document.GetText(headerLine).Trim().StartsWith(FileItem.LegacyHeader, StringComparison.OrdinalIgnoreCase)) return;
            string? newHeader = FileItem.BuildHeaderWithEncoding(editor.Document.GetText(headerLine), encoding);
            if (newHeader == null) return;

            // 行全体を入れ替えると「元に戻す」の履歴に残る（Text = ... だと履歴が消えるため使わない）
            editor.Document.Replace(headerLine.Offset, headerLine.Length, newHeader);
            OperationHistory.Add($"文字コード変更: ヘッダーを「{newHeader}」に");
        }

        // アプリの名前（タイトルバーに出す）
        private const string AppTitle = "BveVehicleEditor";

        // タイトルバー：何も開いていなければアプリ名だけ、開いていれば「BveVehicleEditor - Vehicle_A0.txt*」
        // （* は保存していない変更があるときだけ）
        private void UpdateWindowTitle(bool hasUnsaved)
        {
            if (!_project.IsLoaded)
            {
                Title = AppTitle;
                return;
            }

            string mark = hasUnsaved ? "*" : string.Empty;
            Title = $"{AppTitle} - {_project.VehicleFile.FileName}{mark}";
        }

        // 保存していない変更があるタブの名前の一覧
        private List<string> GetUnsavedTabTitles()
        {
            return GetTabInfos()
                .Where(info => info.File.HasUnsavedChanges(info.Editor.Text))
                .Select(info => info.Title)
                .ToList();
        }

        // ウィンドウを閉じる前：未保存の変更があれば「保存して終了 / 保存せずに終了 / キャンセル」を選んでもらう
        private void MainWindow_Closing(object? sender, CancelEventArgs e)
        {
            // 閉じると決まったら、テーマ・ウィンドウ・文字の大きさを覚える（AppSettings.cs）
            if (ConfirmUnsavedChanges(Loc.T("MW.003")))
            {
                // 普通に閉じるので、自動保存の控えは要らない（アプリが落ちたときだけ残る）
                _autoSaveTimer.Stop();
                ClearAutoSave();
                SaveSettings();
                return;
            }

            // キャンセル、または保存に失敗したとき（変更が消えないように閉じない）
            e.Cancel = true;
        }

        // 未保存の変更があれば「保存して〇〇 / 保存せずに〇〇 / キャンセル」を選んでもらう
        // actionName: これからすること（「終了」「別のファイルを開く」など）
        // 続けてよければ true。キャンセル、または保存に失敗したら false
        private bool ConfirmUnsavedChanges(string actionName)
        {
            List<string> unsavedTitles = GetUnsavedTabTitles();
            if (unsavedTitles.Count == 0) return true;

            string unsavedList = string.Join("\n", unsavedTitles.Select(title => Loc.F("MW.004", title)));
            MessageBoxResult answer = MessageBox.Show(
                Loc.F("MW.005", unsavedList) +
                Loc.F("MW.006", actionName, actionName),
                Loc.T("MW.007"),
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning);

            if (answer == MessageBoxResult.No)
            {
                OperationHistory.Add($"{actionName}: 保存せずに続ける");
                return true;
            }

            if (answer == MessageBoxResult.Yes && TrySaveAll(out _))
            {
                OperationHistory.Add($"{actionName}: 保存して続ける");
                return true;
            }

            OperationHistory.Add(answer == MessageBoxResult.Yes ? $"{actionName}: 保存に失敗したのでやめる" : $"{actionName}: キャンセル");
            return false;
        }

        // テキストエディタの内容をプロジェクトオブジェクトに同期
        private void SyncEditorToProject()
        {
            _project.VehicleFile.TextContent = VehicleTextEditor.Text;
            _project.ParamFile.TextContent = ParamTextEditor.Text;
            _project.PanelFile.TextContent = PanelTextEditor.Text;
            _project.NotchFile.TextContent = NotchTextEditor.Text;
            _project.MotorNoiseFile.TextContent = MotorNoiseTextEditor.Text;
            _project.SoundFile.TextContent = SoundTextEditor.Text;
        }

        // プロジェクトオブジェクトの内容を各AvalonEditに反映
        private void SyncProjectToEditor()
        {
            VehicleTextEditor.Text = _project.VehicleFile.TextContent;
            ParamTextEditor.Text = _project.ParamFile.TextContent;
            PanelTextEditor.Text = _project.PanelFile.TextContent;
            NotchTextEditor.Text = _project.NotchFile.TextContent;
            MotorNoiseTextEditor.Text = _project.MotorNoiseFile.TextContent;
            SoundTextEditor.Text = _project.SoundFile.TextContent;
        }

        // 「開く」コマンド（メニュー / Ctrl+O）
        private void OpenCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            OpenProject_Click(sender, e);
        }

        // 「すべて保存」コマンド（メニュー / Ctrl+S）
        private void SaveCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            SaveProject_Click(sender, e);
        }

        // プロジェクト（フォルダー / Vehicle.txt）を開く処理
        private void OpenProject_Click(object sender, RoutedEventArgs e)
        {
            // 今開いているファイルに未保存の変更があれば、別のファイルを開く前に確認する
            if (!ConfirmUnsavedChanges(Loc.T("MW.008"))) return;

            OperationHistory.Add("開く: ダイアログ表示");

            OpenFileDialog dlg = new()
            {
                // 車両ファイルは Vehicle_A0.txt など名前が自由なので、.txt を全部表示する
                Filter = Loc.T("MW.009"),
                Title = Loc.T("MW.010")
            };

            if (dlg.ShowDialog() != true) return;
            OpenVehicleFile(dlg.FileName);
        }

        // 「ファイル → 新規作成…」（Ctrl+N）：先に名前と場所を選んでもらい、ヘッダーとパスが空のキーだけの車両ファイルを作って開く
        // 今ある車両ファイルを間違って消さないよう、同じ名前のファイルがあれば上書きしない
        private void NewCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            if (!ConfirmUnsavedChanges(Loc.T("MW.011"))) return;

            OperationHistory.Add("新規作成: ダイアログ表示");
            SaveFileDialog dialog = new()
            {
                Filter = Loc.T("MW.012"),
                Title = Loc.T("MW.013"),
                FileName = "Vehicle.txt",
                // 同じ名前のファイルを選んだときは、下で上書きしないと知らせる（ダイアログの「上書きしますか」は出さない）
                OverwritePrompt = false,
            };
            if (dialog.ShowDialog() != true) return;
            string path = dialog.FileName;

            if (File.Exists(path))
            {
                MessageBox.Show(Loc.F("MW.014", path), Loc.T("MW.015"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            // 参照欄と同じ決まり（使えない文字・名前の最後の空白やピリオド・260 文字以上など）で調べる（Windows のルールに従う）
            string? problem = VehicleFileParser.FindPathProblem(path, Path.GetDirectoryName(path) ?? string.Empty);
            if (problem != null)
            {
                MessageBox.Show(Loc.F("MW.016", path, problem), Loc.T("MW.017"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                VehicleProject.CreateNewFile("vehicle", path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                OperationHistory.Add($"新規作成: 作れない {path}（{ex.GetType().Name}: {ex.Message}）");
                MessageBox.Show(Loc.F("MW.018", ErrorText.Describe(ex), path), Loc.T("MW.019"), MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            OperationHistory.Add($"新規作成: {path}");
            OpenVehicleFile(path, isNewFile: true);
        }

        // 「ファイル → 閉じる」（Ctrl+W）：車両ファイルを開いているときだけ押せる（開いていなければメニューが灰色）
        private void CloseCommand_CanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            e.CanExecute = _project.IsLoaded;
        }

        // 「ファイル → 閉じる」：未保存の確認をしてから、起動した直後と同じ何も開いていない状態に戻す（アプリは閉じない）
        private void CloseCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            if (!_project.IsLoaded) return;
            if (!ConfirmUnsavedChanges(Loc.T("MW.020"))) return;

            string vehicleName = Path.GetFileName(_project.VehicleFile.FullPath) ?? string.Empty;
            // 次に開いたときに戻せるよう、タブごとのカーソルの位置を覚える
            RememberCaretPositions();
            // 普通に閉じるので、自動保存の控えは要らない
            ClearAutoSave();

            // Vehicle のタブに切り替える（サウンドのタブで鳴らしている音は、タブが見えなくなると止まる）
            MainFileTabControl.SelectedItem = TabVehicle;
            // 何も読み込んでいないプロジェクトにする（読み込み直しの記録と、0.5 秒後の参照欄の追いかけも止める）
            _project = new();
            _lastAutoLoadPaths.Clear();
            // テキストを空にする（Text に入れると元に戻す履歴も消えるので、Ctrl+Z で前の中身は戻らない）
            foreach ((_, _, TextEditor editor, _) in GetTabInfos()) editor.Text = string.Empty;
            _pathRefreshTimer.Stop();
            // 参照欄・必須の枠線・警告・「2.00 に書き直す」ボタンを、何もない状態で表示し直す
            ShowVehiclePaths(writeHistory: false);
            UpdateProjectStatus();
            // タブの * ・灰色・タイトルバー・文字コードの表示を付け直す
            UpdateTabHeaders();

            OperationHistory.Add($"閉じる: {vehicleName}");
            ShowStatusMessage(Loc.F("MW.021", vehicleName));
        }

        // 「最近使った車両ファイル」に出す数
        private const int MaxRecentMenuItems = 10;

        // 起動時に開く車両ファイル（「起動時に前の車両ファイルを開く」にチェックがあるときだけ。開いたら null）
        private string? _openOnStartupPath;

        // ウィンドウが初めて表示されたら、起動時に開く車両ファイルを開く（開いた直後の知らせは、ウィンドウが出てから出す）
        // タスクバーの履歴（ジャンプリスト）も、起動したときに作り直す
        // そのあと、前にアプリが落ちて残った自動保存の控えがあれば、開いて戻すか尋ねる
        private void MainWindow_ContentRendered(object? sender, EventArgs e)
        {
            UpdateJumpList();
            OpenStartupFile();
            AskOpenLeftoverAutoSave();
        }

        // 起動時に開く車両ファイルを開く（なければ何もしない）
        private void OpenStartupFile()
        {
            if (_openOnStartupPath == null) return;
            string path = _openOnStartupPath;
            _openOnStartupPath = null;
            OperationHistory.Add($"起動時に車両ファイルを開く: {path}");
            // 渡されたファイルがないときは、「開く」と同じく見つからないと知らせる
            if (!File.Exists(path))
            {
                MessageBox.Show(Loc.F("MW.022", path), Loc.T("MW.023"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            OpenVehicleFile(path);
        }

        // 起動したとき：ほかの車両ファイルに、前にアプリが落ちて残った控えがあれば、いちばん新しいものを開いて戻すか尋ねる
        // 「いいえ」なら控えは残す（あとでその車両ファイルを開いたときに、もう一度尋ねる）
        private void AskOpenLeftoverAutoSave()
        {
            string? currentVehicle = _project.IsLoaded ? _project.VehicleFile.FullPath : null;
            List<AutoSaveManager.Leftover> leftovers = [.. AutoSaveManager.FindAllLeftovers().Where(leftover => currentVehicle == null || !IsSamePath(leftover.Info.VehiclePath, currentVehicle))];
            if (leftovers.Count == 0) return;

            AutoSaveManager.Leftover newest = leftovers[0];
            string vehiclePath = newest.Info.VehiclePath;
            string others = leftovers.Count > 1 ? Loc.F("MW.024", leftovers.Count - 1) : string.Empty;
            OperationHistory.Add($"残った自動保存の控え: {vehiclePath}（{leftovers.Count} 件）");

            // 車両ファイルがなくなっていれば、開けないので控えを消すか尋ねる
            if (!File.Exists(vehiclePath))
            {
                MessageBoxResult remove = MessageBox.Show(
                    Loc.F("MW.025", vehiclePath, newest.Info.SavedAt, others),
                    Loc.T("MW.026"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (remove == MessageBoxResult.Yes) AutoSaveManager.Clear(vehiclePath);
                return;
            }

            MessageBoxResult answer = MessageBox.Show(
                Loc.F("MW.027", Path.GetFileName(vehiclePath), newest.Info.SavedAt, vehiclePath, others) +
                Loc.T("MW.028"),
                Loc.T("MW.029"), MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;
            if (!ConfirmUnsavedChanges(Loc.T("MW.030"))) return;
            OpenVehicleFile(vehiclePath);
        }

        // 車両ファイルを開いたあと：前にアプリが落ちて、この車両ファイルの自動保存の控えが残っていれば、控えの中身に戻すか尋ねる
        // 「はい」：控えの中身をタブに入れる（未保存の状態。Ctrl+Z でファイルの中身に戻せる。控えはすべて保存するまで残す）
        // 「いいえ」：控えを消して、ファイルの中身のまま
        // 控えを書いたあとで車両ファイルに書かれたパスが変わって、元のファイルと合わないタブは戻さない
        private void AskRestoreAutoSave(string vehiclePath)
        {
            AutoSaveManager.Leftover? leftover = AutoSaveManager.FindLeftover(vehiclePath);
            if (leftover == null) return;

            var tabs = GetTabInfos();
            List<(TextEditor Editor, string Text)> restorable = [];
            List<string> restorableNames = [];
            List<string> skippedNames = [];
            foreach (AutoSaveManager.InfoFile saved in leftover.Info.Files)
            {
                int index = Array.IndexOf(AutoSaveKeys, saved.Key);
                string name = Path.GetFileName(saved.FullPath) ?? saved.Key;
                string? text = AutoSaveManager.ReadText(leftover, saved);
                if (index < 0 || text == null)
                {
                    skippedNames.Add(Loc.F("MW.031", saved.Key, name));
                    continue;
                }
                (_, string title, TextEditor editor, FileItem file) = tabs[index];
                if (!file.IsLoaded || !IsSamePath(file.FullPath, saved.FullPath))
                {
                    skippedNames.Add(Loc.F("MW.032", title, name));
                    continue;
                }
                restorable.Add((editor, text));
                restorableNames.Add(Loc.F("MW.033", title, name));
            }

            string when = leftover.Info.SavedAt.ToString("yyyy/MM/dd HH:mm");
            string skippedText = skippedNames.Count > 0 ? Loc.F("MW.034", string.Join("\n", skippedNames)) : string.Empty;
            OperationHistory.Add($"自動保存の控えが残っている: {vehiclePath}（戻せる {restorable.Count} 件・戻せない {skippedNames.Count} 件）");
            if (restorable.Count == 0)
            {
                MessageBoxResult remove = MessageBox.Show(
                    Loc.F("MW.035", when, skippedText),
                    Loc.T("MW.036"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (remove == MessageBoxResult.Yes) AutoSaveManager.Clear(vehiclePath);
                return;
            }

            MessageBoxResult answer = MessageBox.Show(
                Loc.F("MW.037", when, string.Join("\n", restorableNames), skippedText) +
                Loc.T("MW.038"),
                Loc.T("MW.039"), MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
            {
                OperationHistory.Add("自動保存の控え: 戻さずに消す");
                AutoSaveManager.Clear(vehiclePath);
                return;
            }

            RestoreAutoSave(vehiclePath, restorable);
        }

        // 控えの中身をタブに入れる（変わった部分だけ置き換えるので Ctrl+Z で戻せる）。そのあと、このアプリの控えとして書き直す
        // （書き直さないと、控えの「書いたアプリ」が落ちたアプリのままで、ほかのウィンドウが残った控えと見間違える）
        private void RestoreAutoSave(string vehiclePath, List<(TextEditor Editor, string Text)> restorable)
        {
            foreach ((TextEditor editor, string text) in restorable) EditorTextHelper.ReplaceChangedText(editor, text);
            UpdateTabHeaders();
            List<AutoSaveManager.Entry> entries = BuildAutoSaveEntries();
            if (AutoSaveManager.Save(vehiclePath, entries)) _lastAutoSaved = entries;
            OperationHistory.Add($"自動保存の控えに戻した: {restorable.Count} 件");
            ShowStatusMessage(Loc.F("MW.040", restorable.Count));
        }

        // タスクバーの履歴（ジャンプリスト）に出す名前の分類
        private static string JumpListCategory => Loc.T("MW.041");

        // タスクバーのアイコンを右クリックしたときの一覧に、最近使った車両ファイル（見つかるもの・新しい順に 10 件）を出す
        // 押すと、そのファイルを渡してアプリが新しく起動する（App.StartupFilePath）。関連付けをしなくても使えるように、ファイルではなく「アプリを起動する項目」にしている
        // 自分で作る分類なので、ピン留めもできる。Windows の設定「最近開いた項目を…ジャンプリスト…に表示する」が切ってあると出ない（Windows のふつうの動きに合わせる。
        // 「タスク」の欄にすると設定に関係なく出るが、ピン留めができないので戻した。ユーザーの PC で設定を入れ直して出ることを確かめた）
        private void UpdateJumpList()
        {
            string? exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath)) return;
            try
            {
                System.Windows.Shell.JumpList jumpList = new();
                foreach (string path in _caretPositions.Select(item => item.VehiclePath).Where(File.Exists).Take(MaxRecentMenuItems))
                {
                    jumpList.JumpItems.Add(new System.Windows.Shell.JumpTask
                    {
                        Title = ShortVehicleName(path),
                        Description = path,
                        ApplicationPath = exePath,
                        Arguments = $"\"{path}\"",
                        IconResourcePath = exePath,
                        CustomCategory = JumpListCategory,
                    });
                }
                System.Windows.Shell.JumpList.SetJumpList(Application.Current, jumpList);
                jumpList.Apply();
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
            {
                // タスクバーの一覧が作れなくてもアプリは使えるので、メッセージは出さない
                OperationHistory.Add($"タスクバーの一覧を作れない: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // 「ファイル → 最近使った車両ファイル」を開いたとき：新しい順に並べ直す（見つからないファイルは灰色。マウスを乗せるとフルパス）
        private void RecentMenu_SubmenuOpened(object sender, RoutedEventArgs e)
        {
            if (e.OriginalSource != MenuRecent) return;
            MenuRecent.Items.Clear();
            List<string> paths = [.. _caretPositions.Select(item => item.VehiclePath).Where(path => path.Length > 0).Take(MaxRecentMenuItems)];
            if (paths.Count == 0)
            {
                MenuRecent.Items.Add(new MenuItem { Header = Loc.T("Menu.RecentNone"), IsEnabled = false });
                return;
            }
            for (int i = 0; i < paths.Count; i++)
            {
                bool exists = File.Exists(paths[i]);
                // 「_」はアクセスキーの印になるので、名前の中の「_」は「__」にする。番号 1～9 と 0 はアクセスキー
                string number = i < 9 ? $"_{i + 1}" : "1_0";
                MenuItem item = new()
                {
                    Header = $"{number}  {ShortVehicleName(paths[i]).Replace("_", "__")}",
                    Tag = paths[i],
                    IsEnabled = exists,
                    ToolTip = exists ? paths[i] : Loc.F("Tip.RecentMissing", paths[i]),
                };
                item.Click += RecentItem_Click;
                MenuRecent.Items.Add(item);
            }
        }

        /// <summary>
        /// 一覧に出す短い名前（フォルダー 2 つ分と名前。例：semboku7000\Motor\Vehicle.txt）
        /// </summary>
        internal static string ShortVehicleName(string path)
        {
            string[] parts = path.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length <= 3 ? path : string.Join(Path.DirectorySeparatorChar, parts[^3..]);
        }

        // エクスプローラーからファイルをドラッグしているとき：ファイル 1 つなら「コピー」の印にする（テキストエリアの上でも、文字を入れる動きより先に受け取る）
        private void Window_PreviewDragOver(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            e.Effects = DroppedFile(e) != null ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        // ファイルを離したとき：車両ファイルとして、未保存の確認をしてから開く（車両ファイルでなければ、開くときにヘッダーが合わないと知らせる）
        private void Window_PreviewDrop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            e.Handled = true;
            string? path = DroppedFile(e);
            if (path == null)
            {
                MessageBox.Show(Loc.T("MW.042"), Loc.T("MW.043"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            OperationHistory.Add($"ドラッグで開く: {path}");
            if (!ConfirmUnsavedChanges(Loc.T("MW.044"))) return;
            OpenVehicleFile(path);
        }

        // ドラッグしているのがファイル 1 つ（フォルダーでない）なら、そのフルパス。ほかは null
        private static string? DroppedFile(DragEventArgs e)
        {
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: 1 } paths) return null;
            return File.Exists(paths[0]) ? paths[0] : null;
        }

        // 最近使った車両ファイルを押したとき：未保存の確認をしてから開く
        private void RecentItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem { Tag: string path }) return;
            if (!ConfirmUnsavedChanges(Loc.T("MW.045"))) return;
            OpenVehicleFile(path);
        }

        // 車両ファイルを開いて各タブに読み込む（「Vehicle.txt を開く…」・最近使った車両ファイル・起動時に前の車両ファイルを開く・新規作成 で共通。未保存の確認は呼ぶ側で）
        // isNewFile: 「新規作成」で作ったばかりのファイル（パスが 1 つも書かれていない警告を出さず、ステータスバーに知らせる）
        private void OpenVehicleFile(string vehiclePath, bool isNewFile = false)
        {
            string? dir = Path.GetDirectoryName(vehiclePath);
            if (string.IsNullOrEmpty(dir)) return;

            OperationHistory.Add($"開く: {vehiclePath}");

            try
            {
                // 今の車両ファイルのカーソルの位置を、別のファイルを開く前に覚える
                RememberCaretPositions();
                // 今の車両ファイルの自動保存の控えは、未保存の確認が済んでいるので要らない
                ClearAutoSave();
                // 読み込み時にヘッダー不一致などの InvalidDataException や IOException が発生する可能性がある
                _project.Load(vehiclePath);
                // 前のプロジェクトで「自動で読み込み直そうとした」記録を消す
                _lastAutoLoadPaths.Clear();
                // 読み込めなかったファイルは「もう試した」ことにする
                // （0.5秒後の自動読み込み直しで、同じエラーをもう一度出さないため）
                foreach (FileLoadError error in _project.LoadErrors)
                {
                    _lastAutoLoadPaths[error.Key] = error.FullPath;
                }
                SyncProjectToEditor();
                // 前にこの車両ファイルを開いていたら、タブごとのカーソルの位置へ戻す
                RestoreCaretPositions();
                // 最近使った車両ファイルの先頭に入れる（アプリが途中で落ちても一覧に残るように、閉じる前にも入れておく）
                RememberCaretPositions();
                // タスクバーの履歴も作り直す
                UpdateJumpList();
                // 開いた直後は変更なしなので * を外す（テキストが前と同じだと TextChanged が来ないことがあるため明示的に呼ぶ）
                UpdateTabHeaders();

                OperationHistory.Add("開く: 成功");

                UpdateProjectStatus();
                // 長くて「…」で省略されたときのために、マウスを乗せると全部見えるようにする
                TxtProjectStatus.ToolTip = dir;

                // 車両ファイルに書かれたパスを参照欄に表示し、見つからないファイルを知らせる
                List<VehiclePathEntry> entries = ShowVehiclePaths();
                if (isNewFile)
                {
                    ShowStatusMessage(Loc.F("MW.046", Path.GetFileName(vehiclePath)));
                    return;
                }
                // 見つからないファイルを作ったら、参照欄と枠線を表示し直してから結果を知らせる
                if (AskCreateMissingFiles(entries)) entries = ShowVehiclePaths();
                ShowLoadResult(entries, _project);
                // 前にアプリが落ちて、この車両ファイルの自動保存の控えが残っていれば、戻すか尋ねる
                AskRestoreAutoSave(vehiclePath);
            }
            catch (InvalidDataException ex)
            {
                // ヘッダー不一致など、フォーマットエラーの場合の通知
                OperationHistory.Add($"開く: ファイル形式エラー（{ex.Message}）");
                MessageBox.Show(ex.Message, Loc.T("MW.047"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                // その他の予期せぬエラー（アクセス権限やファイル破損など）
                OperationHistory.Add($"開く: エラー（{ex.GetType().Name}: {ex.Message}）");
                MessageBox.Show(Loc.F("MW.048", ErrorText.Describe(ex)), Loc.T("MW.049"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // キー（大文字小文字は区別しない）と参照欄の対応
        private Dictionary<string, TextBox> GetPathBoxes()
        {
            return new(StringComparer.OrdinalIgnoreCase)
            {
                ["Parameters"] = TxtParamPath,
                ["Panel"] = TxtPanelPath,
                ["Sound"] = TxtSoundPath,
                ["PerformanceCurve"] = TxtNotchPath,
                ["MotorNoise"] = TxtMotorNoisePath,
                ["Ats"] = TxtAtsPath,
                ["Ats32"] = TxtAts32Path,
                ["Ats64"] = TxtAts64Path,
            };
        }

        // ATS プラグイン（DLL）のキーか。参照ボタンでは DLL を選び、タブには読み込まない
        private static bool IsAtsKey(string key)
        {
            return key.Equals("Ats", StringComparison.OrdinalIgnoreCase) || key.Equals("Ats32", StringComparison.OrdinalIgnoreCase) || key.Equals("Ats64", StringComparison.OrdinalIgnoreCase);
        }

        // ATS の参照欄を、ヘッダーのバージョンに合わせて出し分ける
        // 2.00（とバージョンが読めないとき）は Ats32・Ats64、2.00 より前は Ats。バージョンに合わなくても書かれていれば出す（警告は ShowVehicleKeyProblems）
        private void ShowAtsRows(List<VehiclePathEntry> entries)
        {
            (decimal? version, _) = VehicleFileParser.ReadVersion(VehicleTextEditor.Text);
            bool isNewFormat = version == null || version >= 2.00m;
            bool HasKey(string key) => entries.Any(entry => entry.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

            SetRowVisible(!isNewFormat || HasKey("Ats"), LblAtsPath, TxtAtsPath, BtnAtsPath);
            SetRowVisible(isNewFormat || HasKey("Ats32"), LblAts32Path, TxtAts32Path, BtnAts32Path);
            SetRowVisible(isNewFormat || HasKey("Ats64"), LblAts64Path, TxtAts64Path, BtnAts64Path);
        }

        // 参照欄の 1 行（名前・欄・ボタン）を出す・隠す
        private static void SetRowVisible(bool isVisible, params UIElement[] elements)
        {
            foreach (UIElement element in elements)
            {
                element.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        // 取り出したパスを全部返す（見つからないものも含む）
        // writeHistory: 操作履歴に残すか（テキストを打つたびの自動更新では残さない。履歴が埋まってしまうため）
        private List<VehiclePathEntry> ShowVehiclePaths(bool writeHistory = true)
        {
            Dictionary<string, TextBox> pathBoxes = GetPathBoxes();

            // 前に開いたファイルの表示を消す
            foreach (TextBox box in pathBoxes.Values)
            {
                box.Text = string.Empty;
                box.ToolTip = null;
            }

            List<VehiclePathEntry> entries = VehicleFileParser.Parse(VehicleTextEditor.Text, _project.BaseDirectory);
            List<VehiclePathEntry> missingEntries = [];

            foreach (VehiclePathEntry entry in entries)
            {
                // Ats32 / Ats64 など参照欄がないものも、見つからなければ知らせる
                if (!entry.Exists) missingEntries.Add(entry);
                if (!pathBoxes.TryGetValue(entry.Key, out TextBox? box)) continue;
                box.Text = entry.RawPath;
                // マウスを乗せるとフルパスが見える
                // パスに問題があって探さなかったときは、その理由が見える
                box.ToolTip = entry.Problem ?? entry.FullPath ?? Loc.T("VP.PathUnreadable");
            }

            UpdateRequiredPathBorders(entries);
            ShowAtsRows(entries);
            ShowVehicleKeyProblems();

            if (writeHistory)
            {
                OperationHistory.Add($"パス取り出し: {entries.Count}件（見つからない: {missingEntries.Count}件）");
            }
            return entries;
        }

        // 車両ファイルのキーの書き方を、ヘッダーのバージョンと合わせて調べ、参照欄の上に警告とお知らせを出す（なければ隠す）
        // ふだんは「⚠ 警告 3 件・お知らせ 1 件（クリックで開く）」の1行だけ。開いているかどうかは、表示し直しても変えない
        private void ShowVehicleKeyProblems()
        {
            (List<string> warnings, List<string> notices) = VehicleFileParser.FindKeyProblems(VehicleTextEditor.Text);
            // 開いているときだけ（パスの基準のフォルダーが要る）：
            // ・別のキーが同じファイルを指していないか
            // ・ATS の DLL が 32 ビット版・64 ビット版のどちらかと、キー（Ats32・Ats64・Ats）が合うか（DLL の先頭を読む）
            if (_project.IsLoaded)
            {
                List<VehiclePathEntry> entries = VehicleFileParser.Parse(VehicleTextEditor.Text, _project.BaseDirectory);
                warnings.AddRange(VehicleFileParser.FindSamePathProblems(entries));
                warnings.AddRange(VehicleFileParser.FindAtsDllProblems(entries));
            }
            TxtVehicleWarnings.Text = string.Join("\n", warnings.Select(warning => "⚠ " + warning));
            TxtVehicleWarnings.Visibility = warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            TxtVehicleNotice.Text = string.Join("\n", notices);
            TxtVehicleNotice.Visibility = notices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            ExpVehicleMessages.Visibility = warnings.Count + notices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            List<string> counts = [];
            if (warnings.Count > 0) counts.Add(Loc.F("Vehicle.WarningCount", warnings.Count));
            if (notices.Count > 0) counts.Add(Loc.F("Vehicle.NoticeCount", notices.Count));
            TxtVehicleMessagesHeader.Text = string.Join(Loc.T("Vehicle.CountSeparator"), counts) + Loc.T(ExpVehicleMessages.IsExpanded ? "Vehicle.ClickToClose" : "Vehicle.ClickToOpen");
            // 警告があれば注意の色、お知らせだけなら薄い文字
            TxtVehicleMessagesHeader.SetResourceReference(TextBlock.ForegroundProperty, warnings.Count > 0 ? "SystemFillColorCautionBrush" : "TextFillColorSecondaryBrush");

            // ヘッダーが 2.00 より前なら「2.00 に書き直す」ボタンを出す
            (decimal? version, _) = VehicleFileParser.ReadVersion(VehicleTextEditor.Text);
            BtnUpgradeVehicle.Visibility = version < 2.00m ? Visibility.Visible : Visibility.Collapsed;
        }

        // 「BveTs Vehicle 2.00 に書き直す」を押したとき：何を変えるか見せて確認してから書き直す（Ctrl+Z で戻せる。保存は「すべて保存」のとき）
        // 書き直したあと、BVE6 で使うための Ats64 がない・「=」のない行がある、などを知らせる
        private void BtnUpgradeVehicle_Click(object sender, RoutedEventArgs e)
        {
            VehicleFileParser.UpgradeResult result = VehicleFileParser.BuildUpgrade(VehicleTextEditor.Text, _project.VehicleFile.DetectedEncoding);
            string encodingNote = result.Header.EndsWith(":shift_jis") ? Loc.T("MW.050") : string.Empty;
            List<string> changes = [Loc.F("MW.051", result.Header, encodingNote)];
            if (result.CommentedSummaryCount > 0) changes.Add(Loc.F("MW.052", result.CommentedSummaryCount));
            if (result.RenamedAtsCount > 0) changes.Add(Loc.T("MW.053"));

            MessageBoxResult answer = MessageBox.Show(
                Loc.F("MW.054", string.Join("\n", changes)),
                Loc.T("MW.055"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;

            ApplyVehicleUpgrade(result);

            List<string> notes = UpgradeNotes(result);
            if (notes.Count == 0) return;
            MessageBox.Show(string.Join("\n\n", notes), Loc.T("MW.056"), MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // 書き直したテキストを画面に入れる（変わった部分だけ置き換えるので Ctrl+Z で戻せる）。参照欄と警告も表示し直す
        private void ApplyVehicleUpgrade(VehicleFileParser.UpgradeResult result)
        {
            EditorTextHelper.ReplaceChangedText(VehicleTextEditor, result.Text);
            ShowVehiclePaths();
            OperationHistory.Add($"車両ファイル: {result.Header} に書き直した（コメントにした行 {result.CommentedSummaryCount}、Ats32 にした行 {result.RenamedAtsCount}）");
        }

        // 書き直したあとに知らせること（BVE6 で使うための Ats64、「=」のない行）
        private static List<string> UpgradeNotes(VehicleFileParser.UpgradeResult result)
        {
            List<string> notes = [];
            if (!result.HasAts64) notes.Add(Loc.T("MW.057"));
            if (result.NotKeyValueLines.Count > 0) notes.Add(Loc.F("MW.058", string.Join(Loc.T("MW2.001"), result.NotKeyValueLines.Select(line => Loc.F("MW2.002", line)))));
            return notes;
        }

        // 警告とお知らせを開いた・閉じたとき：見出しの「クリックで開く／閉じる」を付け直す
        private void ExpVehicleMessages_Toggled(object sender, RoutedEventArgs e)
        {
            ShowVehicleKeyProblems();
        }

        // 必須の参照欄（パラメーター・ノッチ）の枠線
        // パスが書かれていて、そのファイルを読み込めた（ヘッダーも一致した）ら枠線を消す
        private void UpdateRequiredPathBorders(List<VehiclePathEntry> entries)
        {
            Dictionary<string, TextBox> pathBoxes = GetPathBoxes();
            Style requiredStyle = (Style)TxtParamPath.FindResource("RequiredPathBoxStyle");

            foreach ((string key, _) in VehicleFileParser.RequiredKeys)
            {
                if (!pathBoxes.TryGetValue(key, out TextBox? box)) continue;

                FileItem? file = _project.GetFileByKey(key);
                // 同じキーが複数あれば最後のもの（読み込みと同じ）
                string? writtenPath = entries.LastOrDefault(entry => string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase))?.FullPath;
                // 書かれたパスと、読み込んでいるファイルが同じときだけ「準備できた」とする
                // （タブを空にするのを取りやめたときなど、パスが消えてもファイルが残っていることがあるため）
                bool isReady = file is { IsLoaded: true } && writtenPath != null && IsSamePath(writtenPath, file.FullPath);

                if (isReady)
                {
                    // ふだんの見た目（Window.Resources の TextBox スタイル）に戻す
                    box.ClearValue(StyleProperty);
                    continue;
                }
                box.Style = requiredStyle;
            }
        }

        // 参照ボタン：ファイルを選んで、車両ファイルにパスを書き込む（Tag にキーが入っている）
        private void BrowsePath_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not string key) return;

            // 相対パスの基準になる車両ファイルがないと書き込めない
            if (!_project.IsLoaded)
            {
                MessageBox.Show(Loc.T("MW.059"), Loc.T("MW.060"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!GetPathBoxes().TryGetValue(key, out TextBox? pathBox)) return;

            OperationHistory.Add($"参照: {key} ダイアログ表示");

            // ATS プラグインは DLL を選ぶ（作れないので、あるファイルだけ選べる）
            bool isAts = IsAtsKey(key);
            OpenFileDialog dlg = new()
            {
                Filter = isAts ? Loc.T("MW.061") : Loc.T("MW.062"),
                Title = isAts ? Loc.F("MW.063", key) : Loc.F("MW.064", key),
                InitialDirectory = GetInitialDirectory(pathBox),
                // まだないファイル名も打てるようにする（あとで作るか尋ねる）。拡張子を省いたら .txt を付ける。ATS はあるファイルだけ
                CheckFileExists = isAts,
                DefaultExt = isAts ? ".dll" : ".txt",
            };

            if (dlg.ShowDialog() != true) return;

            // 車両ファイルからの相対パスにする（別のドライブなら絶対パスのまま）
            string relativePath = Path.GetRelativePath(_project.BaseDirectory, dlg.FileName);

            // 名前の最後がピリオド・長すぎるなど、車両ファイルに書くと使えないパスなら読み込まない
            string? problem = VehicleFileParser.FindPathProblem(relativePath, _project.BaseDirectory);
            if (problem != null)
            {
                OperationHistory.Add($"参照: {key} 使えないパス（{problem}）");
                MessageBox.Show(Loc.F("MW.065", relativePath, problem), Loc.T("MW.066"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // ATS は、選んだ DLL の種類（32 ビット版・64 ビット版）がキーと合わなければ、書く前に尋ねる（BVE で Cannot load. になるため）
            if (isAts && VehicleFileParser.FindAtsDllProblem(key, dlg.FileName) is string dllProblem)
            {
                OperationHistory.Add($"参照: {key} DLL の種類が合わない（{dllProblem}）");
                MessageBoxResult answer = MessageBox.Show(Loc.F("MW.067", dllProblem, relativePath), Loc.T("MW.068"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes) return;
            }

            // まだないファイルなら、作るか尋ねる（作らなければ何も変えない）。ATS はタブがないので読み込まない
            if (!isAts && !File.Exists(dlg.FileName) && !TryCreateNewFile(key, dlg.FileName)) return;

            // 選んだファイルを、そのタブにすぐ読み込む。読み込めなければパスも書き換えない
            if (!isAts && !ReloadFileToTab(key, dlg.FileName)) return;

            // 車両ファイルの中身を書き換え（キーがなければ1行足す）、参照欄を表示し直す
            string newText = VehicleFileParser.SetPath(VehicleTextEditor.Text, key, relativePath);
            // 変わった部分だけ置き換える（Ctrl+Z で戻せる。処理は EditorTextHelper.cs）
            EditorTextHelper.ReplaceChangedText(VehicleTextEditor, newText);
            ShowVehiclePaths();

            // すぐ Ctrl+Z で戻せるように、テキストエディタにカーソルを移す
            VehicleTextEditor.Focus();

            OperationHistory.Add($"参照: {key} = {relativePath}");
        }

        // 開いた直後：見つからないファイルのうち作れるもの（パスに問題がなく、ひな形がある欄）を、まとめて作るか尋ねる
        // 1つでも作ろうとしたら true（参照欄を表示し直すため）
        private bool AskCreateMissingFiles(List<VehiclePathEntry> entries)
        {
            // 同じキーが複数あれば最後のもの（読み込みと同じ）。ATS プラグイン（DLL）などひな形のないものは作らない
            List<VehiclePathEntry> creatableEntries = entries
                .GroupBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Last())
                .Where(entry => !entry.Exists && entry.Problem == null && entry.FullPath != null && VehicleProject.GetNewFileTemplate(entry.Key) != null)
                .ToList();
            if (creatableEntries.Count == 0) return false;

            string creatableList = string.Join("\n", creatableEntries.Select(entry => Loc.F("MW.069", entry.Key, entry.RawPath)));
            MessageBoxResult answer = MessageBox.Show(
                Loc.F("MW.070", creatableList),
                Loc.T("MW.071"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
            {
                OperationHistory.Add($"開く: 見つからないファイル {creatableEntries.Count}件を作るのを取りやめ");
                return false;
            }

            List<string> failedMessages = [];
            foreach (VehiclePathEntry entry in creatableEntries)
            {
                string fullPath = entry.FullPath!;
                try
                {
                    VehicleProject.CreateNewFile(entry.Key, fullPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    OperationHistory.Add($"開く: {entry.Key} 作成エラー（{ex.GetType().Name}: {ex.Message}）");
                    failedMessages.Add(Loc.F("MW.072", entry.Key, ErrorText.Describe(ex)));
                    continue;
                }

                OperationHistory.Add($"開く: {entry.Key} 新しいファイルを作成 {fullPath}");
                // 作ったファイルをそのタブに読み込む（開いた直後なので未保存の確認は出ない）
                ReloadFileToTab(entry.Key, fullPath);
            }

            if (failedMessages.Count > 0)
            {
                OperationHistory.Add($"開く: ファイルの作成に {failedMessages.Count}件失敗");
                MessageBox.Show(Loc.F("MW.073", string.Join("\n", failedMessages)), Loc.T("MW.074"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
            return true;
        }

        // まだないファイルを、ヘッダーだけ入れて作るか尋ねる。作れたら true
        private static bool TryCreateNewFile(string key, string fullPath)
        {
            MessageBoxResult answer = MessageBox.Show(
                Loc.F("MW.075", fullPath),
                Loc.T("MW.076"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
            {
                OperationHistory.Add($"参照: {key} 新しいファイルを作るのを取りやめ");
                return false;
            }

            try
            {
                VehicleProject.CreateNewFile(key, fullPath);
                OperationHistory.Add($"参照: {key} 新しいファイルを作成 {fullPath}");
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                OperationHistory.Add($"参照: {key} 作成エラー（{ex.GetType().Name}: {ex.Message}）");
                MessageBox.Show(Loc.F("MW.077", ErrorText.Describe(ex)), Loc.T("MW.078"), MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        // キーと、ファイルの中身を表示するテキストエディタ（タブ）の対応
        private Dictionary<string, TextEditor> GetFileEditors()
        {
            return new(StringComparer.OrdinalIgnoreCase)
            {
                ["Parameters"] = ParamTextEditor,
                ["Panel"] = PanelTextEditor,
                ["Sound"] = SoundTextEditor,
                ["PerformanceCurve"] = NotchTextEditor,
                ["MotorNoise"] = MotorNoiseTextEditor,
            };
        }

        // 参照ボタンで選んだファイルを、対応するタブに読み込む。読み込めたら true
        private bool ReloadFileToTab(string key, string fullPath)
        {
            FileItem? currentFile = _project.GetFileByKey(key);
            if (currentFile == null || !GetFileEditors().TryGetValue(key, out TextEditor? editor)) return false;

            // タブの中身を書き換えたまま保存していなければ、消えてしまうので確認する
            if (editor.Text != currentFile.TextContent)
            {
                MessageBoxResult answer = MessageBox.Show(
                    Loc.F("MW.079", key),
                    Loc.T("MW.080"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes)
                {
                    OperationHistory.Add($"参照: {key} 読み込みを取りやめ（未保存の変更あり）");
                    return false;
                }
            }

            try
            {
                FileItem newFile = _project.ReplaceFile(key, fullPath);
                // 別のファイルなので、元に戻す履歴は消えてよい
                editor.Text = newFile.TextContent;
                // 空のファイルを読み込んだときはテキストが変わらず TextChanged が来ないので、灰色を外すかを決め直す
                UpdateTabHeaders();
                OperationHistory.Add($"参照: {key} を読み込み {fullPath}");

                // 読み込めたが、ノッチ・モーター音として中身が怪しいときは警告だけ出す
                if (newFile.LacksPowerBrakeSection)
                {
                    OperationHistory.Add($"参照: {key} に [Power] / [Brake] がない");
                    MessageBox.Show(Loc.F("MW.081", NoSectionWarning, newFile.FileName), Loc.T("MW.082"), MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                // ヘッダーの文字コード指定が合わないときも警告だけ出す
                if (newFile.EncodingWarning != null)
                {
                    OperationHistory.Add($"参照: {key} の文字コード指定が合わない");
                    MessageBox.Show(Loc.F("MW.083", EncodingWarningHeading, newFile.FileName, newFile.EncodingWarning), Loc.T("MW.084"), MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                return true;
            }
            catch (InvalidDataException ex)
            {
                // ヘッダー不一致など（今のファイルはそのまま残る）
                OperationHistory.Add($"参照: {key} ファイル形式エラー（{ex.Message}）");
                MessageBox.Show(ex.Message, Loc.T("MW.085"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            catch (Exception ex)
            {
                OperationHistory.Add($"参照: {key} 読み込みエラー（{ex.GetType().Name}: {ex.Message}）");
                MessageBox.Show(Loc.F("MW.086", ErrorText.Describe(ex)), Loc.T("MW.087"), MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        // 車両ファイルのテキストに書かれたパスと、各タブに読み込んでいるファイルを比べ、違っていれば合わせる
        private void SyncTabsWithVehiclePaths()
        {
            List<VehiclePathEntry> entries = VehicleFileParser.Parse(VehicleTextEditor.Text, _project.BaseDirectory);

            foreach (string key in GetFileEditors().Keys)
            {
                FileItem? currentFile = _project.GetFileByKey(key);
                if (currentFile == null) continue;

                // 同じキーが複数あれば最後のもの（読み込みと同じ）
                string? newPath = entries.LastOrDefault(entry => string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase))?.FullPath;

                // 今読み込んでいるファイルと同じなら何もしない
                if (IsSamePath(newPath, currentFile.FullPath))
                {
                    _lastAutoLoadPaths.Remove(key);
                    continue;
                }

                // 前にも同じパスで試していれば、もう一度は試さない（確認やエラーを繰り返さない）
                if (_lastAutoLoadPaths.TryGetValue(key, out string? lastPath) && IsSamePath(lastPath, newPath)) continue;
                
                _lastAutoLoadPaths[key] = newPath;

                if (newPath != null && File.Exists(newPath))
                {
                    ReloadFileToTab(key, newPath);
                    continue;
                }

                // 行が消えた・ファイルが見つからない → そのタブを空にする
                ClearTab(key);
            }
        }

        // タブを空にする（保存していない変更があれば確認する）
        private void ClearTab(string key)
        {
            FileItem? currentFile = _project.GetFileByKey(key);
            if (currentFile == null || !currentFile.IsLoaded || !GetFileEditors().TryGetValue(key, out TextEditor? editor)) return;


            if (editor.Text != currentFile.TextContent)
            {
                MessageBoxResult answer = MessageBox.Show(
                    Loc.F("MW.088", key),
                    Loc.T("MW.089"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes)
                {
                    OperationHistory.Add($"自動: {key} を空にするのを取りやめ（未保存の変更あり）");
                    return;
                }
            }

            _project.ClearFile(key);
            editor.Text = string.Empty;
            // 空のファイルだったときはテキストが変わらず TextChanged が来ないので、灰色にするかを決め直す
            UpdateTabHeaders();
            OperationHistory.Add($"自動: {key} のタブを空にした（パスがない・見つからない）");
        }

        // 2つのパスが同じファイルを指しているか（大文字小文字は区別しない。どちらも null なら同じ）
        private static bool IsSamePath(string? pathA, string? pathB)
        {
            if (pathA == null || pathB == null) return pathA == pathB;

            return string.Equals(Path.GetFullPath(pathA), Path.GetFullPath(pathB), StringComparison.OrdinalIgnoreCase);
        }

        // ファイル選択ダイアログを最初に開く場所（今のファイルの場所、なければ車両ファイルの場所）
        private string GetInitialDirectory(TextBox pathBox)
        {
            if (pathBox.ToolTip is string fullPath && File.Exists(fullPath)) return Path.GetDirectoryName(fullPath) ?? _project.BaseDirectory;

            return _project.BaseDirectory;
        }

        // ノッチ・モーター音に [Power] も [Brake] もないときの警告文（開いたとき・参照ボタンのときで共通）
        private static string NoSectionWarning => Loc.T("MW.090");

        // ヘッダーの文字コード指定が合わないときの見出し（開いたとき・参照ボタンのときで共通）
        private static string EncodingWarningHeading => Loc.T("MW.091");

        // 読み込み結果を知らせる。必須ファイルがない・見つからないファイル・読み込めなかったファイル・
        // 文字コード指定が合わないファイル・古い形式のファイル・[Power]/[Brake] のないノッチやモーター音があれば一覧で出す
        private void ShowLoadResult(List<VehiclePathEntry> entries, VehicleProject project)
        {
            List<FileLoadError> loadErrors = project.LoadErrors;
            List<FileItem> encodingWarningFiles = project.GetEncodingWarningFiles();
            List<FileItem> legacyFiles = project.GetLegacyFormatFiles();
            List<FileItem> noSectionFiles = project.GetFilesWithoutPowerBrakeSection();

            // ファイルパスが1つも書かれていない
            if (entries.Count == 0)
            {
                MessageBox.Show(
                    Loc.T("MW.092"),
                    Loc.T("MW.093"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            List<string> requiredMessages = VehicleFileParser.FindMissingRequired(entries);
            // 必須ファイルは上で知らせるので、ここでは必須以外の見つからないファイルだけ
            // 必須以外で、パスに問題があって探さなかったもの・見つからなかったものを分ける
            List<VehiclePathEntry> optionalProblems = entries.Where(entry => entry.Problem != null && !VehicleFileParser.IsRequiredKey(entry.Key)).ToList();
            List<VehiclePathEntry> optionalMissing = entries.Where(entry => !entry.Exists && entry.Problem == null && !VehicleFileParser.IsRequiredKey(entry.Key)).ToList();

            if (requiredMessages.Count == 0 && optionalProblems.Count == 0 && optionalMissing.Count == 0 && loadErrors.Count == 0 && encodingWarningFiles.Count == 0 && legacyFiles.Count == 0 && noSectionFiles.Count == 0)
            {
                // 問題がなければ、OK を押すメッセージの代わりにステータスバーに少しの間だけ出す（ユーザーの判断）
                ShowStatusMessage(Loc.F("MW.094", Path.GetFileName(project.VehicleFile.FullPath)));
                return;
            }

            string message = Loc.T("MW.095");
            if (requiredMessages.Count > 0)
            {
                message += Loc.F("MW.096", string.Join("\n", requiredMessages));
            }
            if (optionalProblems.Count > 0)
            {
                string problemList = string.Join("\n", optionalProblems.Select(entry => Loc.F("MW.097", entry.Key, entry.RawPath, entry.Problem)));
                message += Loc.F("MW.098", problemList);
            }
            if (optionalMissing.Count > 0)
            {
                string missingList = string.Join("\n", optionalMissing.Select(entry => Loc.F("MW.099", entry.Key, entry.RawPath)));
                message += Loc.F("MW.100", missingList);
            }
            if (loadErrors.Count > 0)
            {
                // 理由は複数行なので、2行目からは字下げして見やすくする。必須ファイルには【必須】を付ける
                string errorList = string.Join("\n", loadErrors.Select(error =>
                    Loc.F("MW.101", (VehicleFileParser.IsRequiredKey(error.Key) ? Loc.T("MW2.003") : string.Empty), error.Key, error.Message.Replace("\n", "\n　　"))));
                message += Loc.F("MW.102", errorList);
            }
            if (encodingWarningFiles.Count > 0)
            {
                string encodingList = string.Join("\n", encodingWarningFiles.Select(file => Loc.F("MW.103", file.FileName, file.EncodingWarning)));
                message += $"\n\n{EncodingWarningHeading}\n{encodingList}";
            }
            if (legacyFiles.Count > 0)
            {
                string legacyList = string.Join("\n", legacyFiles.Select(file => Loc.F("MW.104", file.FileName)));
                message += Loc.F("MW.105", FileItem.LegacyHeader, legacyList);
            }

            if (noSectionFiles.Count > 0)
            {
                string noSectionList = string.Join("\n", noSectionFiles.Select(file => Loc.F("MW.106", file.FileName)));
                message += $"\n\n{NoSectionWarning}\n{noSectionList}";
            }

            OperationHistory.Add($"開く: 必須ファイルなし {requiredMessages.Count}件 / 使えないパス {optionalProblems.Count}件 / 見つからない {optionalMissing.Count}件 / 読み込めない {loadErrors.Count}件 / 文字コード不一致 {encodingWarningFiles.Count}件 / 古い形式 {legacyFiles.Count}件 / [Power][Brake]なし {noSectionFiles.Count}件");
            // 古い形式だけなら「お知らせ」、ないファイル・読めないファイル・文字化けしそうなファイル・中身のおかしいファイルがあれば「警告」
            bool hasProblem = requiredMessages.Count > 0 || optionalProblems.Count > 0 || optionalMissing.Count > 0 || loadErrors.Count > 0 || encodingWarningFiles.Count > 0 || noSectionFiles.Count > 0;
            MessageBox.Show(message, Loc.T("MW.107"), MessageBoxButton.OK, hasProblem ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }

        // 保存ボタンなどのイベント
        private void SaveProject_Click(object sender, RoutedEventArgs e)
        {
            OperationHistory.Add("全て保存");

            if (!_project.IsLoaded)
            {
                OperationHistory.Add("全て保存: プロジェクト未読み込み");
                MessageBox.Show(Loc.T("MW.108"), Loc.T("MW.109"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 失敗したときのエラー表示は TrySaveAll の中で行う
            if (!TrySaveAll(out List<string> savedFileNames)) return;

            // 変更がなかったファイルは保存を飛ばしているので、実際に保存したものを知らせる
            // OK を押すメッセージの代わりに、ステータスバーに少しの間だけ出す（ユーザーの判断。保存できなかったときのメッセージは TrySaveAll がこれまでどおり出す）
            if (savedFileNames.Count == 0)
            {
                ShowStatusMessage(Loc.T("MW.110"));
                return;
            }

            ShowStatusMessage(savedFileNames.Count == 1 ? Loc.F("MW.111", savedFileNames[0]) : Loc.F("MW.112", savedFileNames[0], savedFileNames.Count - 1));
            UpdateEncodingStatus();
        }

        // すべてのファイルを保存する（「すべて保存」と、閉じるときの「保存して終了」で使う）
        // 成功したら true。保存したファイル名は savedFileNames に入る。失敗したらエラーを表示して false
        private bool TrySaveAll(out List<string> savedFileNames)
        {
            savedFileNames = [];
            try
            {
                SyncEditorToProject();
                savedFileNames = _project.Save();
                OperationHistory.Add($"全て保存: 成功（{savedFileNames.Count}件）");
                // 保存したので * を外す
                UpdateTabHeaders();
                // 保存していない変更がなくなったら、自動保存の控えは要らない
                if (GetUnsavedTabTitles().Count == 0) ClearAutoSave();
                return true;
            }
            catch (Exception ex)
            {
                OperationHistory.Add($"全て保存: エラー（{ex.GetType().Name}: {ex.Message}）");
                MessageBox.Show(Loc.F("MW.113", ErrorText.Describe(ex)), Loc.T("MW.114"), MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        // 「表示 → 言語」で選んだ言語（選んでいなければ null。閉じるときに覚える。null のあいだは Windows の表示言語に合わせる）
        private string? _chosenLanguage;

        // 「表示 → 言語」：日本語 / English を切り替えて覚える
        private void LanguageMenu_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem { Tag: string language }) return;
            _chosenLanguage = language;
            ApplyLanguage(language);
        }

        // 言語を当てはめる：XAML の {DynamicResource} はすぐ変わる。コードで作った文字（タブ名・ステータスバー）は作り直す
        private void ApplyLanguage(string? language)
        {
            _chosenLanguage = language;
            Loc.Apply(language);
            MenuLanguageJa.IsChecked = Loc.Current == Loc.Japanese;
            MenuLanguageEn.IsChecked = Loc.Current == Loc.English;
            UpdateTabHeaders();
            UpdateProjectStatus();
            // Vehicle タブの警告とお知らせ・参照欄の吹き出しも、新しい言語で出し直す（開いているときだけ）
            if (_project.IsLoaded) ShowVehiclePaths(writeHistory: false);
            // コードで作っている入力フォームの欄の名前・警告も、新しい言語で作り直す
            ParamForm.Refresh();
            PanelInputForm.Refresh();
            NotchInputForm.ApplyLanguage();
            MotorNoiseInputForm.ApplyLanguage();
            SoundInputForm.ApplyLanguage();
        }

        // ステータスバーのプロジェクトの場所（開いていなければ「未読込」。長くて省略されたときのために、マウスを乗せると全部見える）
        private void UpdateProjectStatus()
        {
            string? folder = _project.IsLoaded ? _project.BaseDirectory : null;
            TxtProjectStatus.Text = folder == null ? Loc.T("Status.ProjectNone") : Loc.F("Status.Project", folder);
            TxtProjectStatus.ToolTip = folder;
        }

        // 「表示 → テーマ」メニュー：システム / ライト / ダークを切り替える
        private void ThemeMenu_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem clickedItem) return;
            ApplyTheme(clickedItem.Tag as string ?? "System");
        }

        // テーマを切り替えて、メニューのチェックを合わせる（System / Light / Dark。起動時に覚えたテーマを当てはめるときにも使う）
        private void ApplyTheme(string themeName)
        {
            // ThemeMode は .NET では「試験的」扱いのため、警告 WPF0001 をここだけ止める
            // ユーザーから　下部にエラー一覧、参照ボタンとテキストボックス群にATSプラグインのファイルパスを置く予定で、さらに狭くなる可能性がある
#pragma warning disable WPF0001
            Application.Current.ThemeMode = themeName switch
            {
                "Light" => ThemeMode.Light,
                "Dark" => ThemeMode.Dark,
                _ => ThemeMode.System,
            };
#pragma warning restore WPF0001

            // 選んだものだけにチェックを付ける（同じものを2回押してもチェックが外れないようにする）
            MenuThemeSystem.IsChecked = themeName is not ("Light" or "Dark");
            MenuThemeLight.IsChecked = themeName == "Light";
            MenuThemeDark.IsChecked = themeName == "Dark";

            OperationHistory.Add($"テーマ変更: {themeName}");
        }

        // 「ヘルプ → バージョン情報」：アプリの名前・バージョン・開発者（GitHub へのリンク）と、.NET・部品の版のウィンドウ（AboutWindow.xaml）
        private void AboutMenu_Click(object sender, RoutedEventArgs e)
        {
            OperationHistory.Add("バージョン情報");
            new AboutWindow { Owner = this }.ShowDialog();
        }

        // 「ヘルプ → 設定のフォルダーを開く」（まだ設定を書いたことがなければ、フォルダーがないと知らせる）
        private void OpenSettingsFolder_Click(object sender, RoutedEventArgs e)
        {
            string folder = AppSettings.FolderPath;
            if (!Directory.Exists(folder))
            {
                MessageBox.Show(Loc.F("MW.115", folder), Loc.T("MW.116"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            OpenFolderInExplorer(folder);
        }

        // 「ヘルプ → クラッシュログのフォルダーを開く」（アプリの隣の CrashLogs か、なければ LocalAppData のほう。どちらもなければ、まだ止まったことがないと知らせる）
        private void OpenCrashLogFolder_Click(object sender, RoutedEventArgs e)
        {
            string[] folders = CrashLogger.GetCandidateFolders();
            string? folder = folders.FirstOrDefault(Directory.Exists);
            if (folder == null)
            {
                MessageBox.Show(Loc.F("MW.117", string.Join("\n", folders)), Loc.T("MW.118"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            OpenFolderInExplorer(folder);
        }

        // 「ファイル → バックアップの設定…」：控えを残すか・残す数を選ぶウィンドウ（BackupWindow.xaml）。OK なら当てはめる（閉じるときに覚える）
        private void BackupMenu_Click(object sender, RoutedEventArgs e)
        {
            BackupWindow window = new(BackupManager.Enabled, BackupManager.MaxCount, AutoSaveManager.Enabled, AutoSaveManager.Minutes) { Owner = this };
            if (window.ShowDialog() != true) return;
            BackupManager.Enabled = window.SelectedEnabled;
            BackupManager.MaxCount = window.SelectedMaxCount;
            AutoSaveManager.Enabled = window.SelectedAutoSaveEnabled;
            AutoSaveManager.Minutes = window.SelectedAutoSaveMinutes;
            // 間隔を変えたら、そこから数え直す
            RestartAutoSaveTimer();
            OperationHistory.Add($"バックアップの設定: {(window.SelectedEnabled ? "残す" : "残さない")}・{window.SelectedMaxCount} 個・自動保存{(window.SelectedAutoSaveEnabled ? "する" : "しない")}・{window.SelectedAutoSaveMinutes} 分");
        }

        // 自動保存のタイマー（「バックアップの設定」の間隔ごと）
        private readonly DispatcherTimer _autoSaveTimer = new();

        // 前に自動保存した中身（同じなら保存しない）
        private List<AutoSaveManager.Entry> _lastAutoSaved = [];

        // タブのキー（自動保存の控えのファイル名と AutoSave.json に使う。GetTabInfos と同じ順）
        private static readonly string[] AutoSaveKeys = ["Vehicle", "Parameters", "Panel", "PerformanceCurve", "MotorNoise", "Sound"];

        // 自動保存のタイマーを、今の設定で動かし直す（自動保存しないなら止める）
        private void RestartAutoSaveTimer()
        {
            _autoSaveTimer.Stop();
            _autoSaveTimer.Tick -= AutoSaveTimer_Tick;
            if (!AutoSaveManager.Enabled) return;
            _autoSaveTimer.Tick += AutoSaveTimer_Tick;
            _autoSaveTimer.Interval = TimeSpan.FromMinutes(AutoSaveManager.Minutes);
            _autoSaveTimer.Start();
        }

        // 間隔ごと：保存していない変更があるタブの中身を控えに書く（前と同じなら書かない。変更がなくなっていれば控えを消す）
        // ステータスバーに「自動保存中…」を出してから書き、終わったら「自動保存しました（時:分）」
        private void AutoSaveTimer_Tick(object? sender, EventArgs e)
        {
            if (!_project.IsLoaded || _project.VehicleFile.FullPath is not string vehiclePath) return;
            List<AutoSaveManager.Entry> entries = BuildAutoSaveEntries();
            if (entries.Count == 0)
            {
                if (_lastAutoSaved.Count > 0) ClearAutoSave();
                return;
            }
            if (entries.SequenceEqual(_lastAutoSaved)) return;

            ShowStatusMessage(Loc.T("MW.119"));
            // 「自動保存中…」を画面に出してから書く
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                if (!AutoSaveManager.Save(vehiclePath, entries))
                {
                    ShowStatusMessage(Loc.T("MW.120"));
                    return;
                }
                _lastAutoSaved = entries;
                ShowStatusMessage(Loc.F("MW.121", DateTime.Now));
            });
        }

        // 保存していない変更がある（読み込めた）タブの、キー・元のファイルの場所・編集中の中身
        private List<AutoSaveManager.Entry> BuildAutoSaveEntries()
        {
            List<AutoSaveManager.Entry> entries = [];
            var tabs = GetTabInfos();
            for (int i = 0; i < tabs.Length; i++)
            {
                (_, _, TextEditor editor, FileItem file) = tabs[i];
                if (!file.HasUnsavedChanges(editor.Text)) continue;
                entries.Add(new(AutoSaveKeys[i], file.FullPath, editor.Text));
            }
            return entries;
        }

        // 今の車両ファイルの自動保存の控えを消す（すべて保存して変更がなくなったとき・普通に閉じたとき・別のファイルを開く前）
        private void ClearAutoSave()
        {
            AutoSaveManager.Clear(_project.VehicleFile.FullPath);
            _lastAutoSaved = [];
        }

        // 「ヘルプ → バックアップのフォルダーを開く」（まだ控えを残したことがなければ、フォルダーがないと知らせる）
        private void OpenBackupFolder_Click(object sender, RoutedEventArgs e)
        {
            string folder = BackupManager.FolderPath;
            if (!Directory.Exists(folder))
            {
                MessageBox.Show(Loc.F("MW.122", folder), Loc.T("MW.123"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            OpenFolderInExplorer(folder);
        }

        // BVE5・BVE6 の exe の場所（"5" / "6" → 場所。まだ選んでいなければ null。閉じても覚える）
        private readonly Dictionary<string, string?> _bvePaths = [];

        // 「デバッグ → BVE5 / BVE6 を起動」：場所を覚えていなければ（なくなっていれば）選んでもらい、保存していない変更があれば確認してから起動する
        private void LaunchBve_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem { Tag: string version }) return;
            string? exePath = _bvePaths.GetValueOrDefault(version);
            if (exePath == null || !File.Exists(exePath)) exePath = ChooseBveExe(version);
            if (exePath == null) return;
            if (!ConfirmUnsavedBeforeLaunch(version)) return;

            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exePath) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exePath) });
                OperationHistory.Add($"BVE{version} を起動: {exePath}");
                ShowStatusMessage(Loc.F("MW.124", version));
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
            {
                OperationHistory.Add($"BVE{version} を起動できない: {exePath}（{ex.GetType().Name}: {ex.Message}）");
                MessageBox.Show(Loc.F("MW.125", version, ErrorText.Describe(ex), exePath), Loc.T("MW.126"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // 「デバッグ → BVE5 / BVE6 の場所を選び直す…」
        private void ChooseBve_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem { Tag: string version }) return;
            string? exePath = ChooseBveExe(version);
            if (exePath != null) ShowStatusMessage(Loc.F("MW.127", version));
        }

        // BVE の exe を選んでもらい、覚える。やめた・使えない場所なら null（使えない場所なら理由を出す）
        private string? ChooseBveExe(string version)
        {
            string? current = _bvePaths.GetValueOrDefault(version);
            string? currentFolder = current == null ? null : Path.GetDirectoryName(current);
            OpenFileDialog dialog = new()
            {
                Filter = Loc.T("MW.128"),
                Title = Loc.F("MW.129", version),
                InitialDirectory = currentFolder != null && Directory.Exists(currentFolder) ? currentFolder : string.Empty,
                CheckFileExists = true,
            };
            if (dialog.ShowDialog() != true) return null;

            // 参照欄と同じ決まり（使えない文字・名前の最後の空白やピリオド・260 文字以上など）で調べる（Windows のルールに従う）
            string? problem = VehicleFileParser.FindPathProblem(dialog.FileName, Path.GetDirectoryName(dialog.FileName) ?? string.Empty);
            if (problem != null)
            {
                MessageBox.Show(Loc.F("MW.130", dialog.FileName, problem), Loc.T("MW.131"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }

            _bvePaths[version] = dialog.FileName;
            OperationHistory.Add($"BVE{version} の場所: {dialog.FileName}");
            return dialog.FileName;
        }

        // BVE を起動する前：保存していない変更があれば「保存して起動 / 保存せずに起動 / キャンセル」を選んでもらう
        // BVE はファイルに保存した中身を読むので確認する。保存せずに起動しても、変更はこの画面に残る（終了のときの確認とは文が違うので分けた）
        private bool ConfirmUnsavedBeforeLaunch(string version)
        {
            List<string> unsavedTitles = GetUnsavedTabTitles();
            if (unsavedTitles.Count == 0) return true;

            string unsavedList = string.Join("\n", unsavedTitles.Select(title => Loc.F("MW.132", title)));
            MessageBoxResult answer = MessageBox.Show(
                Loc.F("MW.133", unsavedList) +
                Loc.F("MW.134", version),
                Loc.T("MW.135"), MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.No) return true;
            if (answer == MessageBoxResult.Yes && TrySaveAll(out _))
            {
                UpdateEncodingStatus();
                return true;
            }
            return false;
        }

        // エクスプローラーでフォルダーを開く（開けなければ日本語の理由を出す）
        private static void OpenFolderInExplorer(string folder)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
                OperationHistory.Add($"フォルダーを開く: {folder}");
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
            {
                OperationHistory.Add($"フォルダーを開けない: {folder}（{ex.GetType().Name}: {ex.Message}）");
                MessageBox.Show(Loc.F("MW.136", ErrorText.Describe(ex), folder), Loc.T("MW.137"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void Exit_Click(object sender, RoutedEventArgs e)
        {
            OperationHistory.Add("終了");
            Close();
        }
    }
}
