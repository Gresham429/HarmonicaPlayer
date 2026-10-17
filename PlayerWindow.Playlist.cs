using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace HarmonicaPlayer;

public sealed partial class PlayerWindow
{
    private readonly Playlist playlist = new();
    private readonly DataGrid playlistView = new() { AutoGenerateColumns = false, IsReadOnly = true,
        CanUserAddRows = false, CanUserDeleteRows = false, CanUserSortColumns = false,
        SelectionMode = DataGridSelectionMode.Single, SelectionUnit = DataGridSelectionUnit.FullRow, Height = 190 };
    private readonly TextBlock playlistStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox playlistIssues = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 120 };
    private readonly Button previousSong = new() { Content = "上一首", Margin = new Thickness(4) };
    private readonly Button nextSong = new() { Content = "下一首", Margin = new Thickness(4) };
    private readonly List<Button> playlistEditButtons = new();
    private bool playlistSwitching, refreshingPlaylist;
    private string playlistNotice = "";
    private CancellationTokenSource? playlistSwitchCancellation;
    private TaskCompletionSource? playbackCompletion, playlistSwitchCompletion;

    private void AddPlaylistControls(Panel panel)
    {
        panel.Children.Add(new TextBlock { Text = "播放列表 · 单击预选，双击加载；连续演奏至末尾停止", Margin = new Thickness(0, 10, 0, 4) });
        var row = new WrapPanel(); panel.Children.Add(row);
        void Button(string label, Func<Task> action)
        {
            var button = new Button { Content = label, Margin = new Thickness(4) };
            button.Click += async (_, _) => await action();
            playlistEditButtons.Add(button); row.Children.Add(button);
        }
        Button("添加 TXT…", AddPlaylistFilesAsync);
        Button("打开列表…", OpenPlaylistAsync);
        Button("保存列表…", SavePlaylistAsync);
        Button("上移", () => { MovePlaylistSelection(-1); return Task.CompletedTask; });
        Button("下移", () => { MovePlaylistSelection(1); return Task.CompletedTask; });
        Button("移除", () =>
        {
            if (playlist.SelectedId is Guid id) playlist.Remove(id);
            RefreshPlaylistView(); return Task.CompletedTask;
        });
        var clearSelection = new Button { Content = "取消预选", Margin = new Thickness(4) };
        clearSelection.Click += (_, _) => { playlist.Select(null); RefreshPlaylistView(); };
        row.Children.Add(clearSelection); row.Children.Add(previousSong); row.Children.Add(nextSong);
        previousSong.Click += async (_, _) => await NavigatePlaylistAsync(-1);
        nextSong.Click += async (_, _) => await NavigatePlaylistAsync(1);
        void Column(string title, string property, double width) => playlistView.Columns.Add(new DataGridTextColumn
        { Header = title, Binding = new Binding(property), Width = width });
        Column("曲名", nameof(PlaylistEntry.Title), 180); Column("BPM", nameof(PlaylistEntry.Bpm), 55);
        Column("间隔(ms)", nameof(PlaylistEntry.Gap), 75); Column("路径", nameof(PlaylistEntry.Path), 300);
        Column("错误", nameof(PlaylistEntry.Issue), 300);
        playlistView.ItemsSource = playlist.Items;
        playlistView.SelectionChanged += (_, e) =>
        {
            // Collection replacement can remove the selected row. Only a newly
            // selected row expresses a user preselection; restore identity below.
            if (!refreshingPlaylist && e.AddedItems.Count > 0 && e.AddedItems[0] is PlaylistEntry entry)
            { playlist.Select(entry.Id); UpdatePlaylistStatus(); UpdateAvailability(); }
        };
        playlistView.MouseDoubleClick += async (_, e) =>
        {
            if (e.OriginalSource is DependencyObject element &&
                ItemsControl.ContainerFromElement(playlistView, element) is DataGridRow item && item.Item is PlaylistEntry entry)
            { e.Handled = true; await SwitchPlaylistAsync(entry.Id, false, 0); }
        };
        panel.Children.Add(playlistView); panel.Children.Add(playlistStatus);
        panel.Children.Add(new Expander { Header = "列表导入／跳过记录", Content = playlistIssues });
    }
    private void UpdatePlaylistStatus()
    {
        string current = playlist.Find(playlist.CurrentId)?.Title ?? "编辑器曲谱（不在列表中）";
        string selected = playlist.Find(playlist.SelectedId)?.Title ?? "无，开始时使用编辑器内容";
        playlistStatus.Text = $"当前：{current}　|　预选：{selected}" + playlistNotice;
    }
    private void UpdatePlaylistAvailability()
    {
        bool editable = !uiBusy && !beginning && !editingHotkeys;
        foreach (var button in playlistEditButtons) button.IsEnabled = editable;
        bool navigable = !closing && !documentBusy && !editingHotkeys && !playlistSwitching;
        previousSong.IsEnabled = navigable && playlist.Neighbor(-1) != null;
        nextSong.IsEnabled = navigable && playlist.Neighbor(1) != null;
        playlistView.IsEnabled = !closing && !documentBusy && !editingHotkeys;
    }
    private void RefreshPlaylistView()
    {
        refreshingPlaylist = true;
        try { playlistView.SelectedItem = playlist.Find(playlist.SelectedId); }
        finally { refreshingPlaylist = false; }
        UpdatePlaylistStatus(); UpdateAvailability();
    }
    private void MovePlaylistSelection(int offset)
    {
        if (playlist.SelectedId is Guid id) playlist.Move(id, offset);
        RefreshPlaylistView();
    }
    private Task AddPlaylistFilesAsync() => DocumentOperation(async () =>
    {
        string[] paths = scoreDialogs.OpenPlaylistTracks(this);
        int fallback = int.TryParse(bpm.Text, out int value) && value is >= 20 and <= 300 ? value : settings.Bpm;
        var errors = new List<string>();
        foreach (string path in paths)
        {
            if (playlist.Items.Count >= Playlist.MaxEntries) { errors.Add($"已达到 {Playlist.MaxEntries} 项上限，其余文件未添加。"); break; }
            var entry = playlist.Add(path, fallback);
            try { await playlist.ReadAsync(entry.Id, CancellationToken.None); }
            catch (Exception e) when (Playlist.IsFileProblem(e)) { errors.Add($"{path}：{e.Message}"); }
        }
        ShowPlaylistSkips(errors); RefreshPlaylistView();
        if (paths.Length > 0) status.Text = $"播放列表共 {playlist.Items.Count} 首；错误项已标记。单击预选，双击加载。";
        return true;
    });
    private Task OpenPlaylistAsync() => DocumentOperation(async () =>
    {
        string? path = scoreDialogs.OpenPlaylist(this); if (path == null) return false;
        var entries = await PlaylistStore.ReadAsync(path);
        playlist.Replace(entries); // Editor content and its dirty flag remain intact.
        var errors = new List<string>();
        foreach (var entry in entries)
        {
            try { await playlist.ReadAsync(entry.Id, CancellationToken.None); }
            catch (Exception e) when (Playlist.IsFileProblem(e)) { errors.Add($"{entry.Path}：{e.Message}"); }
        }
        playlistIssues.Clear(); playlistNotice = ""; ShowPlaylistSkips(errors); RefreshPlaylistView();
        status.Text = $"已打开播放列表，共 {entries.Count} 首。"; return true;
    });
    private Task SavePlaylistAsync() => DocumentOperation(async () =>
    {
        string? path = scoreDialogs.SavePlaylist(this); if (path == null) return false;
        await PlaylistStore.SaveAsync(path, playlist.Items);
        status.Text = "播放列表已保存；曲谱内容请使用“保存 TXT”。"; return true;
    });
    private void ShowPlaylistSkips(IReadOnlyList<string> errors)
    {
        if (errors.Count == 0) return;
        if (playlistIssues.Text.Length > 50000) playlistIssues.Clear();
        playlistIssues.AppendText(string.Join("\n", errors) + "\n");
        playlistNotice = $"\n已跳过／标记 {errors.Count} 项，原因见“列表导入／跳过记录”。";
        UpdatePlaylistStatus();
    }
    private async Task<bool> LoadPlaylistEntryAsync(Guid id, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!await ConfirmUnsavedAsync()) return false;
        token.ThrowIfCancellationRequested();
        var result = await playlist.ReadAsync(id, token);
        token.ThrowIfCancellationRequested();
        ApplyDocument(result.Document, id);
        documentWarning.Text = string.Join("\n", result.Warnings);
        return true;
    }
    private Task NavigatePlaylistAsync(int direction, uint triggerKey = 0)
    {
        var target = playlist.Neighbor(direction);
        return target == null ? Task.CompletedTask : SwitchPlaylistAsync(target.Id, true, triggerKey);
    }
    private async Task SwitchPlaylistAsync(Guid id, bool play, uint triggerKey)
    {
        if (closing || documentBusy || editingHotkeys || playlistSwitching || playlist.Find(id) == null) return;
        playlistSwitching = true;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        playlistSwitchCompletion = completion;
        using var owned = new CancellationTokenSource(); playlistSwitchCancellation = owned;
        var token = owned.Token;
        bool loaded = false;
        try
        {
            cancellation?.Cancel();
            SetBusy(true);
            if (playbackCompletion != null) await playbackCompletion.Task;
            token.ThrowIfCancellationRequested();
            if (closing) return;
            if (playbackReleaseError != null) throw new InvalidOperationException("上次按键释放失败，请先处理并停止重试：" + playbackReleaseError);
            if (!await LoadPlaylistEntryAsync(id, token)) { status.Text = "已取消切换，保持停止。"; return; }
            playlist.ConsumeSelection(id); RefreshPlaylistView();
            loaded = true; status.Text = "已加载到编辑器，尚未播放。";
        }
        catch (OperationCanceledException) { if (!closing) status.Text = "切换已取消，保持停止。"; }
        catch (Exception e) { ReportIssue("切歌失败：" + e.Message); status.Text = "切歌失败，已停止。"; RefreshPlaylistView(); }
        finally
        {
            playlistSwitchCancellation = null; playlistSwitching = false;
            SetBusy(cancellation != null || beginning); completion.TrySetResult();
        }
        if (loaded && play && !token.IsCancellationRequested && !closing) await StartGamePlayback(triggerKey, false);
    }
}
