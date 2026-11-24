using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

/// <summary>
/// WPF rich-text (XAML) -> Unicode PDF / safe HTML, with an HTML fallback for legacy imports.
/// Native WPF font/style semantics are used instead of flattening to a plain text field.
/// </summary>
public static class LettersPdf
{
    public static FlowDocument ReadBody(string body)
    {
        var document = new FlowDocument();
        if (body.StartsWith("xaml:", StringComparison.Ordinal))
        {
            var bytes = Convert.FromBase64String(body[5..]);
            using var memory = new MemoryStream(bytes);
            new TextRange(document.ContentStart, document.ContentEnd).Load(memory, DataFormats.Xaml);
        }
        else
        {
            // Python writes Qt HTML. This fallback is deliberately non-executable and preserves
            // readable text. Exact legacy Qt HTML styling needs a separate importer.
            var value = Regex.Replace(body ?? "", @"(?i)<br\s*/?>|</p\s*>|</li\s*>", "\n");
            value = Regex.Replace(value, "<[^>]+>", "");
            value = WebUtility.HtmlDecode(value);
            foreach (var line in value.Replace("\r", "").Split('\n'))
                document.Blocks.Add(new Paragraph(new Run(line)) { Margin = new Thickness(0, 2, 0, 5) });
        }
        return document;
    }

    public static string SaveBody(FlowDocument body)
    {
        using var stream = new MemoryStream();
        new TextRange(body.ContentStart, body.ContentEnd).Save(stream, DataFormats.Xaml);
        return "xaml:" + Convert.ToBase64String(stream.ToArray());
    }

    public static string BodyText(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";
        var doc = ReadBody(body);
        return new TextRange(doc.ContentStart, doc.ContentEnd).Text.Trim();
    }

    private static string Esc(string? text) => WebUtility.HtmlEncode(text ?? "").Replace("\n", "<br/>");

    private static string HtmlInline(InlineCollection inlines)
    {
        var sb = new StringBuilder();
        foreach (Inline item in inlines)
        {
            switch (item)
            {
                case Run run:
                    var value = Esc(run.Text);
                    if (run.FontWeight == FontWeights.Bold) value = "<b>" + value + "</b>";
                    if (run.FontStyle == FontStyles.Italic) value = "<i>" + value + "</i>";
                    if (run.TextDecorations?.Count > 0) value = "<u>" + value + "</u>";
                    sb.Append(value);
                    break;
                case LineBreak: sb.Append("<br/>"); break;
                case Bold bold: sb.Append("<b>").Append(HtmlInline(bold.Inlines)).Append("</b>"); break;
                case Italic italic: sb.Append("<i>").Append(HtmlInline(italic.Inlines)).Append("</i>"); break;
                case Underline underline: sb.Append("<u>").Append(HtmlInline(underline.Inlines)).Append("</u>"); break;
                case Span span: sb.Append(HtmlInline(span.Inlines)); break;
            }
        }
        return sb.ToString();
    }

    private static void HtmlBlocks(BlockCollection blocks, StringBuilder sb)
    {
        foreach (Block block in blocks)
        {
            switch (block)
            {
                case Paragraph p:
                    var align = p.TextAlignment switch
                    {
                        TextAlignment.Right => "right", TextAlignment.Center => "center",
                        TextAlignment.Justify => "justify", _ => "left"
                    };
                    sb.Append("<p style=\"text-align:").Append(align).Append("\">")
                        .Append(HtmlInline(p.Inlines)).Append("</p>");
                    break;
                case List l:
                    var ordered = l.MarkerStyle is TextMarkerStyle.Decimal or TextMarkerStyle.LowerLatin or TextMarkerStyle.UpperLatin;
                    sb.Append(ordered ? "<ol>" : "<ul>");
                    foreach (ListItem li in l.ListItems)
                    {
                        sb.Append("<li>");
                        HtmlBlocks(li.Blocks, sb);
                        sb.Append("</li>");
                    }
                    sb.Append(ordered ? "</ol>" : "</ul>");
                    break;
                case Section s: HtmlBlocks(s.Blocks, sb); break;
            }
        }
    }

    public static string Html(LetterDocument d)
    {
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\"/><style>")
            .Append("body{font-family:'Times New Roman',serif;font-size:12pt;color:#111;margin:20mm 15mm 18mm 22mm}")
            .Append("p{margin:7pt 0}li{margin:5pt 0}</style></head><body>")
            .Append("<p align=\"center\"><b>").Append(Esc(d.Sender)).Append("</b></p><hr/>")
            .Append("<p align=\"right\">").Append(Esc(d.Recipient)).Append("</p>")
            .Append("<p>").Append(Esc(d.Date)).Append("<br/>Исх. ").Append(Esc(d.Number)).Append("</p>")
            .Append("<p><b>").Append(Esc(d.Subject)).Append("</b></p>")
            .Append("<p align=\"center\">").Append(Esc(d.Greeting)).Append("</p>");
        HtmlBlocks(ReadBody(d.Body).Blocks, sb);
        var included = d.Files.Where(x => x.Kind == "Приложение" && x.Included).ToArray();
        if (included.Length > 0)
        {
            sb.Append("<p><b>Приложения:</b></p><ol>");
            foreach (var file in included)
                sb.Append("<li>").Append(Esc(file.Title)).Append(" — ").Append(file.Pages).Append(" л.</li>");
            sb.Append("</ol>");
        }
        sb.Append("<p>").Append(Esc(d.Signer)).Append("</p></body></html>");
        return sb.ToString();
    }

    public static void ImagePage(string input, string output)
    {
        using var pdf = new PdfDocument();
        var page = pdf.AddPage();
        page.Size = PdfSharp.PageSize.A4;
        using var gfx = XGraphics.FromPdfPage(page);
        using var image = XImage.FromFile(input);
        var width = (double)page.Width - 85;
        var height = (double)page.Height - 102;
        var ratio = Math.Min(width / image.PixelWidth, height / image.PixelHeight);
        gfx.DrawImage(image, ((double)page.Width - image.PixelWidth * ratio) / 2, 51,
            image.PixelWidth * ratio, image.PixelHeight * ratio);
        pdf.Save(output);
    }

    public static void Merge(IEnumerable<string> paths, string output)
    {
        using var result = new PdfDocument();
        foreach (var path in paths)
        {
            using var input = PdfReader.Open(path, PdfDocumentOpenMode.Import);
            if (input.PageCount == 0) throw new InvalidDataException("PDF не содержит страниц: " + path);
            for (var i = 0; i < input.PageCount; i++) result.AddPage(input.Pages[i]);
        }
        result.Save(output);
    }

    public static void Render(LetterDocument letter, string output, Func<LetterAttachment, string> locate)
    {
        if (letter.UseScan)
        {
            var scans = letter.Files.Where(f => f.Kind == "Скан письма").ToArray();
            if (scans.Length == 0) throw new InvalidOperationException("Добавьте сканы письма.");
            var files = scans.Select(locate).ToArray();
            if (files.Length == 1 && Path.GetExtension(files[0]).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(files[0], output, true);
                return;
            }
            var conversionDir = Path.Combine(Path.GetTempPath(), "gmc-letter-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(conversionDir);
            try
            {
                for (var i = 0; i < files.Length; i++)
                {
                    if (Path.GetExtension(files[i]).Equals(".pdf", StringComparison.OrdinalIgnoreCase)) continue;
                    var path = Path.Combine(conversionDir, i + ".pdf");
                    ImagePage(files[i], path);
                    files[i] = path;
                }
                Merge(files, output);
                return;
            }
            finally { Directory.Delete(conversionDir, true); }
        }
        using var book = new PdfDocument();
        book.Info.Title = letter.Subject;
        book.Info.Author = "ГМЦ-Смета";
        using (var canvas = new Canvas(book))
        {
            var company = (letter.Sender ?? "").Replace("\r", "").Split('\n');
            if (company.Length > 0)
            {
                canvas.Paragraph(company[0], true, TextAlignment.Center, 14);
                foreach (var part in company.Skip(1)) canvas.Paragraph(part, false, TextAlignment.Center, 9);
                canvas.Line();
            }
            canvas.Paragraph(letter.Recipient, false, TextAlignment.Right);
            canvas.Gap(7);
            canvas.Paragraph(letter.Date + "\nИсх. " + letter.Number);
            canvas.Paragraph(letter.Subject, true);
            if (!string.IsNullOrWhiteSpace(letter.Greeting))
                canvas.Paragraph(letter.Greeting, false, TextAlignment.Center);
            canvas.Gap(4);
            canvas.Blocks(ReadBody(letter.Body).Blocks);
            var attached = letter.Files.Where(f => f.Kind == "Приложение" && f.Included).ToArray();
            if (attached.Length > 0)
            {
                canvas.Paragraph("Приложения:", true);
                for (var i = 0; i < attached.Length; i++)
                    canvas.Paragraph($"{i + 1}. {attached[i].Title} — {attached[i].Pages} л.");
            }
            canvas.Gap(12);
            canvas.Paragraph(letter.Signer);
        }
        book.Save(output);
    }

    private sealed class Canvas : IDisposable
    {
        private readonly PdfDocument _pdf;
        private XGraphics? _g;
        private double _y;
        private int _page;
        private const double Left = 62.4, Right = 42.5, Top = 51, Bottom = 51;
        private const double PageWidth = 595.3, PageHeight = 841.9, Leading = 17;
        private static readonly XFont Normal = new("Arial", 11, XFontStyleEx.Regular);
        private static readonly XFont Bold = new("Arial", 11, XFontStyleEx.Bold);
        private static readonly XFont Italic = new("Arial", 11, XFontStyleEx.Italic);
        private static readonly XFont BoldItalic = new("Arial", 11, XFontStyleEx.BoldItalic);
        private static readonly XFont Small = new("Arial", 9, XFontStyleEx.Regular);
        private static readonly XFont Header = new("Arial", 14, XFontStyleEx.Bold);
        private readonly List<(string Token, XFont Font)> _line = new();
        private double _lineWidth;

        public Canvas(PdfDocument pdf) { _pdf = pdf; NewPage(); }
        private void NewPage()
        {
            if (_g is not null) ClosePage();
            var page = _pdf.AddPage();
            page.Size = PdfSharp.PageSize.A4;
            _g = XGraphics.FromPdfPage(page);
            _page++;
            _y = Top;
        }
        private void ClosePage()
        {
            _g!.DrawString(_page.ToString(), Small, XBrushes.Gray,
                new XRect(Left, PageHeight - 33, PageWidth - Left - Right, 15), XStringFormats.TopRight);
            _g.Dispose(); _g = null;
        }
        private void Ensure(double height)
        {
            if (_y + height > PageHeight - Bottom) NewPage();
        }
        public void Gap(double height) { Ensure(height); _y += height; }
        public void Line()
        {
            Ensure(17);
            _g!.DrawLine(XPens.Gray, Left, _y, PageWidth - Right, _y);
            _y += 16;
        }
        private void Flush(TextAlignment alignment, double size)
        {
            if (_line.Count == 0) return;
            Ensure(Leading * size / 11);
            var x = alignment switch
            {
                TextAlignment.Right => PageWidth - Right - _lineWidth,
                TextAlignment.Center => Left + (PageWidth - Right - Left - _lineWidth) / 2,
                _ => Left
            };
            var extra = alignment == TextAlignment.Justify && _line.Count > 1
                ? Math.Max(0, (PageWidth - Right - Left - _lineWidth) / (_line.Count - 1)) : 0;
            foreach (var (word, font) in _line)
            {
                var width = _g!.MeasureString(word, font).Width;
                _g.DrawString(word, font, XBrushes.Black,
                    new XRect(x, _y, width + 3, Leading), XStringFormats.TopLeft);
                x += width + extra;
            }
            _y += Leading * size / 11;
            _line.Clear(); _lineWidth = 0;
        }
        private void Token(string token, XFont font, TextAlignment alignment, double size)
        {
            if (token.Contains('\n'))
            {
                foreach (var (part, i) in token.Replace("\r", "").Split('\n').Select((v, i) => (v, i)))
                {
                    if (i > 0) { Flush(alignment, size); _y += 2; }
                    Token(part, font, alignment, size);
                }
                return;
            }
            var width = _g!.MeasureString(token, font).Width;
            if (_lineWidth + width > PageWidth - Left - Right && _line.Count > 0)
                Flush(alignment, size);
            _line.Add((token, font)); _lineWidth += width;
        }
        private void RunText(string value, XFont font, TextAlignment alignment, double size)
        {
            foreach (Match m in Regex.Matches(value ?? "", @"\S+\s*|\s+"))
                Token(m.Value, font, alignment, size);
        }
        public void Paragraph(string? value, bool bold = false,
            TextAlignment alignment = TextAlignment.Left, double size = 11)
        {
            var font = size >= 14 ? Header : size <= 9 ? Small : bold ? Bold : Normal;
            RunText(value ?? "", font, alignment, size);
            Flush(alignment, size);
            Gap(6);
        }
        private void Paragraph(Paragraph p, string prefix)
        {
            var align = p.TextAlignment;
            if (!string.IsNullOrEmpty(prefix)) RunText(prefix, Normal, align, 11);
            foreach (Inline inline in p.Inlines)
                Inline(inline, align);
            Flush(align, 11);
            Gap(5);
        }
        private void Inline(Inline inline, TextAlignment alignment,
            bool inheritedBold = false, bool inheritedItalic = false)
        {
            switch (inline)
            {
                case Run run:
                    var bold = inheritedBold || run.FontWeight == FontWeights.Bold;
                    var italic = inheritedItalic || run.FontStyle == FontStyles.Italic;
                    var font = bold ? (italic ? BoldItalic : Bold) : (italic ? Italic : Normal);
                    RunText(run.Text, font, alignment, 11);
                    break;
                case LineBreak: Flush(alignment, 11); break;
                case Span span:
                    var spanBold = inheritedBold || span is Bold || span.FontWeight == FontWeights.Bold;
                    var spanItalic = inheritedItalic || span is Italic || span.FontStyle == FontStyles.Italic;
                    foreach (Inline child in span.Inlines)
                        Inline(child, alignment, spanBold, spanItalic);
                    break;
            }
        }
        public void Blocks(BlockCollection blocks)
        {
            foreach (Block block in blocks)
            {
                switch (block)
                {
                    case Paragraph p: Paragraph(p, ""); break;
                    case List list:
                        var counter = 0;
                        foreach (ListItem item in list.ListItems)
                        {
                            counter++;
                            var prefix = list.MarkerStyle == TextMarkerStyle.Decimal ? counter + ".  " : "•  ";
                            var first = true;
                            foreach (Block b in item.Blocks)
                            {
                                if (b is Paragraph para)
                                {
                                    Paragraph(para, first ? prefix : "");
                                    first = false;
                                }
                                else if (b is Section section) Blocks(section.Blocks);
                            }
                        }
                        break;
                    case Section section: Blocks(section.Blocks); break;
                }
            }
        }
        public void Dispose()
        {
            if (_g is not null) ClosePage();
        }
    }
}
