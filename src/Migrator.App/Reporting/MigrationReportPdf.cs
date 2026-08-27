namespace Migrator.App.Reporting;

using System.Globalization;
using PdfSharp;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

/// <summary>
/// Draws the batch report.
///
/// <para>The document is built in memory and handed straight to the browser: it carries
/// plain-text passwords, so the one place it is never written is the machine running the
/// migration. Where it goes after the download is the operator's decision, and the last
/// page says so.</para>
/// </summary>
internal static class MigrationReportPdf
{
    private static readonly CultureInfo Turkish = new("tr-TR");

    // A4 in points; PageSize.A4 below produces exactly this.
    private const double PageWidth = 595.276;
    private const double PageHeight = 841.890;
    private const double Margin = 42;
    private const double FooterBand = 28;
    private const double ContentWidth = PageWidth - 2 * Margin;

    private static readonly XColor Ink = XColor.FromArgb(24, 24, 27);
    private static readonly XColor Muted = XColor.FromArgb(113, 113, 122);
    private static readonly XColor Rule = XColor.FromArgb(212, 212, 216);
    private static readonly XColor Panel = XColor.FromArgb(244, 244, 245);
    private static readonly XColor Good = XColor.FromArgb(21, 128, 61);
    private static readonly XColor Bad = XColor.FromArgb(185, 28, 28);
    private static readonly XColor NoticeFill = XColor.FromArgb(254, 243, 199);
    private static readonly XColor NoticeEdge = XColor.FromArgb(217, 119, 6);

    public static byte[] Build(MigrationReport report)
    {
        ReportFonts.Register();

        using var document = new PdfDocument();
        document.Info.Title = TitleFor(report.Mode);
        document.Info.Creator = "SQL → SQL Taşıyıcı";

        var layout = new Layout(document);
        DrawHeader(layout, report);
        DrawStats(layout, report);
        DrawResults(layout, report);
        if (report.HasSchemaNotes) DrawSchemaNotes(layout, report);
        if (report.HasUsers)
        {
            DrawUsers(layout, report);
            DrawPasswordNotice(layout);
        }
        layout.Close();

        DrawFooters(document);

        using var buffer = new MemoryStream();
        document.Save(buffer, false);
        return buffer.ToArray();
    }

    // ── Sections ─────────────────────────────────────────────────────────────

    private static void DrawHeader(Layout layout, MigrationReport report)
    {
        var gfx = layout.Gfx;
        gfx.DrawString(TitleFor(report.Mode), Font(19, true), Brush(Ink),
            new XRect(Margin, layout.Y, ContentWidth, 24), XStringFormats.TopLeft);
        layout.Y += 25;

        var stamp = report.CompletedAt.ToLocalTime().ToString("d MMMM yyyy HH:mm", Turkish);
        gfx.DrawString($"{stamp} tarihinde oluşturuldu", Font(9), Brush(Muted),
            new XRect(Margin, layout.Y, ContentWidth, 12), XStringFormats.TopLeft);
        layout.Y += 20;

        gfx.DrawLine(new XPen(Rule, 0.75), Margin, layout.Y, Margin + ContentWidth, layout.Y);
        layout.Y += 14;

        Field(layout, "Kaynak sunucu", report.SourceServer);
        Field(layout, "Hedef sunucu", report.TargetServer);
        Field(layout, "Mod", report.Mode switch
        {
            RunMode.VerifyOnly => "Yalnız doğrulama — hiçbir veri taşınmadı",
            RunMode.ProvisionOnly => "Yalnız veritabanı oluşturma — hiçbir veri taşınmadı",
            _ => "Taşıma — veri kopyalandı ve doğrulandı",
        });
        layout.Y += 8;
    }

    /// <summary>
    /// The first line of the document, and the one a reader takes at face value: a run that
    /// moved nothing must not head its report with the word for moving.
    /// </summary>
    internal static string TitleFor(RunMode mode) => mode switch
    {
        RunMode.VerifyOnly => "Veritabanı Doğrulama Raporu",
        RunMode.ProvisionOnly => "Veritabanı Hazırlama Raporu",
        _ => "Veritabanı Taşıma Raporu",
    };

    /// <summary>The same distinction in the name the browser saves the file under.</summary>
    internal static string FileNameFor(RunMode mode) => mode switch
    {
        RunMode.VerifyOnly => "dogrulama-raporu",
        RunMode.ProvisionOnly => "hazirlama-raporu",
        _ => "tasima-raporu",
    };

    private static void Field(Layout layout, string label, string value)
    {
        layout.Gfx.DrawString(label, Font(9), Brush(Muted),
            new XRect(Margin, layout.Y, 100, 13), XStringFormats.TopLeft);
        layout.Gfx.DrawString(Fit(layout.Gfx, value, Font(9.5), ContentWidth - 106), Font(9.5), Brush(Ink),
            new XRect(Margin + 106, layout.Y, ContentWidth - 106, 13), XStringFormats.TopLeft);
        layout.Y += 15;
    }

    private static void DrawStats(Layout layout, MigrationReport report)
    {
        const double height = 46;
        const double gap = 8;
        layout.Ensure(height + 14);

        // The fourth tile is whatever the run actually produced. A provisioning run moves no
        // rows, and a "0" under "taşınan satır" reads as a failed migration rather than as
        // a mode that was never going to move any.
        var tiles = new (string Value, string Label)[]
        {
            (report.Databases.Count.ToString(Turkish), "veritabanı"),
            (report.SucceededCount.ToString(Turkish), "başarılı"),
            (report.FailedCount.ToString(Turkish), "başarısız"),
            report.Mode == RunMode.ProvisionOnly
                ? (report.CreatedCount.ToString(Turkish), "oluşturulan")
                : (report.TotalRows.ToString("N0", Turkish), "taşınan satır"),
        };

        var width = (ContentWidth - gap * (tiles.Length - 1)) / tiles.Length;
        for (var i = 0; i < tiles.Length; i++)
        {
            var x = Margin + i * (width + gap);
            layout.Gfx.DrawRectangle(Brush(Panel), new XRect(x, layout.Y, width, height));
            var accent = tiles[i].Label switch
            {
                "başarılı" when report.SucceededCount > 0 => Good,
                "başarısız" when report.FailedCount > 0 => Bad,
                _ => Ink,
            };
            layout.Gfx.DrawString(Fit(layout.Gfx, tiles[i].Value, Font(15, true), width - 16), Font(15, true), Brush(accent),
                new XRect(x + 8, layout.Y + 7, width - 16, 19), XStringFormats.TopLeft);
            layout.Gfx.DrawString(tiles[i].Label, Font(8), Brush(Muted),
                new XRect(x + 8, layout.Y + 28, width - 16, 11), XStringFormats.TopLeft);
        }
        layout.Y += height + 20;
    }

    private static void DrawResults(Layout layout, MigrationReport report)
    {
        // Provisioning has no row count and no duration worth printing, so the column that
        // carries the outcome is wider and says what happened to the database instead.
        var provisioning = report.Mode == RunMode.ProvisionOnly;
        var columns = provisioning
            ? new[]
            {
                new Column("#", 22),
                new Column("Kaynak veritabanı", 145),
                new Column("Hedef veritabanı", 145),
                new Column("Sonuç", 148),
                new Column("Durum", 55),
            }
            : new[]
            {
                new Column("#", 22),
                new Column("Kaynak veritabanı", 155),
                new Column("Hedef veritabanı", 155),
                new Column("Satır", 55, true),
                new Column("Süre", 55, true),
                new Column("Durum", 73),
            };

        Heading(layout, provisioning ? "Hazırlama sonuçları" : "Taşıma sonuçları");
        TableHeader(layout, columns);

        var index = 0;
        foreach (var database in report.Databases)
        {
            index++;
            var status = database.Succeeded ? "Başarılı" : "Başarısız";
            var cells = provisioning
                ? new[]
                {
                    index.ToString(Turkish),
                    database.SourceDatabase,
                    database.TargetDatabase,
                    // A failure's reason can be longer than any column, and a truncated
                    // reason is a wrong one — it goes to the note line below instead.
                    database.Succeeded ? database.Note : "—",
                    status,
                }
                : new[]
                {
                    index.ToString(Turkish),
                    database.SourceDatabase,
                    database.TargetDatabase,
                    database.RowsCopied.ToString("N0", Turkish),
                    FormatDuration(database.Duration),
                    status,
                };
            Row(layout, columns, cells, database.Succeeded ? Good : Bad, cells.Length - 1);

            // The note explains a failure and adds nothing to a success, where the row
            // count — or, when provisioning, the outcome column — already said everything.
            if (!database.Succeeded && database.Note.Length > 0)
                NoteLine(layout, database.Note);
        }
        layout.Y += 14;
    }

    /// <summary>
    /// Credentials get one block each instead of a table row.
    ///
    /// <para>A table has to fit a value into a column, and the only ways to do that are to
    /// shrink it or to cut it off. A cut-off password is not a shorter password, it is a
    /// wrong one — and the operator has no way to tell, because the ellipsis looks like
    /// formatting. A block can wrap, so nothing here is ever abbreviated.</para>
    /// </summary>
    /// <summary>
    /// What the run did that no number above can show.
    ///
    /// <para>A report whose only measure is "rows arrived" hides the interesting half: a
    /// column filled with an invented zero, a source column with nowhere to go, a schema
    /// left behind. Those scroll past in the log and are gone; here they keep.</para>
    /// </summary>
    private static void DrawSchemaNotes(Layout layout, MigrationReport report)
    {
        Heading(layout, "Şema notları");

        foreach (var database in report.Databases)
        {
            if (database.SchemaNotes is not { Count: > 0 } notes) continue;

            layout.Ensure(30);
            layout.Gfx.DrawString(database.TargetDatabase, Font(9.5, true), Brush(Ink),
                new XRect(Margin, layout.Y, ContentWidth, 13), XStringFormats.TopLeft);
            layout.Y += 15;
            foreach (var note in notes) NoteLine(layout, "• " + note);
            layout.Y += 6;
        }
        layout.Y += 8;
    }

    private static void DrawUsers(Layout layout, MigrationReport report)
    {
        Heading(layout, "Veritabanı kullanıcıları");
        foreach (var database in report.Databases.Where(d => d.UserName is not null))
            DrawUserBlock(layout, database);
    }

    private static void DrawUserBlock(Layout layout, DatabaseOutcome database)
    {
        const double labelWidth = 64;
        var valueWidth = ContentWidth - labelWidth - 14;
        var valueFont = Font(9.5);
        var noteFont = Font(8.5);

        var fields = new (string Label, string Value)[]
        {
            ("Kullanıcı", database.UserName!),
            ("Parola", database.Password ?? "(değişmedi — rol zaten vardı)"),
        };
        var wrapped = fields
            .Select(f => (f.Label, Lines: WrapHard(layout.Gfx, f.Value, valueFont, valueWidth).ToList()))
            .ToList();
        var noteLines = database.UserNote is { Length: > 0 }
            ? WrapHard(layout.Gfx, database.UserNote, noteFont, valueWidth).ToList()
            : new List<string>();

        var height = 18 + wrapped.Sum(f => f.Lines.Count * 13) + noteLines.Count * 11 + 8;
        layout.Ensure(height);

        var top = layout.Y;
        layout.Gfx.DrawString(database.TargetDatabase, Font(10.5, true), Brush(Ink),
            new XRect(Margin + 14, layout.Y, ContentWidth - 100, 14), XStringFormats.TopLeft);
        layout.Gfx.DrawString(database.UserCreated ? "Oluşturuldu" : "Zaten vardı", Font(8.5),
            Brush(database.UserCreated ? Good : Muted),
            new XRect(Margin, layout.Y + 1, ContentWidth, 12), XStringFormats.TopRight);
        layout.Y += 17;

        foreach (var (label, lines) in wrapped)
        {
            layout.Gfx.DrawString(label, Font(8.5), Brush(Muted),
                new XRect(Margin + 14, layout.Y + 1, labelWidth, 12), XStringFormats.TopLeft);
            foreach (var line in lines)
            {
                layout.Gfx.DrawString(line, valueFont, Brush(Ink),
                    new XRect(Margin + 14 + labelWidth, layout.Y, valueWidth, 13), XStringFormats.TopLeft);
                layout.Y += 13;
            }
        }

        foreach (var line in noteLines)
        {
            layout.Gfx.DrawString(line, noteFont, Brush(Muted),
                new XRect(Margin + 14 + labelWidth, layout.Y, valueWidth, 11), XStringFormats.TopLeft);
            layout.Y += 11;
        }

        layout.Gfx.DrawLine(new XPen(Rule, 2), Margin + 1, top, Margin + 1, layout.Y);
        layout.Y += 8;
    }

    private static void DrawPasswordNotice(Layout layout)
    {
        const string text =
            "Bu belge düz metin parolalar içerir. Parolalar yalnızca burada görünür — uygulama onları " +
            "hiçbir yere kaydetmez, bu yüzden belgeyi kaybederseniz geri alınamaz; parolayı sıfırlamak " +
            "gerekir. Belgeyi güvenli bir yerde saklayın, e-posta ile paylaşmayın, işiniz bitince silin.";

        var font = Font(8.5);
        var lines = WrapHard(layout.Gfx, text, font, ContentWidth - 24).ToList();
        var height = 16 + lines.Count * 12;
        layout.Ensure(height);

        var box = new XRect(Margin, layout.Y, ContentWidth, height);
        layout.Gfx.DrawRectangle(Brush(NoticeFill), box);
        layout.Gfx.DrawLine(new XPen(NoticeEdge, 2.5), Margin + 1.25, layout.Y, Margin + 1.25, layout.Y + height);

        var y = layout.Y + 8;
        foreach (var line in lines)
        {
            layout.Gfx.DrawString(line, font, Brush(Ink),
                new XRect(Margin + 14, y, ContentWidth - 24, 12), XStringFormats.TopLeft);
            y += 12;
        }
        layout.Y += height + 12;
    }

    // ── Table primitives ─────────────────────────────────────────────────────

    private readonly record struct Column(string Header, double Width, bool AlignRight = false);

    private static void Heading(Layout layout, string text)
    {
        // A new section owns the page from here on; the previous table's header must not
        // reappear after the next break.
        layout.RepeatHeader = null;
        layout.Ensure(46);
        layout.Gfx.DrawString(text, Font(12, true), Brush(Ink),
            new XRect(Margin, layout.Y, ContentWidth, 16), XStringFormats.TopLeft);
        layout.Y += 20;
    }

    private static void TableHeader(Layout layout, Column[] columns)
    {
        const double height = 20;
        layout.Ensure(height + 18);
        layout.Gfx.DrawRectangle(Brush(Panel), new XRect(Margin, layout.Y, ContentWidth, height));

        var x = Margin;
        foreach (var column in columns)
        {
            layout.Gfx.DrawString(column.Header, Font(8.5, true), Brush(Muted),
                new XRect(x + 6, layout.Y + 5, column.Width - 12, 12),
                column.AlignRight ? XStringFormats.TopRight : XStringFormats.TopLeft);
            x += column.Width;
        }
        layout.Y += height;
        layout.RepeatHeader = () => TableHeader(layout, columns);
    }

    private static void Row(Layout layout, Column[] columns, string[] cells, XColor lastColumnColor, int coloredFrom)
    {
        const double height = 18;
        if (layout.Y + height > layout.BottomLimit)
        {
            layout.NewPage();
            layout.RepeatHeader?.Invoke();
        }

        var x = Margin;
        for (var i = 0; i < columns.Length; i++)
        {
            var font = Font(9);
            var color = i >= coloredFrom ? lastColumnColor : Ink;
            layout.Gfx.DrawString(Fit(layout.Gfx, cells[i], font, columns[i].Width - 12), font, Brush(color),
                new XRect(x + 6, layout.Y + 4, columns[i].Width - 12, 12),
                columns[i].AlignRight ? XStringFormats.TopRight : XStringFormats.TopLeft);
            x += columns[i].Width;
        }
        layout.Y += height;
        layout.Gfx.DrawLine(new XPen(Rule, 0.5), Margin, layout.Y, Margin + ContentWidth, layout.Y);
    }

    private static void NoteLine(Layout layout, string note)
    {
        var font = Font(8.5);
        foreach (var line in WrapHard(layout.Gfx, note, font, ContentWidth - 30))
        {
            if (layout.Y + 13 > layout.BottomLimit) layout.NewPage();
            layout.Gfx.DrawString(line, font, Brush(Muted),
                new XRect(Margin + 24, layout.Y + 2, ContentWidth - 30, 12), XStringFormats.TopLeft);
            layout.Y += 13;
        }
        layout.Gfx.DrawLine(new XPen(Rule, 0.5), Margin, layout.Y, Margin + ContentWidth, layout.Y);
    }

    private static void DrawFooters(PdfDocument document)
    {
        for (var i = 0; i < document.PageCount; i++)
        {
            using var gfx = XGraphics.FromPdfPage(document.Pages[i], XGraphicsPdfPageOptions.Append);
            var y = PageHeight - Margin + 4;
            gfx.DrawLine(new XPen(Rule, 0.5), Margin, y - 6, Margin + ContentWidth, y - 6);
            gfx.DrawString("SQL → SQL Taşıyıcı", Font(8), Brush(Muted),
                new XRect(Margin, y, ContentWidth / 2, 11), XStringFormats.TopLeft);
            gfx.DrawString($"Sayfa {i + 1} / {document.PageCount}", Font(8), Brush(Muted),
                new XRect(Margin + ContentWidth / 2, y, ContentWidth / 2, 11), XStringFormats.TopRight);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private sealed class Layout
    {
        private readonly PdfDocument _document;

        public Layout(PdfDocument document)
        {
            _document = document;
            NewPage();
        }

        public XGraphics Gfx { get; private set; } = null!;
        public double Y { get; set; }
        public double BottomLimit => PageHeight - Margin - FooterBand;

        /// <summary>Redraws the current table's header after a page break.</summary>
        public Action? RepeatHeader { get; set; }

        public void NewPage()
        {
            Gfx?.Dispose();
            var page = _document.AddPage();
            page.Size = PageSize.A4;
            Gfx = XGraphics.FromPdfPage(page);
            Y = Margin;
        }

        public void Ensure(double needed)
        {
            if (Y + needed > BottomLimit) NewPage();
        }

        public void Close() => Gfx?.Dispose();
    }

    private static XFont Font(double size, bool bold = false)
        => new(ReportFonts.Family, size, bold ? XFontStyleEx.Bold : XFontStyleEx.Regular);

    private static XSolidBrush Brush(XColor color) => new(color);

    private static string FormatDuration(TimeSpan duration) => duration.TotalSeconds switch
    {
        < 1 => $"{duration.TotalMilliseconds:N0} ms",
        < 60 => $"{duration.TotalSeconds.ToString("N1", Turkish)} sn",
        _ => $"{(int)duration.TotalMinutes} dk {duration.Seconds} sn",
    };

    private static string Fit(XGraphics gfx, string text, XFont font, double width)
    {
        if (gfx.MeasureString(text, font).Width <= width) return text;
        var trimmed = text;
        while (trimmed.Length > 1 && gfx.MeasureString(trimmed + "…", font).Width > width)
            trimmed = trimmed[..^1];
        return trimmed + "…";
    }

    /// <summary>
    /// Wraps on spaces, and breaks mid-word when a single token is wider than the line —
    /// which a 24-character password with no spaces in it always is.
    /// </summary>
    private static IEnumerable<string> WrapHard(XGraphics gfx, string text, XFont font, double width)
    {
        var line = "";
        // Split on every kind of whitespace, not just spaces: driver messages arrive with
        // newlines in them, and a line break that is not a word break glues two sentences
        // together.
        foreach (var word in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = line.Length == 0 ? word : line + " " + word;
            if (gfx.MeasureString(candidate, font).Width <= width) { line = candidate; continue; }

            if (line.Length > 0) { yield return line; line = ""; }
            if (gfx.MeasureString(word, font).Width <= width) { line = word; continue; }

            var chunk = "";
            foreach (var character in word)
            {
                if (chunk.Length > 0 && gfx.MeasureString(chunk + character, font).Width > width)
                {
                    yield return chunk;
                    chunk = "";
                }
                chunk += character;
            }
            line = chunk;
        }
        if (line.Length > 0) yield return line;
    }
}
