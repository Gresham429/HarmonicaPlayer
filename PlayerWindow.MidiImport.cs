using System.Windows;
using System.Windows.Controls;

namespace HarmonicaPlayer;

public sealed partial class PlayerWindow
{
    private readonly Button importMidi = new() { Content = "导入 MIDI", Margin = new Thickness(5), Padding = new Thickness(10, 6, 10, 6) };
    private Task ImportMidiAsync() => DocumentOperation(async () =>
    {
        string? path = scoreDialogs.OpenMidi(this);
        if (path == null) return false;
        // MIDI decoding is bounded but may still be expensive; keep the WPF dispatcher free.
        var file = await Task.Run(() => MidiImporter.ReadAsync(path));
        if (scoreDialogs.ConvertMidi(this, file) is not ScoreDocument document) return false;
        if (!await ConfirmUnsavedAsync()) return false;
        ApplyDocument(document);
        // This is a new, unsaved TXT document, not the source .mid file.
        cleanDocument = ("", "", "", ""); UpdateDocumentTitle();
        documentWarning.Text = $"由 MIDI“{System.IO.Path.GetFileName(path)}”转换，原 MIDI 未修改。请先试听检查，再保存 TXT 或游戏演奏。";
        status.Text = "MIDI 已尽力转换为可编辑谱面，尚未保存 TXT。请先试听确认。";
        return true;
    });
}

public sealed class MidiImportDialog : Window
{
    public ScoreDocument? Document { get; private set; }
    private readonly MidiImportFile file;
    private readonly ComboBox part = new() { MinWidth = 380, DisplayMemberPath = nameof(MidiImportPart.Label) };
    private readonly TextBox tempo = new() { Width = 65 };
    private readonly TextBox gap = new() { Width = 65, Text = "10" };
    private readonly TextBox transpose = new() { Width = 65, Text = "0" };
    private readonly ComboBox melody = new() { ItemsSource = new[] { "最高音", "最低音" }, SelectedIndex = 0, Width = 100 };
    private readonly TextBlock reportSummary = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox details = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 180 };
    private readonly TextBlock summary = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock recommendation = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox result = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = 220 };
    private readonly Button accept = new() { Content = "导入到编辑器", Padding = new Thickness(12, 6, 12, 6), IsDefault = true };
    private MidiTransposeSuggestion? suggestion;
    private MidiConversion? conversion;

    public MidiImportDialog(MidiImportFile file)
    {
        this.file = file;
        Title = "导入 MIDI · 尽力转换"; Width = 760; Height = 720; MinWidth = 560; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(16) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 6, 12, 6), IsCancel = true };
        buttons.Children.Add(accept); buttons.Children.Add(cancel); DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        var panel = new StackPanel();
        root.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        panel.Children.Add(new TextBlock { Text = "MIDI 将尽力转换，效果可能与原曲不同，请先试听确认。", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = "选择轨道／通道；同一时刻起音的和弦按所选方式取一个音。", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(part); panel.Children.Add(summary);
        var row = new WrapPanel { Margin = new Thickness(0, 12, 0, 12) };
        void Field(string label, TextBox box) { row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0) }); row.Children.Add(box); }
        Field("固定 BPM", tempo); Field("gap（ms）", gap); Field("整体移调（半音）", transpose);
        row.Children.Add(new TextBlock { Text = " 和弦取音 ", VerticalAlignment = VerticalAlignment.Center }); row.Children.Add(melody);
        panel.Children.Add(row);
        panel.Children.Add(new TextBlock { Text = "保留起音时间；必要时截短音尾以留出 gap，过短音跳过，超音域音折叠八度。忽略弯音、踏板、音色及 SysEx 效果。整体移调仅在主动设置后应用。", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(recommendation);
        var apply = new Button { Content = "使用建议移调", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 8) };
        panel.Children.Add(apply); panel.Children.Add(reportSummary);
        panel.Children.Add(new Expander { Header = "转换详情", Content = details });
        panel.Children.Add(result); Content = root;
        tempo.Text = file.SuggestedBpm.ToString();
        part.ItemsSource = file.Parts;
        part.SelectionChanged += (_, _) =>
        {
            suggestion = part.SelectedItem is MidiImportPart selected ? MidiImporter.SuggestTranspose(selected) : null;
            Refresh();
        };
        foreach (var box in new[] { tempo, gap, transpose }) box.TextChanged += (_, _) => Refresh();
        melody.SelectionChanged += (_, _) => Refresh();
        apply.Click += (_, _) => { if (suggestion != null) transpose.Text = suggestion.Semitones.ToString(); };
        accept.Click += (_, _) =>
        {
            Refresh();
            if (conversion?.Success == true) { Document = conversion.Document; DialogResult = true; }
        };
        part.SelectedIndex = 0;
    }
    private void Refresh()
    {
        accept.IsEnabled = false; conversion = null; Document = null; reportSummary.Text = ""; details.Text = "";
        if (part.SelectedItem is not MidiImportPart selected) return;
        int low = selected.Notes.Min(n => n.Pitch), high = selected.Notes.Max(n => n.Pitch);
        summary.Text = $"{selected.Label}\n音域：{HarmonicaPitchMap.Name(low)}～{HarmonicaPitchMap.Name(high)}；" +
            $"速度：{(file.Tempos.Count > 1 ? $"{file.Tempos.Count} 段速度" : $"{60000000.0 / file.Tempos[0].Microseconds:0.###} BPM")}。";
        if (suggestion != null)
            recommendation.Text = $"建议整体移调 {suggestion.Semitones:+0;-0;0} 半音：原始音域覆盖率 {suggestion.OriginalPlayable}/{suggestion.Total}（{100.0 * suggestion.OriginalPlayable / suggestion.Total:0.##}%）→ 建议移调后 {suggestion.SuggestedPlayable}/{suggestion.Total}（{100.0 * suggestion.SuggestedPlayable / suggestion.Total:0.##}%）。仅供选择，未自动应用。";
        try
        {
            int targetTempo = PlaybackValidation.ParseBpm(tempo.Text), targetGap = PlaybackValidation.ParseGap(gap.Text);
            if (!int.TryParse(transpose.Text, out int shift)) throw new FormatException("整体移调必须是整数半音。");
            conversion = MidiImporter.Convert(file, selected, targetTempo, targetGap, shift,
                melody.SelectedIndex == 1 ? MidiMelodyStrategy.Lowest : MidiMelodyStrategy.Highest);
            if (conversion.Report is { } report)
            {
                reportSummary.Text = report.Summary;
                details.Text = string.Join("\n", report.IgnoredEffects.Concat(report.Adjustments.Take(1000).Select(a => a.ToString()))) +
                    (report.Adjustments.Count > 1000 ? "\n仅展示前 1000 条，统计包含全部调整。" : "");
            }
            int playable = selected.Notes.Count(n => HarmonicaPitchMap.Contains(n.Pitch + shift));
            summary.Text += $"\n当前移调 {shift:+0;-0;0} 半音：{playable}/{selected.Notes.Count} 音在支持范围内。";
            if (conversion.Success)
            {
                string body = conversion.Document!.ScoreText;
                result.Text = "转换完成，请先试听确认。保留所选轨道的时间范围，调整结果见上方。\n\n" +
                    body[..Math.Min(body.Length, 30000)] + (body.Length > 30000 ? "\n（预览截断，导入时保留完整谱面）" : "");
                accept.IsEnabled = true;
            }
            else
                result.Text = $"无法导入：共 {conversion.Problems.Count} 项问题，未修改原 MIDI。\n" +
                    string.Join("\n\n", conversion.Problems.Take(30)) + (conversion.Problems.Count > 30 ? "\n仅显示前 30 项。" : "");
        }
        catch (FormatException e) { result.Text = e.Message; }
    }
}

