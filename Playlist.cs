using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace HarmonicaPlayer;

public sealed record PlaylistEntry(Guid Id, string Path, string Title, int Bpm, int Gap, string? Issue = null, string Artist = "");
public sealed record PlaylistAdvance(PlaylistEntry? Entry, ScoreReadResult? Score,
    IReadOnlyList<string> Skipped, bool BlockedByEdits = false);

// No window, input, or audio dependencies. Identity is independent of selection
// and ordering; a preselection never changes the editor/current song.
public sealed class Playlist
{
    public const int MaxEntries = 2000;
    public ObservableCollection<PlaylistEntry> Items { get; } = new();
    public Guid? CurrentId { get; private set; }
    public Guid? SelectedId { get; private set; }
    private readonly Func<string, int, CancellationToken, Task<ScoreReadResult>> read;
    public Playlist(Func<string, int, CancellationToken, Task<ScoreReadResult>>? reader = null) => read = reader ?? ScoreDocumentReader.ReadAsync;
    public PlaylistEntry? Find(Guid? id) => Items.FirstOrDefault(e => e.Id == id);
    public void Select(Guid? id) => SelectedId = Find(id)?.Id;
    public void SetCurrent(Guid? id) => CurrentId = Find(id)?.Id;
    public void ConsumeSelection(Guid id) { if (SelectedId == id) SelectedId = null; }
    public PlaylistEntry? Neighbor(int direction)
    {
        if (direction is not (-1 or 1)) throw new ArgumentOutOfRangeException(nameof(direction));
        int index = CurrentId is Guid id ? Index(id) : -1;
        int next = index + direction;
        return index < 0 || next < 0 || next >= Items.Count ? null : Items[next];
    }
    private int Index(Guid id) => Items.ToList().FindIndex(e => e.Id == id);
    public void Replace(IEnumerable<PlaylistEntry> entries)
    {
        // Keep each artist contiguous (first appearance order) so the grouped
        // view and previous/next navigation follow the same order.
        var next = entries.GroupBy(e => e.Artist).SelectMany(g => g).ToArray();
        if (next.Length > MaxEntries || next.Select(e => e.Id).Distinct().Count() != next.Length)
            throw new FormatException($"播放列表最多 {MaxEntries} 项，曲目标识不可重复。");
        Items.Clear(); foreach (var entry in next) Items.Add(entry);
        CurrentId = SelectedId = null;
    }
    public PlaylistEntry Add(string path, int fallbackBpm, string artist = "")
    {
        if (Items.Count >= MaxEntries) throw new FormatException($"播放列表最多 {MaxEntries} 项。");
        path = System.IO.Path.GetFullPath(path);
        string title = System.IO.Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrWhiteSpace(title)) title = "未命名曲谱";
        var item = new PlaylistEntry(Guid.NewGuid(), path, title[..Math.Min(title.Length, 200)], fallbackBpm, 20, Artist: artist);
        // A new song joins the end of its artist group instead of the list end.
        int last = Items.ToList().FindLastIndex(e => e.Artist == artist);
        if (last < 0) Items.Add(item); else Items.Insert(last + 1, item);
        return item;
    }
    public void Remove(Guid id)
    {
        int index = Index(id); if (index < 0) return;
        Items.RemoveAt(index);
        if (CurrentId == id) CurrentId = null;
        if (SelectedId == id) SelectedId = null;
    }
    public void Move(Guid id, int offset)
    {
        int index = Index(id), target = index + offset;
        // Reordering stays inside the artist group; groups are never split.
        if (index >= 0 && target >= 0 && target < Items.Count && Items[target].Artist == Items[index].Artist)
            Items.Move(index, target);
    }
    public void UpdateDocument(Guid id, ScoreDocument document)
    {
        int index = Index(id); if (index < 0) return;
        Items[index] = Items[index] with { Path = document.SourcePath ?? Items[index].Path,
            Title = document.Title, Bpm = document.Bpm, Gap = document.Gap, Issue = null };
    }
    public async Task<ScoreReadResult> ReadAsync(Guid id, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var entry = Find(id) ?? throw new IOException("曲目已从播放列表移除。");
        try
        {
            // Opening a network path can block before the first async read. Keep
            // it off the dispatcher; cancelling the waiter never commits its result.
            var result = await Task.Run(() => read(entry.Path, entry.Bpm, token), token).WaitAsync(token);
            token.ThrowIfCancellationRequested();
            // Metadata is useful even when the score body is invalid.
            UpdateDocument(id, result.Document);
            result.Document.Validate(result.Document.Gap.ToString());
            return result;
        }
        catch (Exception e) when (IsFileProblem(e))
        {
            token.ThrowIfCancellationRequested();
            int index = Index(id);
            if (index >= 0) Items[index] = Items[index] with { Issue = e.Message };
            throw;
        }
    }
    public async Task<PlaylistAdvance> AdvanceAsync(bool editorDirty, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var next = Neighbor(1);
        if (next == null) return new(null, null, Array.Empty<string>());
        if (editorDirty) return new(null, null, Array.Empty<string>(), true);
        var candidates = Items.Skip(Index(next.Id)).Select(e => e.Id).ToArray();
        var skipped = new List<string>();
        foreach (var id in candidates)
        {
            token.ThrowIfCancellationRequested();
            var entry = Find(id); if (entry == null) continue;
            try
            {
                var score = await ReadAsync(id, token);
                token.ThrowIfCancellationRequested();
                return new(Find(id), score, skipped);
            }
            catch (Exception e) when (IsFileProblem(e))
            { skipped.Add($"{entry.Title}（{entry.Path}）：{e.Message}"); }
        }
        return new(null, null, skipped);
    }
    public static bool IsFileProblem(Exception e) => e is IOException or UnauthorizedAccessException or FormatException or DecoderFallbackException;
}

public static class PlaylistStore
{
    public const string Format = "HarmonicaPlayer.Playlist/1";
    public const int MaxBytes = 1024 * 1024;
    // Artist is optional so lists saved by v0.5.0 still open; older readers ignore it.
    private sealed record SavedEntry(string Path, string Title, int Bpm, int Gap, string? Artist = null);
    private sealed record SavedList(string Format, SavedEntry[] Items);
    public static async Task SaveAsync(string path, IEnumerable<PlaylistEntry> entries)
    {
        string target = Path.GetFullPath(path), directory = Path.GetDirectoryName(target)!;
        var items = entries.Select(e => new SavedEntry(Path.GetRelativePath(directory, e.Path), e.Title, e.Bpm, e.Gap, e.Artist)).ToArray();
        if (items.Length > Playlist.MaxEntries) throw new FormatException("播放列表曲目过多。");
        string json = JsonSerializer.Serialize(new SavedList(Format, items), new JsonSerializerOptions { WriteIndented = true });
        if (Encoding.UTF8.GetByteCount(json) > MaxBytes) throw new FormatException("播放列表文件不得超过 1MB。");
        string temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, json, new UTF8Encoding(false));
            File.Move(temp, target, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static async Task<IReadOnlyList<PlaylistEntry>> ReadAsync(string path, string? libraryRoot = null)
    {
        string target = Path.GetFullPath(path), directory = Path.GetDirectoryName(target)!;
        using var stream = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        using var data = new MemoryStream();
        byte[] buffer = new byte[8192]; int count;
        while ((count = await stream.ReadAsync(buffer)) > 0)
        {
            if (data.Length + count > MaxBytes) throw new FormatException("播放列表文件不得超过 1MB。");
            data.Write(buffer, 0, count);
        }
        SavedList list;
        try { list = JsonSerializer.Deserialize<SavedList>(data.ToArray()) ?? throw new FormatException("播放列表为空。"); }
        catch (JsonException e) { throw new FormatException("播放列表 JSON 无效：" + e.Message); }
        if (list.Format != Format || list.Items == null || list.Items.Length > Playlist.MaxEntries)
            throw new FormatException("不支持的播放列表格式或曲目数量超限。");
        var result = new List<PlaylistEntry>();
        foreach (var item in list.Items)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.Path) || item.Bpm is < 20 or > 300 || item.Gap is < 10 or > 5000)
                throw new FormatException("播放列表含无效路径、BPM 或间隔。");
            ScoreDocument.ValidateTitle(item.Title);
            if (item.Artist is { Length: > 200 }) throw new FormatException("播放列表含过长的歌手名。");
            string full = Path.GetFullPath(item.Path, directory);
            string artist = string.IsNullOrWhiteSpace(item.Artist) ? ScoreLibrary.ArtistFor(libraryRoot, full) : item.Artist.Trim();
            result.Add(new(Guid.NewGuid(), full, item.Title, item.Bpm, item.Gap, Artist: artist));
        }
        return result;
    }
}

// The default library is a "简谱" folder; each first-level subfolder is one
// artist. Only file locations are used; score files are never modified.
public static class ScoreLibrary
{
    public const string FolderName = "简谱", Unsorted = "未分类", Outside = "其他";
    private static readonly StringComparer NameOrder = StringComparer.Create(CultureInfo.GetCultureInfo("zh-CN"), true);

    // Release packages keep the folder beside the EXE; a source build runs
    // from bin\<config>\<tfm>, so parent folders are searched as well.
    public static string? FindFolder(string startDirectory, int maxLevels = 6)
    {
        var directory = new DirectoryInfo(startDirectory);
        for (int level = 0; directory != null && level <= maxLevels; level++, directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, FolderName);
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }
    public static string ArtistFor(string? root, string path)
    {
        if (root == null) return Outside;
        string relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar) || Path.IsPathRooted(relative)) return Outside;
        var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Length >= 2 ? parts[0] : Unsorted;
    }
    // Artists in name order with unsorted files last; songs by file name.
    public static IReadOnlyList<(string Path, string Artist)> Scan(string root) =>
        Directory.EnumerateFiles(root, "*.txt", SearchOption.AllDirectories)
            .Where(p => string.Equals(Path.GetExtension(p), ".txt", StringComparison.OrdinalIgnoreCase))
            .Select(p => (Path: Path.GetFullPath(p), Artist: ArtistFor(root, p)))
            .OrderBy(e => e.Artist == Unsorted ? 1 : 0).ThenBy(e => e.Artist, NameOrder)
            .ThenBy(e => Path.GetFileName(e.Path), NameOrder).ToArray();
}
