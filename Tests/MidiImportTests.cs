using System.Text;
using HarmonicaPlayer;

static class MidiImportTests
{
    internal static byte[] Track(params (int Tick, byte[] Data)[] events)
    {
        using var stream = new MemoryStream(); int previous = 0;
        foreach (var item in events)
        {
            int value = item.Tick - previous; previous = item.Tick;
            var buffer = new List<byte> { (byte)(value & 127) };
            while ((value >>= 7) > 0) buffer.Insert(0, (byte)((value & 127) | 128));
            stream.Write(buffer.ToArray()); stream.Write(item.Data);
        }
        return stream.ToArray();
    }
    internal static byte[] File(int format, int division, params byte[][] tracks)
    {
        using var stream = new MemoryStream();
        void U16(int n) => stream.Write(new[] { (byte)(n >> 8), (byte)n });
        void U32(int n) => stream.Write(new[] { (byte)(n >> 24), (byte)(n >> 16), (byte)(n >> 8), (byte)n });
        stream.Write(Encoding.ASCII.GetBytes("MThd")); U32(6); U16(format); U16(tracks.Length); U16(division);
        foreach (var track in tracks) { stream.Write(Encoding.ASCII.GetBytes("MTrk")); U32(track.Length); stream.Write(track); }
        return stream.ToArray();
    }
    static byte[] On(int pitch, int channel = 0) => new[] { (byte)(0x90 | channel), (byte)pitch, (byte)96 };
    static byte[] Off(int pitch, int channel = 0) => new[] { (byte)(0x80 | channel), (byte)pitch, (byte)0 };
    static byte[] End() => new byte[] { 0xFF, 0x2F, 0 };
    public static void Run()
    {
        int passed = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception("MIDI import: " + name); passed++; }
        void Reject(byte[] bytes, string name)
        {
            bool rejected = false;
            try { MidiImporter.Parse(bytes); } catch (FormatException) { rejected = true; }
            Check(rejected, name);
        }
        bool Near(double a, double b) => Math.Abs(a - b) < .12; // Up to one MIDI tick, not drift per note.
        var source = ScoreTimeline.Create("0:0.01 （#1） 5 5 【3_.】 【4__】 【【1】】:1.25 【【#1】】:1.25 0:0.007", "120", "20");
        var bytes = MidiExporter.Encode(source, "roundtrip");
        var parsed = MidiImporter.Parse(bytes, "roundtrip");
        var converted = MidiImporter.Convert(parsed, parsed.Parts.Single(), 120, 10);
        Check(converted.Success, "exported MIDI imports at chosen gap without changing event timing");
        var restored = ScoreTimeline.Create(converted.Document!.ScoreText, "120", "10");
        var sounding = source.Notes.Where(n => n.MidiPitch >= 0).ToArray();
        var imported = restored.Notes.Where(n => n.MidiPitch >= 0).ToArray();
        Check(sounding.Length == imported.Length, "roundtrip note count");
        for (int i = 0; i < sounding.Length; i++)
        {
            Check(sounding[i].MidiPitch == imported[i].MidiPitch, "roundtrip pitch");
            Check(Near(sounding[i].StartMs, imported[i].StartMs), "roundtrip onset");
            Check(Near(sounding[i].SoundEndMs, imported[i].SoundEndMs), "roundtrip note off including gap compensation");
        }
        Check(Near(source.DurationMs, restored.DurationMs), "leading/trailing rests and total duration");
        for (int pitch = HarmonicaPitchMap.Lowest; pitch <= HarmonicaPitchMap.Highest; pitch++)
            Check(ScoreTimeline.Pitch(ScoreParser.Parse(HarmonicaPitchMap.Token(pitch)).Single()) == pitch, "all supported semitones inverse map");

        var tempo = Track((0, new byte[] { 0xFF, 0x51, 3, 7, 0xA1, 0x20 }),
            (480, new byte[] { 0xFF, 0x51, 3, 0x0A, 0x2C, 0x2B }), (960, End())); // 90 BPM
        var melody = Track((240, On(60)), (720, Off(60)), (960, End()));
        var variable = MidiImporter.Parse(File(1, 480, tempo, melody));
        Check(variable.Parts.Single().Track == 1 && variable.Tempos.Count == 2, "conductor tempo read independently of melody track");
        var variableConversion = MidiImporter.Convert(variable, variable.Parts.Single(), 120, 10);
        Check(variableConversion.Success, "multiple tempi flatten to constant BPM");
        var variableTimeline = ScoreTimeline.Create(variableConversion.Document!.ScoreText, "120", "10");
        Check(Near(variableTimeline.Notes.First(n => n.MidiPitch >= 0).StartMs, 250) &&
            Near(variableTimeline.Notes.First(n => n.MidiPitch >= 0).SoundEndMs, 833.3335) &&
            Near(variableTimeline.DurationMs, 1166.667), "tempo integration preserves seconds");

        var overlap = MidiImporter.Parse(File(0, 1000, Track((0, On(60)), (380, On(62)), (400, Off(60)), (700, Off(62)), (800, End()))));
        var overlapResult = MidiImporter.Convert(overlap, overlap.Parts.Single(), 120, 10);
        Check(overlapResult.Success && overlapResult.Report!.ShortenedNotes == 1, "overlap truncates previous tail");
        var overlapTimeline = ScoreTimeline.Create(overlapResult.Document!.ScoreText, "120", "10");
        Check(Near(overlapTimeline.Notes[0].SoundEndMs, 180) && Near(overlapTimeline.Notes[1].StartMs, 190),
            "overlap preserves later attack with 10ms gap");
        var chord = MidiImporter.Parse(File(0, 480, Track((0, On(60)), (0, On(64)), (240, Off(60)), (240, Off(64)), (480, End()))));
        var highChord = MidiImporter.Convert(chord, chord.Parts.Single(), 120, 10);
        var lowChord = MidiImporter.Convert(chord, chord.Parts.Single(), 120, 10, strategy: MidiMelodyStrategy.Lowest);
        Check(highChord.Success && highChord.Report!.ChordNotesDropped == 1 &&
            ScoreTimeline.Create(highChord.Document!.ScoreText, "120", "10").Notes[0].MidiPitch == 64, "highest chord tone by default");
        Check(lowChord.Success && ScoreTimeline.Create(lowChord.Document!.ScoreText, "120", "10").Notes[0].MidiPitch == 60,
            "lowest chord tone option");

        var running = MidiImporter.Parse(File(0, 480, Track((0, On(60)), (240, new byte[] { 60, 0 }),
            (260, new byte[] { 62, 96 }), (480, new byte[] { 62, 0 }), (500, End()))));
        Check(running.Parts.Single().Notes.Count == 2, "running status and velocity-zero Note Off");
        Check(MidiImporter.Convert(running, running.Parts.Single(), 120, 10).Success, "separate repeated events kept in sequence");
        var channels = MidiImporter.Parse(File(0, 480, Track((0, On(60)), (0, On(64, 1)),
            (240, Off(60)), (240, Off(64, 1)), (480, End()))));
        Check(channels.Parts.Count == 2 && channels.Parts.All(p => MidiImporter.Convert(channels, p, 120, 10).Success),
            "format 0 channels can be selected independently without merging");
        var foreign = MidiImporter.Parse(File(0, 480, Track((0, On(36)), (240, Off(36)), (480, End()))));
        var suggestion = MidiImporter.SuggestTranspose(foreign.Parts.Single());
        Check(suggestion.Semitones == 12 && suggestion.OriginalPlayable == 0 && suggestion.SuggestedPlayable == 1,
            "whole-song transpose suggestion and coverage");
        var foldedForeign = MidiImporter.Convert(foreign, foreign.Parts.Single(), 120, 10);
        Check(foldedForeign.Success && foldedForeign.Report!.Transpose == 0 && foldedForeign.Report.OctaveAdjustedNotes == 1,
            "octave fold does not apply suggested global transpose");
        Check(MidiImporter.Convert(foreign, foreign.Parts.Single(), 120, 10, suggestion.Semitones).Success, "explicit transpose allows import");
        var tooWide = MidiImporter.Parse(File(0, 480, Track((0, On(0)), (200, Off(0)), (240, On(127)), (440, Off(127)), (480, End()))));
        var wideSuggestion = MidiImporter.SuggestTranspose(tooWide.Parts.Single());
        var wideResult = MidiImporter.Convert(tooWide, tooWide.Parts.Single(), 120, 10, wideSuggestion.Semitones);
        Check(wideSuggestion.SuggestedPlayable < 2 && wideResult.Success && wideResult.Report!.OctaveAdjustedNotes > 0,
            "wide range uses per-note octave folding after explicit transpose");
        Check(ScoreTimeline.Create(wideResult.Document!.ScoreText, "120", "10").Notes.Where(n => n.MidiPitch >= 0)
            .All(n => HarmonicaPitchMap.Contains(n.MidiPitch)), "all folded notes inside playable range");
        var shortFile = MidiImporter.Parse(File(0, 480, Track((0, On(60)), (20, Off(60)), (240, End()))));
        var allShort = MidiImporter.Convert(shortFile, shortFile.Parts.Single(), 120, 10);
        Check(!allShort.Success && allShort.Report!.ShortNotesDropped == 1, "all short notes yields explicit empty-result failure");
        var noGap = MidiImporter.Parse(File(0, 480, Track((0, On(60)), (240, Off(60)), (240, On(62)), (480, Off(62)), (480, End()))));
        var noGapResult = MidiImporter.Convert(noGap, noGap.Parts.Single(), 120, 10);
        Check(noGapResult.Success && noGapResult.Report!.ShortenedNotes == 2,
            "legato and trailing gap both shorten tails");
        var noGapTimeline = ScoreTimeline.Create(noGapResult.Document!.ScoreText, "120", "10");
        Check(Near(noGapTimeline.Notes[1].StartMs, 250) && Near(noGapTimeline.DurationMs, 500) &&
            Near(noGapTimeline.Notes[1].SoundEndMs, 490), "legato conversion preserves onsets and track end");
        var narrowGap = MidiImporter.Parse(File(0, 1000, Track((0, On(60)), (400, Off(60)), (410, On(62)), (800, Off(62)), (900, End()))));
        Check(MidiImporter.Convert(narrowGap, narrowGap.Parts.Single(), 120, 10).Report!.ShortenedNotes == 1,
            "5ms source gap causes reported truncation");
        var restFile = MidiImporter.Parse(File(0, 1000, Track((0, On(60)), (400, Off(60)), (421, On(62)), (800, Off(62)), (840, End()))));
        var restResult = MidiImporter.Convert(restFile, restFile.Parts.Single(), 120, 10);
        Check(restResult.Success && ScoreParser.Parse(restResult.Document!.ScoreText).Any(n => n.Degree == 0 && n.Beats < .01),
            "real sub-ms rest retained after explicit gap compensation");

        Reject(Array.Empty<byte>(), "empty file");
        Reject(bytes[..^1], "truncated file");
        Reject(File(2, 480, melody), "format 2");
        Reject(File(0, 0xE728, melody), "SMPTE division explicitly unsupported");
        Reject(File(0, 0, melody), "zero division");
        Reject(File(0, 480, Track((0, On(60)), (20, On(60)), (240, Off(60)), (480, End()))), "unclosed repeated attack remains invalid");
        var repeatedOverlap = MidiImporter.Parse(File(0, 1000, Track((0, On(60)), (200, On(60)),
            (240, Off(60)), (500, Off(60)), (600, End()))));
        var repeatedResult = MidiImporter.Convert(repeatedOverlap, repeatedOverlap.Parts.Single(), 120, 10);
        Check(repeatedResult.Success && repeatedResult.Report!.KeptNotes == 2 && repeatedResult.Report.ShortenedNotes == 1,
            "same-pitch overlap pairs FIFO and keeps separate attacks");
        Reject(File(0, 480, Track((0, On(60)), (480, End()))), "missing Note Off");
        Reject(File(0, 480, Track((0, Off(60)), (480, End()))), "unmatched Note Off");
        Reject(File(0, 480, Track((0, new byte[] { 60, 96 }), (480, End()))), "orphan running status");
        Reject(File(0, 480, Track((0, On(60)), (240, Off(60)))), "missing end of track");
        Reject(File(0, 480, Track((0, new byte[] { 0xFF, 0x51, 3, 0, 0, 0 }), (480, End()))), "invalid zero tempo");
        var changedPitch = MidiImporter.Parse(File(0, 480, Track((0, new byte[] { 0xE0, 0, 65 }), (0, On(60)), (240, Off(60)), (480, End()))));
        Check(MidiImporter.Convert(changedPitch, changedPitch.Parts.Single(), 120, 10).Report!.IgnoredEffects.Any(s => s.Contains("弯音")),
            "pitch bend ignored with report");
        var pedal = MidiImporter.Parse(File(0, 480, Track((0, new byte[] { 0xB0, 64, 127 }), (0, On(60)), (240, Off(60)), (480, End()))));
        Check(MidiImporter.Convert(pedal, pedal.Parts.Single(), 120, 10).Success, "sustain ignored without blocking notes");
        var drums = MidiImporter.Parse(File(0, 480, Track((0, On(60, 9)), (240, Off(60, 9)), (480, End()))));
        Check(!MidiImporter.Convert(drums, drums.Parts.Single(), 120, 10).Success, "percussion not mapped to pitched harmonica");
        var longSilence = MidiImporter.Parse(File(0, 480, Track((0, On(60)), (240, Off(60)), (96000, End()))));
        var longResult = MidiImporter.Convert(longSilence, longSilence.Parts.Single(), 120, 10);
        Check(longResult.Success && ScoreParser.Parse(longResult.Document!.ScoreText).All(n => n.Beats <= 64),
            "long silence split into valid rests without creating extra notes");
        var globalSysEx = MidiImporter.Parse(File(1, 480,
            Track((0, new byte[] { 0xF0, 3, 0x7E, 0, 0xF7 }), (480, End())), melody));
        Check(MidiImporter.Convert(globalSysEx, globalSysEx.Parts.Single(), 120, 10).Report!.IgnoredEffects.Any(s => s.Contains("SysEx")),
            "conductor SysEx reported as ignored");
        var excessive = new MidiImportPart(0, 0, "too many", Enumerable.Repeat(new MidiSourceNote(60, 0, 240), 20001).ToArray(), 480, Array.Empty<string>());
        Check(!MidiImporter.Convert(parsed, excessive, 120, 10).Success, "oversized selection rejected before expensive conversion");
        var conductorPedal = MidiImporter.Parse(File(1, 480,
            Track((0, new byte[] { 0xB0, 64, 127 }), (960, End())), melody));
        Check(MidiImporter.Convert(conductorPedal, conductorPedal.Parts.Single(), 120, 10).Report!.IgnoredEffects.Any(s => s.Contains("踏板")),
            "pedal in separate same-channel track reported");
        var laterBend = MidiImporter.Parse(File(1, 480, melody,
            Track((0, new byte[] { 0xE0, 0, 65 }), (960, End()))));
        Check(MidiImporter.Convert(laterBend, laterBend.Parts.Single(), 120, 10).Report!.IgnoredEffects.Any(s => s.Contains("弯音")),
            "bend in later same-channel track reported");
        var aboveTop = MidiImporter.Parse(File(0, 480, Track((0, On(86)), (240, Off(86)), (480, End()))));
        var aboveTopResult = MidiImporter.Convert(aboveTop, aboveTop.Parts.Single(), 120, 10);
        Check(aboveTopResult.Success && aboveTopResult.Report!.OctaveAdjustedNotes == 1 &&
            ScoreTimeline.Create(aboveTopResult.Document!.ScoreText, "120", "10").Notes[0].MidiPitch == 74,
            "MIDI above highest sharp folds by octave");
        var topSuggestion = MidiImporter.SuggestTranspose(aboveTop.Parts.Single());
        Check(topSuggestion.Semitones == -1 && topSuggestion.OriginalPlayable == 0 && topSuggestion.SuggestedPlayable == 1,
            "top-boundary coverage suggests explicit one-semitone transpose");
        var ornaments = MidiImporter.Parse(File(0, 1000, Track((0, On(60)), (100, On(62)), (120, Off(62)),
            (500, Off(60)), (600, On(64)), (1000, Off(64)), (1200, End()))));
        var ornamentResult = MidiImporter.Convert(ornaments, ornaments.Parts.Single(), 120, 10);
        var ornamentNotes = ScoreTimeline.Create(ornamentResult.Document!.ScoreText, "120", "10").Notes.Where(n => n.MidiPitch >= 0).ToArray();
        Check(ornamentResult.Report!.ShortNotesDropped == 1 && ornamentResult.Report.ShortenedNotes == 0,
            "dropped short ornament does not shorten previous sustained note");
        Check(Near(ornamentNotes[0].SoundEndMs, 250) && Near(ornamentNotes[1].StartMs, 300), "skipped ornament does not compress time");
        var crowded = MidiImporter.Parse(File(0, 1000, Track((0, On(60)), (100, On(62)),
            (300, Off(60)), (600, Off(62)), (800, End()))));
        var crowdedResult = MidiImporter.Convert(crowded, crowded.Parts.Single(), 120, 10);
        Check(crowdedResult.Report!.ShortNotesDropped == 1 && Near(ScoreTimeline.Create(crowdedResult.Document!.ScoreText, "120", "10")
            .Notes.First(n => n.MidiPitch >= 0).StartMs, 50), "later attack wins when shortened previous note cannot fit");
        var boundary = MidiImporter.Parse(File(0, 1000, Track((0, On(60)), (104, Off(60)), (124, End()))));
        foreach (int targetBpm in new[] { 20, 90, 120, 163, 300 })
        {
            var edge = MidiImporter.Convert(boundary, boundary.Parts.Single(), targetBpm, 10);
            Check(edge.Success, $"exact 52ms survives numeric encoding at BPM {targetBpm}: {string.Join("; ", edge.Problems)}");
        }
        var shifted = MidiImporter.Convert(chord, chord.Parts.Single(), 120, 10, -127);
        Check(shifted.Success && shifted.Report!.Transpose == -127 && shifted.Report.OctaveAdjustedNotes == 1,
            "large explicit shift folds without losing pitch class");
        Check(highChord.Report!.SourceNotes == highChord.Report.KeptNotes + highChord.Report.ChordNotesDropped + highChord.Report.ShortNotesDropped,
            "conversion report partitions source note counts");
        foreach (int targetGap in new[] { 10, 20, 5000 })
        {
            var minimum = MidiImporter.Parse(File(0, 1000, Track((0, On(60)), (104, Off(60)), ((52 + targetGap) * 2, End()))));
            for (int targetBpm = 20; targetBpm <= 300; targetBpm++)
            {
                var result = MidiImporter.Convert(minimum, minimum.Parts.Single(), targetBpm, targetGap);
                if (!result.Success) throw new Exception($"52ms boundary BPM={targetBpm}, gap={targetGap}: {string.Join("; ", result.Problems)}");
                var actual = ScoreTimeline.Create(result.Document!.ScoreText, targetBpm.ToString(), targetGap.ToString());
                if (!Near(actual.DurationMs, 52 + targetGap)) throw new Exception("Minimum-note round trip drifted");
            }
            Check(true, "52ms boundary across every supported BPM at gap " + targetGap);
        }
        Console.WriteLine($"PASS MIDI import: {passed} checks");
    }
}

