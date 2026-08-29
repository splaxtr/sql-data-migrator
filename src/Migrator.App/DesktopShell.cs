using Photino.NET;

namespace Migrator.App;

/// <summary>
/// The window the application lives in.
///
/// Photino draws nothing itself: it opens a native OS window around the platform's own
/// WebView — WebView2 on Windows, WebKitGTK on Linux, WKWebView on macOS — and points it at
/// the address this process is already serving. That is the whole reason for choosing it
/// over a UI toolkit: <c>wwwroot/</c> stays one HTML page with no build step, and the same
/// page is what a hosted deployment would serve later. There is one interface, not two.
///
/// The component it borrows can be absent — a Linux box without WebKitGTK, a Windows
/// install without the WebView2 runtime, a machine with no display at all. None of those
/// are bugs, so they are reported as a reason the caller can show before falling back to a
/// browser, rather than thrown at the user as a stack trace.
/// </summary>
internal static class DesktopShell
{
    // Wide enough for the migration table to show its columns without scrolling sideways,
    // short enough to fit on a 768-pixel laptop screen.
    private const int InitialWidth = 1280;
    private const int InitialHeight = 860;
    private const int MinimumWidth = 900;
    private const int MinimumHeight = 600;

    // Matches <title> in wwwroot/index.html: the window caption and the page heading are
    // the same name to the person reading them.
    private const string Title = "SQL → SQL Taşıyıcı";

    /// <summary>
    /// Opens the window on the calling thread and blocks until the user closes it.
    ///
    /// It must be called on the process's main thread. macOS will not run a window loop
    /// anywhere else, and the GTK loop Linux uses expects the thread it was initialised on.
    /// </summary>
    /// <returns>
    /// <c>null</c> once a window has been opened and closed; otherwise a Turkish sentence
    /// saying why no window could be opened.
    /// </returns>
    public static string? Run(string address)
    {
        if (NoDisplay())
            return "Pencere açılamadı: bu oturuma bağlı bir ekran yok (DISPLAY tanımlı değil).";

        try
        {
            new PhotinoWindow()
                .SetTitle(Title)
                // Photino's native layer logs every window message at its default
                // verbosity, which turns the console into noise for anyone who started the
                // application from one.
                .SetLogVerbosity(0)
                .SetUseOsDefaultSize(false)
                .SetSize(InitialWidth, InitialHeight)
                .SetMinSize(MinimumWidth, MinimumHeight)
                .Center()
                .Load(address)
                .WaitForClose();
            return null;
        }
        catch (Exception ex) when (IsMissingWebView(ex))
        {
            return CannotOpenMessage(Innermost(ex));
        }
    }

    /// <summary>
    /// A Linux session with neither an X nor a Wayland display cannot show a window, and
    /// GTK aborts the process rather than failing politely when asked to try. Checking
    /// first turns a crash into a sentence. Windows and macOS always have a display when
    /// there is a user to see it.
    /// </summary>
    private static bool NoDisplay() =>
        OperatingSystem.IsLinux()
        && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))
        && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

    /// <summary>
    /// The shapes a missing WebView takes: the native library will not load, loads but is
    /// missing an export, or the type that P/Invokes it fails to initialise. Anything else
    /// is a real fault and is left to propagate — a window that failed for a reason this
    /// code does not understand must not be reported as "install a package".
    /// </summary>
    private static bool IsMissingWebView(Exception ex) => Innermost(ex) is
        DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException;

    private static Exception Innermost(Exception ex) =>
        ex is TypeInitializationException && ex.InnerException is { } inner ? Innermost(inner) : ex;

    private static string CannotOpenMessage(Exception ex)
    {
        var remedy = OperatingSystem.IsLinux()
            ? "Sistemde WebKitGTK yok. Kurmak için: sudo apt install libwebkit2gtk-4.1-0"
            : OperatingSystem.IsWindows()
                ? "Microsoft Edge WebView2 Runtime yüklü değil. "
                  + "https://developer.microsoft.com/microsoft-edge/webview2/ adresinden kurabilirsiniz."
                : "İşletim sisteminin WebView bileşeni yüklenemedi.";

        return $"Pencere açılamadı. {remedy} (Ayrıntı: {ex.Message})";
    }
}
