using System.Globalization;
using System.IO;
using System.Text;

namespace HarmonicaPlayer;

public sealed record MidiSourceNote(int Pitch, long StartTick, long EndTick);
public sealed record MidiTempo(long Tick, int Microseconds);
public sealed record MidiImportPart(int Track, int Channel, string Name, IReadOnlyList<MidiSourceNote> Notes,
    long EndTick, IReadOnlyList<string> Unsupported)
{
    public string Label => $"轨道 {Track + 1} / 通道 {Channel + 1} · {Name} · {Notes.Count} 音" +
        (Channel == 9 ? " · 打击乐" : "");
}
public sealed class MidiImportFile
{
    public string Title { get; }
    public int TicksPerQuarter { get; }
    public IReadOnlyList<MidiImportPart> Parts { get; }
    public IReadOnlyList<MidiTempo> Tempos { get; }
    public IReadOnlyList<string> Unsupported { get; }
    private readonly double[] tempoTimes;
    internal MidiImportFile(string title, int division, List<MidiImportPart> parts, List<MidiTempo> tempos, IReadOnlyList<string> unsupported)
    {
        Title = title; TicksPerQuarter = division; Parts = parts.AsReadOnly(); Tempos = tempos.AsReadOnly(); Unsupported = unsupported;
        tempoTimes = new double[tempos.Count];
        for (int i = 1; i < tempos.Count; i++)
            tempoTimes[i] = tempoTimes[i - 1] +
                (tempos[i].Tick - tempos[i - 1].Tick) * (double)tempos[i - 1].Microseconds / division / 1000;
    }
    public double Milliseconds(long tick)
    {
        int lo = 0, hi = Tempos.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (Tempos[mid].Tick <= tick) lo = mid; else hi = mid - 1;
        }
        return tempoTimes[lo] + (tick - Tempos[lo].Tick) * (double)Tempos[lo].Microseconds / TicksPerQuarter / 1000;
    }
    public int SuggestedBpm => Math.Clamp((int)Math.Round(60000000.0 / Tempos[0].Microseconds), 20, 300);
}

public sealed record MidiImportProblem(int NoteNumber, double TimeMs, string Message)
{
    public override string ToString() => $"第 {NoteNumber} 音 · {TimeMs / 1000:0.###} 秒：{Message}";
}
public enum MidiMelodyStrategy { Highest, Lowest }
public sealed record MidiConversionReport(int SourceNotes, int KeptNotes, int ChordNotesDropped,
    int ShortNotesDropped, int ShortenedNotes, int OctaveAdjustedNotes, int Transpose,
    IReadOnlyList<string> IgnoredEffects, IReadOnlyList<MidiImportProblem> Adjustments)
{
    public string Summary => $"原始 {SourceNotes} 音 → 保留 {KeptNotes} 音；舍弃 {ChordNotesDropped + ShortNotesDropped} 音" +
        $"（和弦取舍 {ChordNotesDropped}、过短 {ShortNotesDropped}）；截短 {ShortenedNotes} 音；" +
        $"八度调整 {OctaveAdjustedNotes} 音；整体移调 {Transpose:+0;-0;0} 半音。";
}
public sealed record MidiConversion(ScoreDocument? Document, IReadOnlyList<MidiImportProblem> Problems,
    MidiConversionReport? Report = null)
{
    public bool Success => Document != null && Problems.Count == 0;
}
public sealed record MidiTransposeSuggestion(int Semitones, int OriginalPlayable, int SuggestedPlayable, int Total);

public static class MidiImporter
{
    public const int MaxBytes = 16 * 1024 * 1024;
    private const int MaxEvents = 500000;
    public static async Task<MidiImportFile> ReadAsync(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, true);
        using var bytes = new MemoryStream();
        byte[] buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer)) > 0)
        {
            if (bytes.Length + count > MaxBytes) throw new FormatException("MIDI 文件不得超过 16MB。");
            bytes.Write(buffer, 0, count);
        }
        return Parse(bytes.ToArray(), Path.GetFileNameWithoutExtension(path));
    }

    public static MidiImportFile Parse(byte[] bytes, string title = "导入的 MIDI")
    {
        if (bytes.Length > MaxBytes) throw new FormatException("MIDI 文件不得超过 16MB。");
        using var reader = new BinaryReader(new MemoryStream(bytes));
        int events = 0;
        try
        {
            int U16() => (reader.ReadByte() << 8) | reader.ReadByte();
            uint U32() => ((uint)reader.ReadByte() << 24) | ((uint)reader.ReadByte() << 16) |
                ((uint)reader.ReadByte() << 8) | reader.ReadByte();
            string Chunk() => Encoding.ASCII.GetString(reader.ReadBytes(4));
            if (Chunk() != "MThd") throw new FormatException("不是标准 MIDI 文件（缺少 MThd）。");
            uint headerLength = U32();
            if (headerLength < 6 || headerLength > bytes.Length - reader.BaseStream.Position)
                throw new FormatException("MIDI 文件头长度无效。");
            int format = U16(), trackCount = U16(), division = U16();
            if (format is not (0 or 1)) throw new FormatException("当前支持标准 MIDI Format 0 / 1，不支持 Format 2 或 MIDI 2.0 文件。");
            if (trackCount is < 1 or > 256 || (format == 0 && trackCount != 1))
                throw new FormatException("MIDI 轨道数量无效或超过 256。");
            if ((division & 0x8000) != 0 || division == 0)
                throw new FormatException("当前仅支持按拍计时的 MIDI（PPQ），暂不支持 SMPTE 时间码。");
            reader.BaseStream.Position += headerLength - 6;
            var parts = new List<MidiImportPart>();
            var tempos = new List<MidiTempo>();
            var globalUnsupported = new HashSet<string>();
            var channelUnsupported = new Dictionary<int, HashSet<string>>();
            for (int trackIndex = 0; trackIndex < trackCount; trackIndex++)
            {
                if (Chunk() != "MTrk") throw new FormatException($"第 {trackIndex + 1} 轨缺少 MTrk。");
                uint length = U32();
                long end = reader.BaseStream.Position + length;
                if (end > bytes.Length) throw new FormatException($"第 {trackIndex + 1} 轨数据不完整。");
                byte Byte()
                {
                    if (reader.BaseStream.Position >= end) throw new FormatException($"第 {trackIndex + 1} 轨事件被截断。");
                    return reader.ReadByte();
                }
                int Variable()
                {
                    int value = 0;
                    for (int i = 0; i < 4; i++)
                    {
                        int b = Byte(); value = (value << 7) | (b & 127);
                        if (b < 128) return value;
                    }
                    throw new FormatException("MIDI 事件长度或间隔编码无效。");
                }
                byte[] Data(int count)
                {
                    if (count > end - reader.BaseStream.Position) throw new FormatException("MIDI 事件数据长度越界。");
                    return reader.ReadBytes(count);
                }
                // Repeated Note On messages pair with Note Off in arrival order. Keep
                // separate attacks; chord/overlap reduction belongs to conversion.
                var active = new Dictionary<(int Channel, int Pitch), Queue<long>>();
                var notes = new Dictionary<int, List<MidiSourceNote>>();
                void Unsupported(int channel, string issue)
                {
                    if (!channelUnsupported.TryGetValue(channel, out var values)) channelUnsupported[channel] = values = new();
                    values.Add(issue);
                }
                long tick = 0; int running = 0; bool ended = false;
                string name = $"未命名轨道 {trackIndex + 1}";
                while (reader.BaseStream.Position < end)
                {
                    if (++events > MaxEvents) throw new FormatException("MIDI 事件超过 500000，文件过于复杂。");
                    tick = checked(tick + Variable());
                    int status = Byte(), first = -1;
                    if (status < 128)
                    {
                        if (running == 0) throw new FormatException("MIDI running status 缺少前置状态。");
                        first = status; status = running;
                    }
                    if (status == 0xFF)
                    {
                        running = 0;
                        int type = Byte(), size = Variable(); byte[] data = Data(size);
                        if (type == 0x2F)
                        {
                            if (size != 0) throw new FormatException("MIDI 结束事件长度无效。");
                            ended = true;
                            if (reader.BaseStream.Position != end) throw new FormatException("MIDI 轨道结束事件之后仍有数据。");
                            break;
                        }
                        if (type == 0x51)
                        {
                            if (size != 3) throw new FormatException("MIDI 速度事件长度无效。");
                            int tempo = (data[0] << 16) | (data[1] << 8) | data[2];
                            if (tempo == 0) throw new FormatException("MIDI 速度不能为零。");
                            tempos.Add(new(tick, tempo));
                        }
                        if (type == 3)
                        {
                            name = new string(Encoding.UTF8.GetString(data).Where(c => !char.IsControl(c)).Take(100).ToArray());
                            if (name.Length == 0) name = $"未命名轨道 {trackIndex + 1}";
                        }
                        continue;
                    }
                    if (status is 0xF0 or 0xF7)
                    {
                        running = 0; Data(Variable());
                        globalUnsupported.Add("已忽略 SysEx 的调音/演奏效果。");
                        continue;
                    }
                    if (status is < 0x80 or >= 0xF0) throw new FormatException("MIDI 包含不支持的事件状态。");
                    running = status;
                    int kind = status & 0xF0, channel = status & 15;
                    int a = first >= 0 ? first : Byte();
                    int b = kind is 0xC0 or 0xD0 ? 0 : Byte();
                    if (a > 127 || b > 127) throw new FormatException("MIDI 通道事件数据无效。");
                    if (kind == 0x90 && b > 0)
                    {
                        if (!active.TryGetValue((channel, a), out var starts)) active[(channel, a)] = starts = new();
                        starts.Enqueue(tick);
                    }
                    else if (kind == 0x80 || (kind == 0x90 && b == 0))
                    {
                        if (!active.TryGetValue((channel, a), out var starts))
                            throw new FormatException($"第 {trackIndex + 1} 轨、通道 {channel + 1} 存在未配对的 Note Off。");
                        long start = starts.Dequeue();
                        if (starts.Count == 0) active.Remove((channel, a));
                        if (!notes.TryGetValue(channel, out var list)) notes[channel] = list = new();
                        list.Add(new(a, start, tick));
                    }
                    else if (kind == 0xE0 && ((b << 7) | a) != 8192)
                        Unsupported(channel, "已忽略弯音，使用 Note On 的固定音高。");
                    else if (kind == 0xB0 && ((a == 64 && b >= 64) || a is 66 or 69 or 120 or 123))
                        Unsupported(channel, "已忽略踏板/通道关闭控制效果，按 Note On / Note Off 计算时长。");
                    else if (kind is 0xB0 or 0xC0 or 0xD0 or 0xA0)
                        Unsupported(channel, "已忽略音色、表情或其他控制效果。");
                }
                if (!ended) throw new FormatException($"第 {trackIndex + 1} 轨缺少结束事件。");
                if (active.Count != 0) throw new FormatException($"第 {trackIndex + 1} 轨有音符缺少 Note Off，未自动补全。");
                foreach (var pair in notes.OrderBy(p => p.Key))
                    parts.Add(new(trackIndex, pair.Key, name,
                        pair.Value.OrderBy(n => n.StartTick).ThenBy(n => n.EndTick).ToList().AsReadOnly(), tick,
                        Array.Empty<string>()));
            }
            if (reader.BaseStream.Position != bytes.Length) throw new FormatException("MIDI 文件有多余或不完整的轨道数据。");
            if (parts.Count == 0) throw new FormatException("MIDI 没有可导入的音符。");
            // Format 1 tracks share MIDI channels; controls in a conductor track affect melody too.
            parts = parts.Select(p => p with { Unsupported = channelUnsupported.TryGetValue(p.Channel, out var issues)
                ? issues.Order().ToArray() : Array.Empty<string>() }).ToList();
            var ordered = new List<MidiTempo> { new(0, 500000) };
            foreach (var group in tempos.GroupBy(t => t.Tick).OrderBy(g => g.Key))
            {
                int[] values = group.Select(t => t.Microseconds).Distinct().ToArray();
                if (values.Length > 1) throw new FormatException($"MIDI 在 tick {group.Key} 有冲突的速度事件。");
                var item = new MidiTempo(group.Key, values[0]);
                if (item.Tick == 0) ordered[0] = item;
                else if (ordered[^1].Microseconds != item.Microseconds) ordered.Add(item);
            }
            title = new string(title.Where(c => !char.IsControl(c)).Take(200).ToArray());
            return new(title.Length == 0 ? "导入的 MIDI" : title, division, parts, ordered, globalUnsupported.ToArray());
        }
        catch (EndOfStreamException) { throw new FormatException("MIDI 文件被截断，未导入。"); }
        catch (OverflowException) { throw new FormatException("MIDI 时间或长度超出支持范围。"); }
    }

    public static MidiTransposeSuggestion SuggestTranspose(MidiImportPart part)
    {
        var counts = new int[128];
        foreach (var note in part.Notes) counts[note.Pitch]++;
        var prefix = new int[129];
        for (int i = 0; i < counts.Length; i++) prefix[i + 1] = prefix[i] + counts[i];
        int Coverage(int shift)
        {
            int low = Math.Max(0, HarmonicaPitchMap.Lowest - shift);
            int high = Math.Min(127, HarmonicaPitchMap.Highest - shift);
            return low > high ? 0 : prefix[high + 1] - prefix[low];
        }
        int best = Enumerable.Range(-127, 255).OrderByDescending(Coverage)
            .ThenBy(n => Math.Abs(n)).ThenBy(n => n).First();
        int original = Coverage(0);
        return new(best, original, Coverage(best), part.Notes.Count);
    }

    public static MidiConversion Convert(MidiImportFile file, MidiImportPart part, int bpm, int gap, int transpose = 0,
        MidiMelodyStrategy strategy = MidiMelodyStrategy.Highest)
    {
        PlaybackValidation.ParseBpm(bpm.ToString(CultureInfo.InvariantCulture));
        PlaybackValidation.ParseGap(gap.ToString(CultureInfo.InvariantCulture));
        if (transpose is < -127 or > 127) throw new FormatException("移调范围为 -127～127 半音。");
        if (!Enum.IsDefined(strategy)) throw new FormatException("未知的和弦取音方式。");
        var problems = new List<MidiImportProblem>();
        void Problem(int i, double time, string message) => problems.Add(new(i + 1, time, message));
        if (part.Channel == 9) Problem(0, 0, "打击乐通道不能作为单旋律口琴导入。");
        if (part.Notes.Count > 20000)
        {
            Problem(0, 0, "音符超过 20000，无法生成本版谱面。");
            return new(null, problems.AsReadOnly());
        }
        if (file.Milliseconds(part.EndTick) * bpm / 60000.0 > 20000 * 64)
        {
            Problem(0, 0, "所选轨道过长，无法在 20000 个音/休止和每段 64 拍的限制内转换。");
            return new(null, problems.AsReadOnly());
        }
        if (part.Notes.Count == 0) Problem(0, 0, "所选轨道/通道没有音符。");
        if (problems.Count > 0) return new(null, problems.AsReadOnly());
        var adjustments = new List<MidiImportProblem>();
        void Adjust(int index, double time, string message) => adjustments.Add(new(index + 1, time, message));
        int chords = 0, shortNotes = 0, shortened = 0, folded = 0;
        var candidates = new List<(MidiSourceNote Note, int Index)>();
        foreach (var group in part.Notes.Select((note, index) => (Note: note, Index: index))
            .GroupBy(n => n.Note.StartTick).OrderBy(g => g.Key))
        {
            var chosen = group.OrderBy(n => strategy == MidiMelodyStrategy.Highest ? -n.Note.Pitch : n.Note.Pitch)
                .ThenByDescending(n => n.Note.EndTick).ThenBy(n => n.Index).First();
            candidates.Add(chosen);
            foreach (var other in group.Where(n => n.Index != chosen.Index))
            {
                chords++;
                Adjust(other.Index, file.Milliseconds(other.Note.StartTick),
                    $"和弦取{(strategy == MidiMelodyStrategy.Highest ? "最高" : "最低")}音：舍弃 MIDI {other.Note.Pitch}，保留 MIDI {chosen.Note.Pitch}。");
            }
        }
        var kept = new List<(int Pitch, double Start, double End)>();
        double boundary = file.Milliseconds(part.EndTick);
        // Work backwards: a dropped ornament must not unnecessarily truncate the
        // preceding note. Later retained attacks always keep their source time.
        foreach (var candidate in candidates.AsEnumerable().Reverse())
        {
            var n = candidate.Note; int i = candidate.Index;
            double start = file.Milliseconds(n.StartTick), originalEnd = file.Milliseconds(n.EndTick);
            double end = Math.Min(originalEnd, boundary - gap);
            if (end - start < PlaybackValidation.PreparationMs + 40 - 0.000001)
            {
                shortNotes++;
                Adjust(i, start, $"舍弃过短音：原时长 {originalEnd - start:0.###}ms，留出 gap 后最多 {Math.Max(0, end - start):0.###}ms，不足 52ms；后续起音不变。");
                continue;
            }
            if ((end - start + gap) * bpm / 60000.0 > 64)
                Problem(i, start, "单音时值超过 64 拍，请选用更低的固定 BPM 或在原 MIDI 拆分。");
            if (end < originalEnd - 0.000001)
            {
                shortened++;
                Adjust(i, start, $"音尾截短 {originalEnd - end:0.###}ms，保留起音并留出 gap={gap}ms。");
            }
            int pitch = n.Pitch + transpose, beforeFold = pitch;
            while (pitch < HarmonicaPitchMap.Lowest) pitch += 12;
            while (pitch > HarmonicaPitchMap.Highest) pitch -= 12;
            if (pitch != beforeFold)
            {
                folded++;
                Adjust(i, start, $"八度调整：移调后 MIDI {beforeFold} → {pitch}（{(pitch - beforeFold) / 12:+0;-0;0} 八度）。");
            }
            kept.Add((pitch, start, end)); boundary = start;
        }
        kept.Reverse();
        var report = new MidiConversionReport(part.Notes.Count, kept.Count, chords, shortNotes, shortened, folded,
            transpose, file.Unsupported.Concat(part.Unsupported).Distinct().ToArray(),
            adjustments.OrderBy(a => a.TimeMs).ThenBy(a => a.NoteNumber).ToArray());
        if (kept.Count == 0) Problem(0, 0, "转换后没有可演奏的音符，请选择其他轨道或减小 gap。");
        if (problems.Count > 0) return new(null, problems.AsReadOnly(), report);
        var tokens = new List<string>(); double cursor = 0, beatMs = 60000.0 / bpm;
        static string BeatText(double beats)
        {
            string text = beats.ToString("R", CultureInfo.InvariantCulture);
            // TXT accepts decimals, not exponents. Preserve round-trip precision.
            return text.Contains('E') || text.Contains('e')
                ? decimal.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture).ToString("0.############################", CultureInfo.InvariantCulture)
                : text;
        }
        void Rest(double milliseconds)
        {
            double beats = milliseconds / beatMs;
            while (beats > 64) { tokens.Add("0:64"); beats -= 64; }
            if (beats > 0) tokens.Add("0:" + BeatText(beats));
        }
        foreach (var n in kept)
        {
            Rest(Math.Max(0, n.Start - cursor));
            double beats = (n.End - n.Start + gap) / beatMs;
            // Avoid a floating point roundoff making exactly 52ms fail validation.
            if (beats * beatMs - gap - PlaybackValidation.PreparationMs < 40)
                beats = (PlaybackValidation.PreparationMs + 40 + gap) / beatMs;
            while (beats * beatMs - gap - PlaybackValidation.PreparationMs < 40) beats = Math.BitIncrement(beats);
            tokens.Add(HarmonicaPitchMap.Token(n.Pitch) + ":" + BeatText(beats));
            cursor = n.End + gap;
        }
        Rest(Math.Max(0, file.Milliseconds(part.EndTick) - cursor));
        string body = string.Join("\n", tokens.Chunk(8).Select(row => string.Join(" ", row)));
        var document = new ScoreDocument(file.Title, bpm, body, Gap: gap);
        try
        {
            document.Validate(gap.ToString(CultureInfo.InvariantCulture));
            ScoreDocumentWriter.Serialize(document, gap.ToString(CultureInfo.InvariantCulture));
        }
        catch (FormatException e) { return new(null, new[] { new MidiImportProblem(1, 0, e.Message) }, report); }
        return new(document, Array.Empty<MidiImportProblem>(), report);
    }
}
