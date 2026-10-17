using HarmonicaPlayer;

static class PlaylistTests
{
    public static async Task Run()
    {
        int passed = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception("Playlist: " + name); passed++; }
        async Task Reject(Func<Task> action, string name)
        {
            bool rejected = false;
            try { await action(); } catch (Exception e) when (e is FormatException or IOException or UnauthorizedAccessException) { rejected = true; }
            Check(rejected, name);
        }
        string folder = Path.Combine(Path.GetTempPath(), "HarmonicaPlaylist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string old = Path.Combine(folder, "旧谱 bpm90.txt"), missing = Path.Combine(folder, "missing.txt"),
                bad = Path.Combine(folder, "bad.txt"), next = Path.Combine(folder, "next.txt"), fallback = Path.Combine(folder, "fallback.txt");
            await File.WriteAllTextAsync(old, "565 【123】 【3_.】 【【#1】】:1.25");
            await File.WriteAllTextAsync(bad, "8");
            await File.WriteAllTextAsync(fallback, "1 2 3");
            await ScoreDocumentWriter.SaveAsync(next, new ScoreDocument("下一首", 163, "1 2 3", Gap: 10), "10");
            var list = new Playlist();
            var first = list.Add(old, 120); var lost = list.Add(missing, 120); var invalid = list.Add(bad, 120); var last = list.Add(next, 120);
            var firstRead = await list.ReadAsync(first.Id, default);
            Check(firstRead.Document.IsLegacy && firstRead.Document.Bpm == 90 && firstRead.Document.Gap == 20, "legacy filename BPM and default gap");
            Check(list.Find(first.Id)!.Bpm == 90 && list.Find(first.Id)!.Title.Contains("旧谱"), "metadata from reader");
            Check(list.CurrentId == null, "metadata import does not load current song");
            list.SetCurrent(first.Id); list.Select(last.Id);
            Check(list.CurrentId == first.Id && list.SelectedId == last.Id, "single selection independent of current");
            Check(list.Neighbor(1)?.Id == lost.Id && list.Neighbor(-1) == null, "navigation based on current not preselection; first boundary");
            var blocked = await list.AdvanceAsync(true, default);
            Check(blocked.BlockedByEdits && blocked.Score == null && blocked.Skipped.Count == 0, "unsaved content stops before touching next file");
            Check(list.CurrentId == first.Id && list.SelectedId == last.Id, "blocked advance preserves identities");
            var advance = await list.AdvanceAsync(false, default);
            Check(advance.Entry?.Id == last.Id && advance.Skipped.Count == 2, "missing and malformed songs skipped once");
            Check(advance.Skipped.Any(s => s.Contains(missing)) && advance.Skipped.Any(s => s.Contains(bad)), "skip report locates both files");
            Check(advance.Score?.Document.Bpm == 163 && advance.Score.Document.Gap == 10, "next song gets its own BPM and gap");
            Check(list.Find(lost.Id)!.Issue != null && list.Find(invalid.Id)!.Issue != null, "bad rows keep reasons");
            Check(list.CurrentId == first.Id, "resolution alone cannot commit editor identity");
            list.SetCurrent(last.Id);
            Check(list.Neighbor(1) == null && list.Neighbor(-1)?.Id == invalid.Id, "last boundary never loops");
            Check((await list.AdvanceAsync(false, default)).Entry == null, "end of list terminates");
            list.ConsumeSelection(first.Id);
            Check(list.SelectedId == last.Id, "unrelated selection is not cleared");
            list.ConsumeSelection(last.Id); Check(list.SelectedId == null, "adopted preselection consumed");
            list.Select(last.Id); list.Move(last.Id, -1);
            Check(list.CurrentId == last.Id && list.SelectedId == last.Id && list.Neighbor(1)?.Id == invalid.Id,
                "reordering keeps identities and changes next neighbor");
            list.Remove(last.Id);
            Check(list.CurrentId == null && list.SelectedId == null && list.Neighbor(1) == null, "removing current detaches list identity");
            list.SetCurrent(first.Id);
            Check((await list.AdvanceAsync(false, default)).Skipped.Count == 2, "all remaining invalid songs terminate after one pass");
            await ScoreDocumentWriter.SaveAsync(bad, new ScoreDocument("修好的谱", 100, "5 6", Gap: 15), "15");
            var repaired = await list.AdvanceAsync(false, default);
            Check(repaired.Entry?.Id == invalid.Id && list.Find(invalid.Id)!.Issue == null, "re-read repairs a formerly invalid row");
            await File.WriteAllTextAsync(bad, "@format=HarmonicaPlayer/1\n@bpm=80\n@gap=20\n\n2 3");
            Check((await list.ReadAsync(invalid.Id, default)).Document.Bpm == 80, "external edits are re-read before playing");
            var legacy = list.Add(fallback, 111);
            Check((await list.ReadAsync(legacy.Id, default)).Document.Bpm == 111, "legacy missing BPM uses fixed import fallback");

            string saved = Path.Combine(folder, "songs.hpl");
            await PlaylistStore.SaveAsync(saved, list.Items);
            string json = await File.ReadAllTextAsync(saved);
            Check(!json.Contains(folder.Replace("\\", "\\\\")), "same-folder paths saved relatively");
            var loaded = await PlaylistStore.ReadAsync(saved);
            Check(loaded.Select(e => e.Path).SequenceEqual(list.Items.Select(e => e.Path)), "list preserves order and resolves relative paths");
            Check(loaded.Last().Bpm == 111 && loaded[0].Gap == 20, "fallback BPM and gap metadata survive save");
            list.Replace(loaded);
            Check(list.CurrentId == null && list.SelectedId == null, "opening list does not select or load a song");
            Check(loaded.Select(e => e.Id).Distinct().Count() == loaded.Count, "restored entries have unique identity");
            Check(Directory.GetFiles(folder, "*.tmp").Length == 0, "atomic save leaves no temporary files");
            await File.WriteAllTextAsync(saved, "{bad json");
            await Reject(async () => { await PlaylistStore.ReadAsync(saved); }, "invalid JSON rejected");
            await File.WriteAllTextAsync(saved, "{\"Format\":\"unknown\",\"Items\":[]}");
            await Reject(async () => { await PlaylistStore.ReadAsync(saved); }, "unknown format rejected");
            await File.WriteAllTextAsync(saved, "{\"Format\":\"HarmonicaPlayer.Playlist/1\",\"Items\":[null]}");
            await Reject(async () => { await PlaylistStore.ReadAsync(saved); }, "null item rejected");
            await File.WriteAllTextAsync(saved, new string('x', PlaylistStore.MaxBytes + 1));
            await Reject(async () => { await PlaylistStore.ReadAsync(saved); }, "oversized list rejected");
            await PlaylistStore.SaveAsync(saved, Array.Empty<PlaylistEntry>());
            Check((await PlaylistStore.ReadAsync(saved)).Count == 0, "empty list round trip");
            await Reject(() => PlaylistStore.SaveAsync(folder, list.Items), "write failure returned without claiming success");
            Check(Directory.GetFiles(folder, "*.tmp").Length == 0, "failed write cleans temp");

            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<ScoreReadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            int reads = 0;
            var gated = new Playlist(async (path, bpm, token) => { reads++; entered.TrySetResult(); return await release.Task; });
            var ga = gated.Add(old, 120); var gb = gated.Add(next, 120); gated.SetCurrent(ga.Id);
            using var cancel = new CancellationTokenSource();
            Task<PlaylistAdvance> pending = gated.AdvanceAsync(false, cancel.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            cancel.Cancel();
            bool cancelled = false;
            try { await pending.WaitAsync(TimeSpan.FromSeconds(3)); } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && !release.Task.IsCompleted, "stop does not wait for a blocked file source");
            release.SetResult(new ScoreReadResult(new ScoreDocument("迟到结果", 90, "1 2", next), Array.Empty<string>()));
            Check(cancelled && gated.CurrentId == ga.Id && gated.Find(gb.Id)!.Title != "迟到结果", "stop during file read cannot apply stale result");
            using var alreadyCancelled = new CancellationTokenSource(); alreadyCancelled.Cancel();
            bool readCancelled = false;
            try { await ScoreDocumentReader.ReadAsync(missing, 120, alreadyCancelled.Token); }
            catch (OperationCanceledException) { readCancelled = true; }
            Check(readCancelled, "cancelled document read returns before opening missing file");
            try { await gated.AdvanceAsync(false, alreadyCancelled.Token); } catch (OperationCanceledException) { }
            Check(reads == 1, "stopped sequence does not read another file");
            var restart = await gated.AdvanceAsync(false, default);
            Check(restart.Entry?.Id == gb.Id && reads == 2, "restart works after cancelled load");
        }
        finally { Directory.Delete(folder, true); }

        var backend = new FakeHotkeys(); var manager = new HotkeyController(backend);
        var previous = new HotkeyBinding(0x74); var nextBinding = new HotkeyBinding(0x76);
        manager.Apply(HotkeyBinding.DefaultStart, HotkeyBinding.DefaultStop, previous, nextBinding);
        Check(backend.Registered.Count == 4 && manager.PreviousReady && manager.NextReady && manager.StopReady, "four global actions register independently");
        backend.Occupied.Add(nextBinding);
        manager.Apply(HotkeyBinding.DefaultStart, HotkeyBinding.DefaultStop, previous, nextBinding);
        Check(manager.StopReady && manager.StartReady && manager.PreviousReady && !manager.NextReady, "navigation conflict does not disable emergency stop");
        bool conflict = false;
        try { manager.Apply(HotkeyBinding.DefaultStart, HotkeyBinding.DefaultStop, previous, HotkeyBinding.DefaultStop); }
        catch (FormatException) { conflict = true; }
        Check(conflict && manager.StopReady && manager.PreviousReady, "cross-action duplicate rejected before unregistering");
        backend.Occupied.Clear();
        manager.Apply(HotkeyBinding.DefaultStart, HotkeyBinding.DefaultStop);
        Check(backend.Registered.Count == 2 && !manager.PreviousReady && !manager.NextReady, "clearing optional bindings unregisters both");
        manager.Suspend(); Check(backend.Registered.Count == 0, "suspend releases all bindings");
        var oldSettings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{\"Bpm\":90}")!;
        oldSettings.Validate(); Check(oldSettings.Previous == null && oldSettings.Next == null, "old settings keep optional shortcuts empty");
        var custom = oldSettings with { Previous = previous, Next = nextBinding };
        var restored = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(System.Text.Json.JsonSerializer.Serialize(custom))!;
        restored.Validate(); Check(restored == custom, "four hotkeys round trip in settings");
        Console.WriteLine($"PASS playlist: {passed} checks");
    }
}
