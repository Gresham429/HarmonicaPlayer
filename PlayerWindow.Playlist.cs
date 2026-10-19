using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;

namespace HarmonicaPlayer;

public sealed partial class PlayerWindow
{
    private readonly Playlist playlist = new();
    private readonly DataGrid playlistView = new() { AutoGenerateColumns = false, IsReadOnly = true,
        CanUserAddRows = false, CanUserDeleteRows = false, CanUserSortColumns = false, HeadersVisibility = DataGridHeadersVisibility.Column,
        SelectionMode = DataGridSelectionMode.Single, SelectionUnit = DataGridSelectionUnit.FullRow, GridLinesVisibility = DataGridGridLinesVisibility.None };
    private readonly TextBox playlistSearch = new() { Padding = new Thickness(4, 3, 4, 3) };
    private readonly TextBlock playlistStatus = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBox playlistIssues = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 120 };
    private readonly Button previousSong = new() { Content = "⏮ 上一首", Margin = new Thickness(4), Padding = new Thickness(12, 5, 12, 5) };
    private readonly Button nextSong = new() { Content = "下一首 ⏭", Margin = new Thickness(4), Padding = new Thickness(12, 5, 12, 5) };
    private readonly List<Button> playlistEditButtons = new();
    private ListCollectionView? playlistGroups;
    private bool playlistSwitching, refreshingPlaylist, playlistRevealPending;
    private string playlistNotice = "";
    private CancellationTokenSource? playlistSwitchCancellation;
    private TaskCompletionSource? playbackCompletion, playlistSwitchCompletion;
    // Release packages keep 简谱 beside the EXE; source builds find it in a parent folder.
    private readonly string? scoreLibrary = ScoreLibrary.FindFolder(AppContext.BaseDirectory);
    // Folder changes are debounced and applied only while idle; a list opened
    // from .hpl or extended with outside files is never replaced automatically.
    private FileSystemWatcher? libraryWatcher;
    private readonly DispatcherTimer libraryRefreshTimer = new() { Interval = TimeSpan.FromMilliseconds(1000) };
    private bool libraryListActive;

    private void WatchLibrary()
    {
        if (scoreLibrary == null || libraryWatcher != null) return;
        libraryRefreshTimer.Tick += async (_, _) =>
        {
            if (closing) { libraryRefreshTimer.Stop(); return; }
            if (!libraryListActive) { libraryRefreshTimer.Stop(); return; }
            // Busy (playing, countdown, preview, file work): keep waiting.
            if (uiBusy || beginning || editingHotkeys || documentBusy || playlistSwitching || cancellation != null) return;
            libraryRefreshTimer.Stop();
            await ReloadLibraryAsync(false, true);
        };
        try
        {
            libraryWatcher = new FileSystemWatcher(scoreLibrary) { IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite };
            void Changed(object? _, FileSystemEventArgs e) => Dispatcher.BeginInvoke(() =>
            {
                if (closing || !libraryListActive) return;
                libraryRefreshTimer.Stop(); libraryRefreshTimer.Start(); // Restart = debounce bursts.
            });
            libraryWatcher.Created += Changed; libraryWatcher.Deleted += Changed;
            libraryWatcher.Changed += Changed; libraryWatcher.Renamed += Changed;
            libraryWatcher.Error += (_, _) => Dispatcher.BeginInvoke(() => { if (!closing) { libraryRefreshTimer.Stop(); libraryRefreshTimer.Start(); } });
            libraryWatcher.EnableRaisingEvents = true;
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        { libraryWatcher?.Dispose(); libraryWatcher = null; playlistNotice = "\n无法监视简谱文件夹，请手动点“刷新简谱”：" + e.Message; UpdatePlaylistStatus(); }
    }
    private void StopWatchingLibrary()
    {
        libraryRefreshTimer.Stop();
        libraryWatcher?.Dispose(); libraryWatcher = null;
    }

    private FrameworkElement BuildPlaylistPanel()
    {
        var panel = new DockPanel { Margin = new Thickness(10, 4, 6, 10) };
        void Top(UIElement element) { DockPanel.SetDock(element, Dock.Top); panel.Children.Add(element); }
        void Bottom(UIElement element) { DockPanel.SetDock(element, Dock.Bottom); panel.Children.Add(element); }
        Top(new TextBlock { Text = "播放列表", FontSize = 18, FontWeight = FontWeights.Bold });
        Top(new TextBlock { Text = "单击预选，双击加载；按歌手分组，连续演奏至末尾停止。", Foreground = Brushes.DimGray,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 6) });

        // WPF has no placeholder text; show a hint behind the empty search box.
        var searchHint = new TextBlock { Text = "🔍 搜索曲名或歌手", Foreground = Brushes.Gray, Margin = new Thickness(7, 4, 0, 0), IsHitTestVisible = false };
        var search = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        search.Children.Add(playlistSearch); search.Children.Add(searchHint);
        playlistSearch.TextChanged += (_, _) =>
        {
            searchHint.Visibility = playlistSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            playlistGroups?.Refresh(); RefreshPlaylistView();
        };
        Top(search);

        var row = new WrapPanel(); Top(row);
        void Button(string label, string tip, Func<Task> action)
        {
            var button = new Button { Content = label, ToolTip = tip, Margin = new Thickness(2), Padding = new Thickness(6, 2, 6, 2) };
            button.Click += async (_, _) => await action();
            playlistEditButtons.Add(button); row.Children.Add(button);
        }
        Button("刷新简谱", "重新扫描“简谱”文件夹（每个子文件夹是一位歌手）", () => ReloadLibraryAsync(false));
        Button("添加 TXT…", "把其他 TXT 加入列表", AddPlaylistFilesAsync);
        Button("打开列表…", "打开保存的 .hpl 播放列表", OpenPlaylistAsync);
        Button("保存列表…", "保存为 .hpl 播放列表", SavePlaylistAsync);
        Button("上移", "在同一歌手内上移", () => { MovePlaylistSelection(-1); return Task.CompletedTask; });
        Button("下移", "在同一歌手内下移", () => { MovePlaylistSelection(1); return Task.CompletedTask; });
        Button("移除", "从列表移除（不删除文件）", () =>
        {
            if (playlist.SelectedId is Guid id) playlist.Remove(id);
            RefreshPlaylistView(); return Task.CompletedTask;
        });
        var clearSelection = new Button { Content = "取消预选", Margin = new Thickness(2), Padding = new Thickness(6, 2, 6, 2) };
        clearSelection.Click += (_, _) => { playlist.Select(null); RefreshPlaylistView(); };
        row.Children.Add(clearSelection);

        var navigation = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        navigation.Children.Add(previousSong); navigation.Children.Add(nextSong);
        previousSong.Click += async (_, _) => await NavigatePlaylistAsync(-1);
        nextSong.Click += async (_, _) => await NavigatePlaylistAsync(1);
        Bottom(new Expander { Header = "列表导入／跳过记录", Content = playlistIssues });
        Bottom(playlistStatus); Bottom(navigation);

        ConfigurePlaylistView();
        panel.Children.Add(playlistView);
        return panel;
    }
    private void ConfigurePlaylistView()
    {
        void Column(string title, Binding binding, DataGridLength width) => playlistView.Columns.Add(new DataGridTextColumn
        { Header = title, Binding = binding, Width = width });
        Column("曲名", new Binding(nameof(PlaylistEntry.Title)), new DataGridLength(1, DataGridLengthUnitType.Star));
        Column("BPM", new Binding(nameof(PlaylistEntry.Bpm)), 45);
        Column("间隔", new Binding(nameof(PlaylistEntry.Gap)), 45);
        Column("", new Binding(nameof(PlaylistEntry.Issue)) { Converter = new IssueMarkConverter() }, 28);

        var groups = new ListCollectionView(playlist.Items);
        groups.GroupDescriptions.Add(new PropertyGroupDescription(nameof(PlaylistEntry.Artist)));
        groups.Filter = item => item is PlaylistEntry entry && MatchesSearch(entry);
        playlistGroups = groups; playlistView.ItemsSource = groups;
        playlistView.GroupStyle.Add((GroupStyle)XamlReader.Parse("""
            <GroupStyle xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
              <GroupStyle.ContainerStyle>
                <Style TargetType="GroupItem">
                  <Setter Property="Template">
                    <Setter.Value>
                      <ControlTemplate TargetType="GroupItem">
                        <Expander IsExpanded="True" Margin="0,2,0,0">
                          <Expander.Header>
                            <TextBlock FontWeight="Bold"><Run Text="{Binding Name, Mode=OneWay}"/><Run Text="（"/><Run Text="{Binding ItemCount, Mode=OneWay}"/><Run Text=" 首）"/></TextBlock>
                          </Expander.Header>
                          <ItemsPresenter/>
                        </Expander>
                      </ControlTemplate>
                    </Setter.Value>
                  </Setter>
                </Style>
              </GroupStyle.ContainerStyle>
            </GroupStyle>
            """));

        // The grid's Tag carries the current song id; that row is highlighted.
        var rowStyle = new Style(typeof(DataGridRow));
        rowStyle.Setters.Add(new Setter(ToolTipProperty, new Binding { Converter = new EntryTipConverter() }));
        var current = new DataTrigger { Value = true, Binding = new MultiBinding { Converter = new SameIdConverter(), Bindings =
        {
            new Binding(nameof(PlaylistEntry.Id)),
            new Binding(nameof(Tag)) { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(DataGrid), 1) },
        } } };
        current.Setters.Add(new Setter(BackgroundProperty, new SolidColorBrush(Color.FromRgb(0xD8, 0xF0, 0xD8))));
        current.Setters.Add(new Setter(FontWeightProperty, FontWeights.Bold));
        rowStyle.Triggers.Add(current);
        playlistView.RowStyle = rowStyle;

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
    }
    private bool MatchesSearch(PlaylistEntry entry)
    {
        string text = playlistSearch.Text.Trim();
        return text.Length == 0 || entry.Title.Contains(text, StringComparison.OrdinalIgnoreCase) ||
            entry.Artist.Contains(text, StringComparison.OrdinalIgnoreCase);
    }
    private void UpdatePlaylistStatus()
    {
        var currentEntry = playlist.Find(playlist.CurrentId);
        playlistView.Tag = currentEntry?.Id;
        string current = currentEntry is { } c ? $"{c.Artist} · {c.Title}" : "编辑器曲谱（不在列表中）";
        string selected = playlist.Find(playlist.SelectedId)?.Title ?? "无，开始时使用编辑器内容";
        playlistStatus.Text = $"▶ 当前：{current}\n☆ 预选：{selected}\n共 {playlist.Items.Count} 首" + playlistNotice;
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
        RevealPlaylistFocus();
        UpdatePlaylistStatus(); UpdateAvailability();
    }
    // Rescans, moves and filtering rebuild the rows and reset the scroll position.
    // After layout settles, bring the preselected (else current) song back into
    // view, expanding its artist group if it was collapsed.
    private void RevealPlaylistFocus()
    {
        if (playlistRevealPending) return;
        playlistRevealPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            playlistRevealPending = false;
            var target = playlist.Find(playlist.SelectedId) ?? playlist.Find(playlist.CurrentId);
            if (closing || target == null || playlistGroups?.Contains(target) != true) return;
            var group = playlistGroups.Groups?.OfType<CollectionViewGroup>().FirstOrDefault(g => g.Items.Contains(target));
            if (group != null && playlistView.ItemContainerGenerator.ContainerFromItem(group) is GroupItem container &&
                FindDescendant<Expander>(container) is { IsExpanded: false } expander)
            { expander.IsExpanded = true; playlistView.UpdateLayout(); }
            playlistView.ScrollIntoView(target);
        }));
    }
    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is T nested) return nested;
        }
        return null;
    }
    private void MovePlaylistSelection(int offset)
    {
        if (playlist.SelectedId is Guid id) playlist.Move(id, offset);
        RefreshPlaylistView();
    }
    private int FallbackBpm() => int.TryParse(bpm.Text, out int value) && value is >= 20 and <= 300 ? value : settings.Bpm;
    private Task ReloadLibraryAsync(bool startup, bool automatic = false) => DocumentOperation(async () =>
    {
        if (scoreLibrary == null)
        {
            if (!startup && !automatic) ReportIssue($"未找到“{ScoreLibrary.FolderName}”文件夹：请放在程序同目录（或源码根目录），每个子文件夹为一位歌手。");
            return false;
        }
        var found = ScoreLibrary.Scan(scoreLibrary);
        string? currentPath = playlist.Find(playlist.CurrentId)?.Path, selectedPath = playlist.Find(playlist.SelectedId)?.Path;
        int fallback = FallbackBpm();
        playlist.Replace(Array.Empty<PlaylistEntry>());
        var errors = new List<string>();
        foreach (var (path, artist) in found)
        {
            if (playlist.Items.Count >= Playlist.MaxEntries) { errors.Add($"已达到 {Playlist.MaxEntries} 项上限，其余文件未添加。"); break; }
            var entry = playlist.Add(path, fallback, artist);
            try { await playlist.ReadAsync(entry.Id, CancellationToken.None); }
            catch (Exception e) when (Playlist.IsFileProblem(e)) { errors.Add($"{path}：{e.Message}"); }
        }
        // Rescanning keeps the current and preselected songs when they are still listed.
        Guid? ByPath(string? path) => path == null ? null :
            playlist.Items.FirstOrDefault(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase))?.Id;
        playlist.SetCurrent(ByPath(currentPath)); playlist.Select(ByPath(selectedPath));
        libraryListActive = true; WatchLibrary();
        playlistIssues.Clear(); playlistNotice = ""; ShowPlaylistSkips(errors); RefreshPlaylistView();
        int artists = playlist.Items.Select(e => e.Artist).Distinct().Count();
        status.Text = (automatic ? "简谱文件夹有变化，已自动刷新：" : $"已从“{ScoreLibrary.FolderName}”载入 ") +
            $"{playlist.Items.Count} 首（{artists} 位歌手）。单击预选，双击加载。";
        return true;
    });
    private Task AddPlaylistFilesAsync() => DocumentOperation(async () =>
    {
        string[] paths = scoreDialogs.OpenPlaylistTracks(this);
        int fallback = FallbackBpm();
        var errors = new List<string>();
        foreach (string path in paths)
        {
            if (playlist.Items.Count >= Playlist.MaxEntries) { errors.Add($"已达到 {Playlist.MaxEntries} 项上限，其余文件未添加。"); break; }
            string artist = ScoreLibrary.ArtistFor(scoreLibrary, path);
            // An automatic rescan would drop songs outside the library folder.
            if (artist == ScoreLibrary.Outside) libraryListActive = false;
            var entry = playlist.Add(path, fallback, artist);
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
        var entries = await PlaylistStore.ReadAsync(path, scoreLibrary);
        playlist.Replace(entries); // Editor content and its dirty flag remain intact.
        libraryListActive = false; // Until "刷新简谱", a saved list is not auto-replaced.
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

internal sealed class SameIdConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values.Length == 2 && values[0] is Guid a && values[1] is Guid b && a == b;
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
internal sealed class IssueMarkConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is string { Length: > 0 } ? "⚠" : "";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
internal sealed class EntryTipConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is PlaylistEntry e ? e.Path + (e.Issue is { Length: > 0 } issue ? "\n错误：" + issue : "") : "";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
