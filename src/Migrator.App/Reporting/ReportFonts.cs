namespace Migrator.App.Reporting;

using System.Reflection;
using PdfSharp.Fonts;

/// <summary>
/// Supplies the one font the report is drawn with.
///
/// <para>The font travels inside the assembly rather than being looked up on the machine:
/// the published binary is a single self-contained file that has to produce the same PDF on
/// a Windows desktop, a Linux container and a Mac, and a system font lookup is exactly the
/// kind of thing that differs between those three. DejaVu Sans covers the Turkish letters
/// (ş ğ ı İ) that a Latin-1 core font would drop.</para>
/// </summary>
internal sealed class ReportFonts : IFontResolver
{
    public const string Family = "DejaVu Sans";
    private const string FaceName = "DejaVuSans";
    private const string ResourceName = "Migrator.App.Assets.DejaVuSans.ttf";

    // PDFsharp refuses a second resolver once the first has been used, so registration
    // happens exactly once however many reports are built, on however many threads.
    private static readonly Lazy<bool> Registration = new(() =>
    {
        GlobalFontSettings.FontResolver = new ReportFonts();
        return true;
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    public static void Register() => _ = Registration.Value;

    public byte[]? GetFont(string faceName)
    {
        using var stream = typeof(ReportFonts).GetTypeInfo().Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded font missing: {ResourceName}");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    // One face for every request: PDFsharp synthesizes bold and italic from it, which for a
    // table of names and passwords is indistinguishable from a real bold face and saves
    // shipping a second megabyte.
    public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic)
        => new FontResolverInfo(FaceName, isBold, isItalic);
}
