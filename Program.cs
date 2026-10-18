using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace HarmonicaPlayer;

public static class Program
{
    [STAThread]
    public static void Main() => SingleInstance.Run();
}

public sealed partial class PlayerWindow : Window
{
    private readonly TextBox songTitle = new() { Text = "未命名曲谱" };
    private readonly TextBlock documentInfo = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock documentWarning = new() { TextWrapping = TextWrapping.Wrap, Background = Brushes.LemonChiffon };
    private readonly Button saveScore = new() { Content = "保存 TXT", Margin = new Thickness(5) };
    private readonly Button saveAs = new() { Content = "另存为", Margin = new Thickness(5) };
    private readonly Button newScore = new() { Content = "新建", Margin = new Thickness(5) };
    private readonly IScoreDialogs scoreDialogs;
    private string? documentPath;
    private bool legacyDocument, documentBusy;
    private string[] extraHeaders = Array.Empty<string>();
    private (string Title, string Bpm, string Body, string Gap) cleanDocument;
    private long saveGeneration;
    private bool DocumentDirty => cleanDocument != (songTitle.Text, bpm.Text, score.Text, gap.Text);
    private void UpdateDocumentTitle()
    {
        Title = $"口琴简谱播放器 0.5.0 — {songTitle.Text}{(DocumentDirty ? " *" : "")}";
        documentInfo.Text = documentPath ?? "尚未保存";
        UpdatePlaylistStatus();
    }
    private void MarkDocumentClean() { cleanDocument = (songTitle.Text, bpm.Text, score.Text, gap.Text); UpdateDocumentTitle(); }
    private readonly TextBox score = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Height = 230, Text = "1234567 【1234567】 （1234567） #1 #2 #4 #5 #6 111 000" };
    private readonly TextBox gap = new() { Text = "20", Width = 75 };
    private readonly CheckBox dry = new() { Content = "仅测试（不会操作游戏）", IsChecked = false, Margin = new Thickness(0, 10, 0, 10) };
    private readonly TextBlock status = new() { Text = "就绪：先导入谱面，快捷键状态见上方。", TextWrapping = TextWrapping.Wrap };
    private readonly TextBox log = new() { IsReadOnly = true, Height = 110, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly Button start = new() { Content = "开始 F6", Margin = new Thickness(5), Padding = new Thickness(15, 6, 15, 6) };
    private readonly Button import = new() { Content = "导入 TXT", Margin = new Thickness(5), Padding = new Thickness(15, 6, 15, 6) };
    private readonly TextBox bpm = new() { Text = "120", Width = 75 };
    private readonly TextBox preview = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 130, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly Button refresh = new() { Content = "预览 / 检查", Margin = new Thickness(5), Padding = new Thickness(12, 6, 12, 6) };
    private readonly TextBlock alert = new() { TextWrapping = TextWrapping.Wrap, FontSize = 16, FontWeight = FontWeights.Bold, Foreground = Brushes.DarkRed };
    private readonly Border alertBox = new() { Background = Brushes.MistyRose, Padding = new Thickness(12), Margin = new Thickness(0, 6, 0, 6), Visibility = Visibility.Collapsed };
    private string? scoreIssue, hotkeyIssue, operationIssue;
    private int? errorPosition, operationErrorPosition;
    private bool operationIsRuntime;
    private string? playbackReleaseError;
    private long playbackGeneration;
    private bool uiBusy;
    private readonly Button locateError = new() { Content = "定位曲谱错误", Margin = new Thickness(4) };
    private readonly TextBlock startReason = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DarkRed };
    private void UpdateAvailability()
    {
        bool blockedByHotkey = dry.IsChecked != true && hotkeys?.StopReady != true;
        bool otherSelected = playlist.SelectedId != null && playlist.SelectedId != playlist.CurrentId;
        start.IsEnabled = !uiBusy && (scoreIssue == null || otherSelected) && !blockedByHotkey;
        startReason.Text = scoreIssue != null && !otherSelected ? "无法开始：请先修正曲谱或速度、音符间隔。" :
            blockedByHotkey ? "无法开始游戏演奏：请先修复停止快捷键。" : "";
        locateError.IsEnabled = !uiBusy && (errorPosition ?? operationErrorPosition) != null;
        listen.IsEnabled = listenFromCursor.IsEnabled = exportMidi.IsEnabled = !uiBusy && scoreIssue == null;
        UpdatePlaylistAvailability();
    }
    private void LocateError()
    {
        if (uiBusy || (errorPosition ?? operationErrorPosition) is not int position) return;
        position = Math.Clamp(position, 0, score.Text.Length);
        score.Focus(); score.Select(position, position < score.Text.Length ? 1 : 0);
        score.ScrollToLine(score.GetLineIndexFromCharacterIndex(position));
    }
    private void UpdateAlert()
    {
        if (operationIssue == null) { operationErrorPosition = null; operationIsRuntime = false; }
        UpdateMode();
        // Runtime failures stay first; preflight reminders follow the confirmed priority.
        var issues = new[] { operationIsRuntime ? operationIssue : null, PermissionHint, TestModeHint, scoreIssue,
            operationIsRuntime ? null : operationIssue, hotkeyIssue }.Where(x => !string.IsNullOrWhiteSpace(x));
        alert.Text = string.Join("\n\n", issues);
        alertBox.Visibility = alert.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        UpdateAvailability();
    }
    private void ReportIssue(string message, int? position = null, bool runtime = false)
    { operationIssue = message; operationErrorPosition = position; operationIsRuntime = runtime; UpdateAlert(); }
    private CancellationTokenSource? cancellation;
    private Task? running;
    public bool IsClosing => closing;
    private SettingsWriter settingsWriter = null!;
    private readonly string settingsPath;
    private readonly DispatcherTimer previewTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly NativeInput output = new();
    private HwndSource? source;
    private IntPtr hwnd;
    private bool allowClose, closing, editingHotkeys, beginning;
    private HotkeyController? hotkeys;
    private AppSettings settings = new();
    private readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly TextBlock settingsStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock hotkeyStatus = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6) };
    private readonly Button configure = new() { Content = "自定义快捷键", Margin = new Thickness(5), Padding = new Thickness(10, 6, 10, 6) };
    private readonly Button retry = new() { Content = "重试注册", Margin = new Thickness(5), Padding = new Thickness(10, 6, 10, 6) };
    private readonly Button stop = new() { Content = "停止 F8", Margin = new Thickness(5), Padding = new Thickness(15, 6, 15, 6) };

    public PlayerWindow(string? settingsFile = null, IScoreDialogs? dialogs = null, Action<AppSettings>? writeSettings = null, ILocalAudioPlayer? audioPlayer = null, bool? administrator = null, bool loadScoreLibrary = false)
    {
        this.administrator = administrator ?? DetectAdministrator();
        scoreDialogs = dialogs ?? new ScoreDialogs();
        localAudio = audioPlayer ?? new LocalAudioPlayer();
        settingsPath = settingsFile ?? SettingsStore.DefaultPath;
        Title = "口琴简谱播放器 0.5.0"; Width = 1180; Height = 900; MinWidth = 900; MinHeight = 600;
        var panel = new StackPanel { Margin = new Thickness(18) };
        var root = new DockPanel { Margin = new Thickness(8) };
        var fixedHeader = new StackPanel();
        fixedHeader.Children.Add(modeStatus);
        var alertPanel = new StackPanel();
        alertPanel.Children.Add(alert);
        var alertActions = new WrapPanel();
        var fixHotkeys = new Button { Content = "修改快捷键", Margin = new Thickness(4) };
        var retryHotkeys = new Button { Content = "重试注册", Margin = new Thickness(4) };
        var dismissIssue = new Button { Content = "清除上次操作提示", Margin = new Thickness(4) };
        fixHotkeys.Click += (_, _) => ConfigureHotkeys();
        retryHotkeys.Click += (_, _) => ApplyHotkeys();
        dismissIssue.Click += (_, _) => { operationIssue = null; UpdateAlert(); };
        locateError.Click += (_, _) => LocateError();
        alertActions.Children.Add(locateError);
        alertActions.Children.Add(fixHotkeys); alertActions.Children.Add(retryHotkeys); alertActions.Children.Add(dismissIssue);
        alertPanel.Children.Add(alertActions); alertBox.Child = alertPanel;
        fixedHeader.Children.Add(alertBox);
        status.FontSize = 19; status.FontWeight = FontWeights.Bold;
        status.Margin = new Thickness(12, 4, 12, 8);
        fixedHeader.Children.Add(status);
        DockPanel.SetDock(fixedHeader, Dock.Top); root.Children.Add(fixedHeader);
        // Playlist on the left (fills the height), editor and controls on the right.
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(360), MinWidth = 260 });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 500 });
        var playlistPanel = BuildPlaylistPanel();
        var splitter = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Brushes.Gainsboro };
        var editorScroll = new ScrollViewer { Content = panel };
        Grid.SetColumn(splitter, 1); Grid.SetColumn(editorScroll, 2);
        body.Children.Add(playlistPanel); body.Children.Add(splitter); body.Children.Add(editorScroll);
        root.Children.Add(body); Content = root;
        panel.Children.Add(new TextBlock { Text = "TXT → 单音口琴演奏", FontSize = 23 });
        panel.Children.Add(new TextBlock { Text = "【高音】 （低音） 【【1】】最高do、【【#1】】升半音；5:1.25指定总拍数；#或＃升半音；0休止，-或—延长一拍，_半拍，__四分之一拍，.附点。例：1 2_ 3_ 5 — | 0 6. 5_ 1 |", Margin = new Thickness(0, 10, 0, 10), TextWrapping = TextWrapping.Wrap });
        var controls = new WrapPanel { Orientation = Orientation.Horizontal };
        controls.Children.Add(import); controls.Children.Add(refresh); controls.Children.Add(start);
        controls.Children.Add(stop); panel.Children.Add(controls); panel.Children.Add(startReason);
        var fileControls = new WrapPanel();
        fileControls.Children.Add(newScore); fileControls.Children.Add(saveScore); fileControls.Children.Add(saveAs); fileControls.Children.Add(importMidi);
        panel.Children.Add(fileControls);
        AddListeningControls(panel);
        panel.Children.Add(new TextBlock { Text = "曲名（与文件名独立）：" });
        panel.Children.Add(songTitle); panel.Children.Add(documentInfo); panel.Children.Add(documentWarning);
        var shortcuts = new StackPanel { Orientation = Orientation.Horizontal };
        shortcuts.Children.Add(configure); shortcuts.Children.Add(retry); panel.Children.Add(shortcuts);
        panel.Children.Add(hotkeyStatus); panel.Children.Add(settingsStatus); panel.Children.Add(score);
        // All scores use beat-based timing.
        var tempo = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        tempo.Children.Add(new TextBlock { Text = "速度（BPM）：", VerticalAlignment = VerticalAlignment.Center });
        tempo.Children.Add(bpm); panel.Children.Add(tempo);
        panel.Children.Add(new TextBlock { Text = "解析预览（每个音的拍数和时间；不是图片识谱）", Margin = new Thickness(0, 8, 0, 4) });
        panel.Children.Add(preview);
        var timing = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        
        timing.Children.Add(new TextBlock { Text = "音符间隔／留白(ms)：", VerticalAlignment = VerticalAlignment.Center }); timing.Children.Add(gap);
        panel.Children.Add(timing);
        panel.Children.Add(dry);
        panel.Children.Add(new TextBlock
        {
            Text = "游戏演奏模式会操作键鼠；仅测试不会操作游戏，也不会发声。开始后倒计时3秒，请切到游戏口琴界面；演奏期间除停止键外不要操作键鼠。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8)
        });
        panel.Children.Add(log);
        settings = SettingsStore.Load(settingsPath, out var loadWarning);
        settingsWriter = new SettingsWriter(writeSettings ?? (value => SettingsStore.Save(settingsPath, value)),
            writeSettings == null && loadWarning == null && File.Exists(settingsPath) ? settings : null);
        bpm.Text = settings.Bpm.ToString(); gap.Text = settings.Gap.ToString();
        volumeSlider.Value = settings.PreviewVolume; localAudio.Volume = settings.PreviewVolume;
        settingsStatus.Text = loadWarning ?? "设置会自动保存；每次启动默认游戏演奏模式，“仅测试”不勾选。";
        saveTimer.Tick += async (_, _) => { saveTimer.Stop(); await SaveCurrentSettingsAsync(); };
        previewTimer.Tick += (_, _) => { previewTimer.Stop(); Preview(); };
        configure.Click += (_, _) => ConfigureHotkeys();
        retry.Click += (_, _) => ApplyHotkeys();
        import.Click += async (_, _) => await Import(); start.Click += async (_, _) => await Begin();
        importMidi.Click += async (_, _) => await ImportMidiAsync();
        saveScore.Click += async (_, _) => await DocumentOperation(() => SaveScoreAsync(false));
        saveAs.Click += async (_, _) => await DocumentOperation(() => SaveScoreAsync(true));
        newScore.Click += async (_, _) => await DocumentOperation(async () =>
        {
            if (!await ConfirmUnsavedAsync()) return false;
            ApplyDocument(new ScoreDocument("未命名曲谱", 120, "")); return true;
        });
        stop.Click += (_, _) => Stop();
        refresh.Click += (_, _) => Preview();
        score.TextChanged += (_, _) => QueuePreview();
        bpm.TextChanged += (_, _) => QueuePreview();
        gap.TextChanged += (_, _) => QueuePreview();
        foreach (var box in new[] { bpm, gap })
            box.TextChanged += (_, _) => QueueSave();
        foreach (var box in new[] { songTitle, bpm, score, gap })
            box.TextChanged += (_, _) => UpdateDocumentTitle();
        MarkDocumentClean();
        dry.Checked += (_, _) => UpdateAlert();
        dry.Unchecked += (_, _) => UpdateAlert();
        SetBusy(false); Preview();
        // Only the real app scans 简谱; window tests start with an empty list.
        if (loadScoreLibrary) Loaded += async (_, _) => await ReloadLibraryAsync(true);
        SourceInitialized += (_, _) =>
        {
            hwnd = new WindowInteropHelper(this).Handle;
            source = HwndSource.FromHwnd(hwnd); source.AddHook(Hook);
            hotkeys = new HotkeyController(new NativeHotkeys(hwnd));
            ApplyHotkeys();
        };
        Closing += (_, e) =>
        {
            if (allowClose) return;
            e.Cancel = true;
            if (closing) return;
            if (documentBusy) { ReportIssue("正在读写曲谱，请完成后再关闭。"); return; }
            closing = true;
            playlistSwitchCancellation?.Cancel();
            saveTimer.Stop(); previewTimer.Stop(); libraryRefreshTimer.Stop();
            cancellation?.Cancel(); // Cancel first; do not put disk I/O before it.
            SetBusy(true); stop.IsEnabled = false;
            status.Text = "正在停止演奏并关闭…";
            // Always leave the initial Closing event before calling Close again,
            // even when there is no playback task or every await completes inline.
            Dispatcher.BeginInvoke(DispatcherPriority.Normal,
                new Action(() => _ = CloseAfterStopAsync()));
        };
    }
    private async Task CloseAfterStopAsync()
    {
        try
        {
            Task? playback = playbackCompletion?.Task ?? running;
            if (playback != null)
            {
                if (await Task.WhenAny(playback, Task.Delay(1500)) != playback)
                    status.Text = "正在等待演奏任务停止，窗口仍可响应…";
                try { await playback; }
                catch (Exception e) { status.Text = "演奏结束异常：" + e.Message; }
            }
            if (playlistSwitchCompletion != null) await playlistSwitchCompletion.Task;
            // No background note sender remains before final cleanup begins.
            string? releaseError = await Task.Run(output.Release);
            if (releaseError != null)
            {
                // Do not silently exit with keys still held. A second close retries.
                status.Text = "按键释放失败，请手动按下并松开相关键后再关闭：" + releaseError; ReportIssue(status.Text, runtime: true);
                return;
            }
            if (!await ConfirmUnsavedAsync()) { status.Text = "已取消关闭。"; return; }
            Task save = SaveCurrentSettingsAsync(true);
            if (await Task.WhenAny(save, Task.Delay(1500)) != save)
                status.Text = "正在保存最后的设置，请稍候（窗口仍可响应）…";
            await save;
            hotkeys?.Suspend(); source?.RemoveHook(Hook); StopWatchingLibrary();
            allowClose = true; Close();
        }
        catch (Exception e) { status.Text = "关闭未完成，请重试：" + e.Message; ReportIssue(status.Text); }
        finally
        {
            if (!allowClose)
            {
                closing = false; stop.IsEnabled = true; SetBusy(false);
                Preview(); QueueSave(); // A cancelled close must restore validation/debouncing.
            }
        }
    }
    private void QueuePreview()
    {
        if (closing) return;
        // A runtime character location belongs to the old score/settings snapshot.
        if (operationErrorPosition != null) { operationIssue = null; operationErrorPosition = null; UpdateAlert(); }
        previewTimer.Stop(); previewTimer.Start();
    }

    private IntPtr Hook(IntPtr h, int message, IntPtr w, IntPtr l, ref bool handled)
    {
        if (message == 0x0312)
        {
            // Discard queued messages from old registrations after editing.
            uint packed = unchecked((uint)l.ToInt64());
            uint key = packed >> 16, modifiers = packed & 0xFFFF;
            if (!editingHotkeys && w.ToInt32() == 1 && hotkeys?.StartReady == true &&
                key == settings.Start.Key && modifiers == settings.Start.Modifiers) _ = Begin(key);
            if (!editingHotkeys && w.ToInt32() == 2 && hotkeys?.StopReady == true &&
                key == settings.Stop.Key && modifiers == settings.Stop.Modifiers) Stop();
            if (!editingHotkeys && w.ToInt32() == 3 && hotkeys?.PreviousReady == true &&
                settings.Previous is { } previous && key == previous.Key && modifiers == previous.Modifiers)
                _ = NavigatePlaylistAsync(-1, key);
            if (!editingHotkeys && w.ToInt32() == 4 && hotkeys?.NextReady == true &&
                settings.Next is { } next && key == next.Key && modifiers == next.Modifiers)
                _ = NavigatePlaylistAsync(1, key);
            handled = true;
        }
        return IntPtr.Zero;
    }
    private void QueueSave()
    {
        if (closing) return;
        ++saveGeneration; // Invalidate completion even during the debounce interval.
        saveTimer.Stop(); saveTimer.Start();
    }
    private async Task SaveCurrentSettingsAsync(bool preserveValidTiming = false)
    {
        long generation = ++saveGeneration;
        AppSettings next = settings with { PreviewVolume = (int)volumeSlider.Value };
        string? invalid = null;
        if (!int.TryParse(bpm.Text, out int tempo) || !int.TryParse(gap.Text, out int silence))
            invalid = "部分数值尚未填写完整，保留上次有效设置。";
        else
        {
            var candidate = next with { Bpm = tempo, Gap = silence };
            try { candidate.Validate(); next = candidate; }
            catch (FormatException e) { invalid = e.Message; }
        }
        if (invalid != null && !preserveValidTiming)
        { settingsStatus.Text = invalid; return; }
        settings = next; // Update the in-memory snapshot before asynchronous disk work.
        string? error = await settingsWriter.SaveAsync(next);
        if (generation != saveGeneration) return; // Never replace a newer edit/error's status.
        settingsStatus.Text = error != null ? "设置未保存：" + error :
            invalid ?? "设置已保存（快捷键、速度、音符间隔和试听音量）。";
    }
    private void ApplyHotkeys()
    {
        if (hotkeys == null || cancellation != null || closing || beginning || editingHotkeys || documentBusy || playlistSwitching) return;
        hotkeys.Apply(settings.Start, settings.Stop, settings.Previous, settings.Next);
        UpdateMode(); stop.Content = "停止 " + settings.Stop.Label;
        hotkeyStatus.Text = hotkeys.Describe(settings.Start, settings.Stop, settings.Previous, settings.Next);
        hotkeyIssue = hotkeys.StartReady && hotkeys.StopReady &&
            (settings.Previous == null || hotkeys.PreviousReady) && (settings.Next == null || hotkeys.NextReady) ? null :
            hotkeyStatus.Text + "\n常见原因：其他播放器、录屏或键盘工具占用了热键。点击“自定义快捷键”换一个组合，或关闭占用程序后点“重试注册”。管理员权限不能解除热键占用。";
        UpdateAlert();
    }
    private void ConfigureHotkeys()
    {
        if (hotkeys == null || cancellation != null || closing || beginning || editingHotkeys || documentBusy || playlistSwitching) return;
        editingHotkeys = true; hotkeys.Suspend();
        try
        {
            var dialog = new HotkeyDialog(settings.Start, settings.Stop, settings.Previous, settings.Next) { Owner = this };
            if (dialog.ShowDialog() == true)
            {
                settings = settings with { Start = dialog.Start, Stop = dialog.Stop, Previous = dialog.Previous, Next = dialog.Next };
                _ = SaveCurrentSettingsAsync(true);
            }
        }
        finally { editingHotkeys = false; ApplyHotkeys(); }
    }
    private async Task WaitForRelease(uint triggerKey, CancellationToken token)
    {
        var timeout = Stopwatch.StartNew();
        while (NativeHotkeys.Held(triggerKey))
        {
            token.ThrowIfCancellationRequested();
            if (timeout.Elapsed.TotalSeconds > 15)
                throw new InvalidOperationException("等待松开快捷键超时，已取消演奏。");
            status.Text = $"请松开启动键及 Ctrl / Alt / Shift / Win；{settings.Stop.Label} 可停止。";
            await Task.Delay(20, token);
        }
        token.ThrowIfCancellationRequested();
    }
    private async Task DocumentOperation(Func<Task<bool>> action)
    {
        if (cancellation != null || beginning || editingHotkeys || closing || documentBusy || playlistSwitching) return;
        documentBusy = true; SetBusy(true);
        try { await action(); }
        catch (Exception e) { ReportIssue("曲谱操作失败：" + e.Message); }
        finally { documentBusy = false; SetBusy(false); }
    }
    private Task Import() => DocumentOperation(async () =>
    {
        string? path = scoreDialogs.Open(this);
        if (path == null) return false;
        int fallback = int.TryParse(bpm.Text, out int value) && value is >= 20 and <= 300 ? value : settings.Bpm;
        var result = await ScoreDocumentReader.ReadAsync(path, fallback);
        if (!await ConfirmUnsavedAsync()) return false;
        // Saving the current document may have updated the very file selected for import.
        result = await ScoreDocumentReader.ReadAsync(path, fallback);
        ApplyDocument(result.Document);
        documentWarning.Text = string.Join("\n", result.Warnings);
        status.Text = scoreIssue == null ? "已导入，检查通过；请确认速度（BPM）。" : "已导入，请修正顶部提示的错误。";
        return true;
    });
    private void ApplyDocument(ScoreDocument document, Guid? playlistId = null)
    {
        playlist.SetCurrent(playlistId);
        documentPath = document.SourcePath; legacyDocument = document.IsLegacy;
        extraHeaders = document.ExtraHeaders ?? Array.Empty<string>();
        songTitle.Text = document.Title; bpm.Text = document.Bpm.ToString(); score.Text = document.ScoreText;
        gap.Text = document.Gap.ToString();
        documentWarning.Text = ""; operationIssue = null; MarkDocumentClean(); Preview(); RefreshPlaylistView();
    }
    private async Task<bool> ConfirmUnsavedAsync()
    {
        if (!DocumentDirty) return true;
        return scoreDialogs.Unsaved(this) switch
        {
            MessageBoxResult.No => true,
            MessageBoxResult.Yes => await SaveScoreAsync(false),
            _ => false
        };
    }
    private async Task<bool> SaveScoreAsync(bool forceSaveAs)
    {
        try
        {
            var document = new ScoreDocument(songTitle.Text, PlaybackValidation.ParseBpm(bpm.Text), score.Text,
                documentPath, legacyDocument, extraHeaders, PlaybackValidation.ParseGap(gap.Text));
            document.Validate(gap.Text); // Validate before asking for a destination.
            string? path = documentPath;
            if (forceSaveAs || legacyDocument || path == null)
            {
                string name = string.Concat(songTitle.Text.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
                path = scoreDialogs.Save(this, name + (legacyDocument ? "-新版" : "") + ".txt");
                if (path == null) return false;
            }
            await ScoreDocumentWriter.SaveAsync(path, document, gap.Text);
            documentPath = Path.GetFullPath(path); legacyDocument = false;
            if (playlist.CurrentId is Guid currentId)
                playlist.UpdateDocument(currentId, document with { SourcePath = documentPath, IsLegacy = false });
            RefreshPlaylistView();
            documentWarning.Text = string.Join("\n", ScoreDocumentReader.Parse(
                ScoreDocumentWriter.Serialize(document, gap.Text), documentPath, document.Bpm).Warnings);
            MarkDocumentClean(); operationIssue = null; UpdateAlert();
            status.Text = "曲谱已保存（新格式，含曲名、BPM和音符间隔）。";
            return true;
        }
        catch (Exception e) { ReportIssue("曲谱未保存：" + e.Message); return false; }
    }
    private void Stop()
    {
        if (closing) return; // Shutdown owns final input cleanup; do not race it.
        playlistSwitchCancellation?.Cancel();
        cancellation?.Cancel();
        if (cancellation != null) status.Text = listening ? "正在停止试听…" : "正在停止并释放输入…";
        else { var error = output.Release(); playbackReleaseError = error; status.Text = error ?? "已停止。"; if (error != null) ReportIssue("释放按键失败：" + error, runtime: true); }
    }
    private Task Begin(uint triggerKey = 0) => StartGamePlayback(triggerKey, true);
    private Task StartGamePlayback(uint triggerKey, bool useSelection)
    {
        if (cancellation != null || closing || beginning || editingHotkeys || documentBusy || playlistSwitching) return Task.CompletedTask;
        var owned = new CancellationTokenSource(); cancellation = owned;
        playbackCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        beginning = true; ++playbackGeneration; SetBusy(true);
        operationIssue = null; UpdateAlert();
        Guid? selected = useSelection ? playlist.SelectedId : null;
        running = PlaySequenceAsync(triggerKey, selected, owned, playbackCompletion);
        return running;
    }
    private async Task PlaySequenceAsync(uint triggerKey, Guid? selected, CancellationTokenSource owned, TaskCompletionSource completion)
    {
        // Publish the owning task before a synchronous validation failure can finish.
        await Task.Yield();
        var token = owned.Token;
        bool loadingSelection = false;
        try
        {
            if (selected is Guid id)
            {
                loadingSelection = id != playlist.CurrentId;
                if (loadingSelection && !await LoadPlaylistEntryAsync(id, token))
                { status.Text = "已取消加载，保持停止。"; return; }
                loadingSelection = false;
                playlist.ConsumeSelection(id); RefreshPlaylistView();
            }
            token.ThrowIfCancellationRequested();
            bool simulation = dry.IsChecked == true;
            if (!simulation && hotkeys?.StopReady != true)
                throw new InvalidOperationException($"停止键 {settings.Stop.Label} 不可用，禁止真实演奏。请点击“自定义快捷键”或“重试注册”。");
            _ = SaveCurrentSettingsAsync();
            SetBusy(true); log.Clear();
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var notes = ScoreParser.Parse(score.Text);
                double ms = Timing(); int silence = ValidateGap(notes, ms);
                scoreIssue = null; errorPosition = null; UpdateAlert();
                ++playbackGeneration;
                if (!await Run(notes, ms, silence, simulation, triggerKey, token)) break;
                ++playbackGeneration; // Discard the completed song's queued progress while loading the next.
                token.ThrowIfCancellationRequested();
                var advance = await playlist.AdvanceAsync(DocumentDirty, token);
                ShowPlaylistSkips(advance.Skipped); RefreshPlaylistView();
                if (advance.BlockedByEdits) { status.Text = "当前曲目有未保存修改，连续播放已停止，请先保存或处理修改。"; break; }
                if (advance.Entry == null || advance.Score == null)
                {
                    if (playlist.CurrentId != null) status.Text = "播放列表已结束。";
                    break;
                }
                token.ThrowIfCancellationRequested();
                ApplyDocument(advance.Score.Document, advance.Entry.Id);
                documentWarning.Text = string.Join("\n", advance.Score.Warnings);
                triggerKey = 0; // Every song still goes through Run's three-second countdown.
            }
        }
        catch (OperationCanceledException) { if (!closing) status.Text = "已停止。"; }
        catch (FormatException e)
        {
            // A location in an unloaded file must never select unrelated editor text.
            if (loadingSelection) ReportIssue($"预选曲目无法加载（{playlist.Find(selected)?.Path}）：{e.Message}");
            else operationIssue = null; // Preview below owns current editor validation.
            status.Text = "无法开始，请查看顶部提示。";
        }
        catch (Exception e)
        {
            status.Text = "无法开始，请查看顶部提示。";
            if (dry.IsChecked != true && hotkeys?.StopReady != true) UpdateAlert();
            else ReportIssue(e.Message);
        }
        finally
        {
            owned.Cancel(); owned.Dispose(); // Invalidate queued progress from the completed run.
            cancellation = null; running = null; beginning = false; SetBusy(false); Preview();
            completion.TrySetResult();
        }
    }
    private int ValidateGap(List<ScoreNote> notes, double ms)
        => PlaybackValidation.ValidateGap(gap.Text, notes, ms);
    private double Timing()
    {
        return 60000.0 / PlaybackValidation.ParseBpm(bpm.Text);
    }
    private void Preview()
    {
        if (cancellation != null || closing) return;
        try
        {
            var notes = ScoreParser.Parse(score.Text);
            double ms = Timing();
            ValidateGap(notes, ms);
            double total = 0;
            var lines = new StringBuilder();
            foreach (var note in notes)
            {
                if (lines.Length < 30000)
                    lines.AppendLine($"{total / 1000:0.###}s  {note.Label}  {note.Beats:0.###}拍  ({note.Beats * ms:0.##}ms)");
                total += note.Beats * ms;
            }
            scoreIssue = null; errorPosition = null; UpdateAlert();
            preview.Text = $"共{notes.Count}个音/休止，{notes.Sum(n => n.Beats):0.###}拍，预计{total / 1000:0.###}秒（不含倒计时）\n" + lines;
        }
        catch (Exception e) { errorPosition = (e as ScoreFormatException)?.Position; scoreIssue = "谱面／设置错误：" + e.Message; preview.Text = scoreIssue; UpdateAlert(); }
    }
    private void SetBusy(bool busy)
    {
        busy |= closing || documentBusy || playlistSwitching;
        uiBusy = busy;
        import.IsEnabled = importMidi.IsEnabled = refresh.IsEnabled = gap.IsEnabled = dry.IsEnabled = !busy;
        bpm.IsEnabled = !busy;
        configure.IsEnabled = retry.IsEnabled = !busy;
        score.IsReadOnly = busy;
        songTitle.IsReadOnly = busy;
        saveScore.IsEnabled = saveAs.IsEnabled = newScore.IsEnabled = !busy;
        UpdateAlert();
    }
    private async Task<bool> Run(List<ScoreNote> notes, double ms, int silence, bool simulation, uint triggerKey, CancellationToken token)
    {
        string result = simulation ? "测试完成：未操作游戏，也未发声。需要游戏演奏请取消“仅测试”。" : "演奏完成。";
        bool failed = false, released = true;
        PlaybackTimingException? timingFailure = null;
        string playbackBody = score.Text;
        long generation = playbackGeneration;
        try
        {
            await WaitForRelease(triggerKey, token);
            for (int n = 3; n > 0; n--)
            { status.Text = $"{(!simulation && !administrator ? "权限提醒：未以管理员运行，允许继续。\n" : "")}{(simulation ? "仅测试" : "游戏演奏")}：{n}秒后开始，请切到目标窗口…"; await Task.Delay(1000, token); }
            // Check again after countdown: the user may have used Alt+Tab to switch windows.
            await WaitForRelease(triggerKey, token);
            var target = NativeInput.GetForegroundWindow();
            NativeInput.GetWindowThreadProcessId(target, out uint pid);
            if (!simulation && (target == IntPtr.Zero || pid == (uint)Environment.ProcessId))
                throw new InvalidOperationException("倒计时结束时仍在播放器窗口，已取消。请在3秒倒计时内切到游戏口琴界面，或在游戏内按开始快捷键。");
            await Task.Run(() =>
            {
                var schedule = GamePlaybackTiming.Create(notes, ms, silence);
                var clock = Stopwatch.StartNew();
                void Check()
                {
                    token.ThrowIfCancellationRequested();
                    if (!simulation && NativeInput.GetForegroundWindow() != target)
                        throw new InvalidOperationException("目标窗口失去焦点，已停止。演奏期间请勿切换窗口；切回游戏后重新开始。");
                }
                void Wait(double until)
                {
                    while (true)
                    {
                        Check(); double remaining = until - clock.Elapsed.TotalMilliseconds;
                        if (remaining <= 0) return;
                        if (remaining > 2) token.WaitHandle.WaitOne((int)Math.Min(5, Math.Max(1, remaining - 1)));
                        else Thread.SpinWait(100);
                    }
                }
                var initialError = simulation ? null : output.Release();
                if (initialError != null) throw new InvalidOperationException(initialError);
                for (int i = 0; i < schedule.Count; i++)
                {
                    var step = schedule[i];
                    var note = step.Note;
                    double noteMs = step.DurationMs;
                    double begin = step.StartMs;
                    Wait(begin); Check();
                    // Keep the original deadlines; tiny rests receive independent jitter tolerance.
                    PlaybackTimingGuard.Check(step, clock.Elapsed.TotalMilliseconds, playbackBody, i);
                    int index = i;
                    Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                    {
                    if (closing || token.IsCancellationRequested || playbackGeneration != generation) return;
                        status.Text = $"{index + 1}/{notes.Count}：{note.Label} / {note.Beats:0.###}拍";
                        if (log.LineCount > 150) log.Clear();
                        log.AppendText($"{begin:0}ms  {note.Label}  {note.Beats:0.###}拍 ({noteMs:0.##}ms)\n"); log.ScrollToEnd();
                    }));
                    if (note.Degree != 0)
                    {
                        PlaybackTimingGuard.Check(step, clock.Elapsed.TotalMilliseconds, playbackBody, i);
                        if (!simulation) output.Modifiers(note);
                        Wait(step.KeyDownMs!.Value); Check();
                        PlaybackTimingGuard.Check(step, clock.Elapsed.TotalMilliseconds, playbackBody, i, step.KeyDownMs.Value);
                        if (!simulation) output.NoteOn(note);
                        Wait(step.KeyUpMs!.Value);
                        var error = simulation ? null : output.Release();
                        if (error != null) throw new InvalidOperationException(error);
                    }
                    Wait(step.EndMs);
                }
            }, token);
        }
        catch (OperationCanceledException) { result = "已停止。"; }
        catch (Exception e) { failed = true; timingFailure = e as PlaybackTimingException; result = "已停止：" + e.Message; }
        finally
        {
            // 工作任务已退出，再做最终释放，避免释放之后仍有旧任务按键。
            var error = simulation ? null : await Task.Run(output.Release);
            playbackReleaseError = error;
            released = error == null;
            if (!closing)
            {
                status.Text = error == null ? result : result + " 释放失败，请手动按下并松开相关键：" + error;
                if (failed || error != null) ReportIssue(status.Text, timingFailure?.Position, runtime: true);
                if (timingFailure != null)
                {
                    string message = status.Text; int position = timingFailure.Position;
                    _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                    {
                        // Show after cancellation cleanup; discard an old run's queued dialog.
                        if (!closing && cancellation == null && playbackGeneration == generation && operationErrorPosition == position && operationIssue == message)
                            scoreDialogs.PlaybackIssue(this, message);
                    }));
                }
            }
        }
        return !failed && released && !token.IsCancellationRequested;
    }
}
