// Stage 3 groundwork (#1099): what RichEditBox can hold for an HTML message, and what a screen
// reader makes of it. Started with "--rich", the spike's body is loaded with one of each thing
// QuickMail's HTML mode has today: a heading, bold text, a link, a bulleted and a numbered list,
// a quote, a picture with alternative text, and a table. Ctrl+Shift+D writes what the document
// then reports about itself to %TEMP%\spike-rich-dump.txt.
using System;
using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Controls;

namespace EditorHostSpike;

static class RichSample
{
    public static void Load(RichEditBox editor)
    {
        var doc = editor.Document;
        string[] lines =
        {
            "Heading one",                                   // 0
            "Plain paragraph with bold and a link here.",    // 1
            "First bullet",                                  // 2
            "Second bullet",                                 // 3
            "Step one",                                      // 4
            "Step two",                                      // 5
            "Quoted line",                                   // 6
            "Picture: ",                                     // 7
            "Before the table",                              // 8
        };
        doc.SetText(TextSetOptions.None, string.Join("\r", lines) + "\r");

        int[] starts = new int[lines.Length];
        for (int i = 0, at = 0; i < lines.Length; at += lines[i].Length + 1, i++) starts[i] = at;
        ITextRange Para(int i) => doc.GetRange(starts[i], starts[i] + lines[i].Length);
        ITextRange Word(int i, string word)
        {
            int at = starts[i] + lines[i].IndexOf(word, StringComparison.Ordinal);
            return doc.GetRange(at, at + word.Length);
        }

        // Later paragraphs first, so earlier positions stay valid as things are inserted.
        // A table, as RTF: there is no table API in Microsoft.UI.Text.
        int end = starts[8] + lines[8].Length + 1;
        doc.GetRange(end, end).SetText(TextSetOptions.FormatRtf,
            @"{\rtf1\ansi\trowd\cellx2500\cellx5000\intbl Cell A1\cell Cell B1\cell\row" +
            @"\trowd\cellx2500\cellx5000\intbl Cell A2\cell Cell B2\cell\row\pard After the table\par}");

        // A picture with alternative text.
        int pictureAt = starts[7] + lines[7].Length;
        using (var png = new MemoryStream(MakePng()))
            doc.GetRange(pictureAt, pictureAt).InsertImage(48, 32, 0, VerticalCharacterAlignment.Baseline,
                "A red rectangle", png.AsRandomAccessStream());

        // A hidden marker just before the picture: does it stay out of sight and out of speech?
        doc.GetRange(pictureAt, pictureAt).SetText(TextSetOptions.None, "{img7}");
        doc.GetRange(pictureAt, pictureAt + 6).CharacterFormat.Hidden = FormatEffect.On;

        // A quote: no such thing in RichEdit; an indent stands in. Do these flags survive?
        Para(6).ParagraphFormat.SetIndents(0, 24, 0);
        Para(6).ParagraphFormat.KeepTogether = FormatEffect.On;
        Para(6).ParagraphFormat.KeepWithNext = FormatEffect.On;
        Para(6).ParagraphFormat.NoLineNumber = FormatEffect.On;
        Para(6).ParagraphFormat.PageBreakBefore = FormatEffect.Off;
        Para(6).ParagraphFormat.RightIndent = 3;

        var numbered = doc.GetRange(starts[4], starts[5] + lines[5].Length);
        numbered.ParagraphFormat.ListType = MarkerType.Arabic;
        numbered.ParagraphFormat.ListStyle = MarkerStyle.Period;
        var bullets = doc.GetRange(starts[2], starts[3] + lines[3].Length);
        bullets.ParagraphFormat.ListType = MarkerType.Bullet;

        Word(1, "bold").CharacterFormat.Bold = FormatEffect.On;
        Word(1, "link").Link = "\"https://example.com/\"";
        Para(0).ParagraphFormat.Style = ParagraphStyle.Heading1;
        Para(0).CharacterFormat.Size = 20;
        Para(0).CharacterFormat.Bold = FormatEffect.On;

        doc.Selection.SetRange(0, 0);
    }

    public static string Dump(RichEditBox editor)
    {
        var doc = editor.Document;
        var sb = new StringBuilder();
        doc.GetText(TextGetOptions.None, out var raw);
        sb.AppendLine($"RAW ({raw.Length} chars): {Show(raw)}");
        doc.GetText(TextGetOptions.NoHidden, out var visible);
        sb.AppendLine($"NOHIDDEN ({visible.Length} chars): {Show(visible)}");
        var sel = doc.Selection;
        sb.AppendLine($"SELECTION {sel.StartPosition}-{sel.EndPosition}");
        sb.AppendLine();

        int at = 0, guard = 0;
        while (at < raw.Length && guard++ < 500)
        {
            var p = doc.GetRange(at, at);
            p.Expand(TextRangeUnit.Paragraph);
            if (p.EndPosition <= at) break;
            var pf = p.ParagraphFormat;
            p.GetText(TextGetOptions.None, out var text);
            sb.AppendLine($"PARA {p.StartPosition}-{p.EndPosition} style={pf.Style} list={pf.ListType}/{pf.ListStyle} " +
                          $"level={pf.ListLevelIndex} start={pf.ListStart} left={pf.LeftIndent} first={pf.FirstLineIndent} right={pf.RightIndent} " +
                          $"keep={pf.KeepTogether} keepNext={pf.KeepWithNext} noLine={pf.NoLineNumber} align={pf.Alignment} : {Show(text)}");

            int runAt = p.StartPosition, runGuard = 0;
            while (runAt < p.EndPosition && runGuard++ < 200)
            {
                var run = doc.GetRange(runAt, runAt);
                run.MoveEnd(TextRangeUnit.CharacterFormat, 1);
                int runEnd = Math.Min(run.EndPosition, p.EndPosition);
                if (runEnd <= runAt) break;
                var r = doc.GetRange(runAt, runEnd);
                var cf = r.CharacterFormat;
                r.GetText(TextGetOptions.None, out var runText);
                sb.AppendLine($"    RUN {runAt}-{runEnd} bold={cf.Bold} italic={cf.Italic} underline={cf.Underline} " +
                              $"strike={cf.Strikethrough} hidden={cf.Hidden} size={cf.Size} font={cf.Name} linkType={cf.LinkType} link={Show(r.Link)} : {Show(runText)}");
                runAt = runEnd;
            }
            at = p.EndPosition;
        }

        sb.AppendLine();
        doc.GetText(TextGetOptions.FormatRtf, out var rtf);
        sb.AppendLine($"RTF ({rtf.Length} chars), first 3000:");
        sb.AppendLine(rtf.Length > 3000 ? rtf[..3000] : rtf);

        var path = Path.Combine(Path.GetTempPath(), "spike-rich-dump.txt");
        File.WriteAllText(path, sb.ToString());
        return path;
    }

    /// <summary>Control characters and anything outside ASCII as \uXXXX, so marks can be seen.</summary>
    static string Show(string? text)
    {
        if (text == null) return "<null>";
        var sb = new StringBuilder();
        foreach (char c in text)
        {
            if (c is >= ' ' and < (char)127) sb.Append(c);
            else sb.Append($"\\u{(int)c:X4}");
        }
        return "\"" + sb + "\"";
    }

    static byte[] MakePng()
    {
        const int w = 48, h = 32;
        var pixels = new byte[w * h * 4];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i + 2] = 200; pixels[i + 3] = 255; }
        var bitmap = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
