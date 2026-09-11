using System.IO;
using Spectre.Console;

// Tints the terminal's *default* background so the whole console - including the
// space the app never paints - matches the active theme.
//
// Mechanism: OSC 11 (set default background colour) on entry and OSC 111 (reset
// default background colour) on exit. Note 110 is the *foreground* reset - the
// background reset is 111. Unlike per-segment markup backgrounds this
// also covers Spectre's prompt rows and table padding, which would otherwise show
// through as "holes". Terminals that don't understand the sequence ignore it, and
// it is only emitted when the console really renders ANSI to a terminal, so
// redirected output and non-ANSI hosts are never polluted with escape bytes.
internal static class ConsoleBackdrop
{
    private static bool _applied;
    private static bool _handlersRegistered;

    public static void Apply(ConsoleTheme theme)
    {
        if(theme.Background is not Color background) return;
        if(!AnsiConsole.Console.Profile.Capabilities.Ansi) return;
        if(!AnsiConsole.Console.Profile.Out.IsTerminal) return;
        if(Console.IsOutputRedirected) return;

        RegisterRestoreHandlers();
        Write($"\u001b]11;{background.ToMarkup()}\u0007");
        _applied = true;
    }

    // restores the user's own terminal background; safe to call repeatedly
    public static void Restore()
    {
        if(!_applied) return;

        Write("\u001b]111\u0007");
        _applied = false;
    }

    // a hard kill (SIGKILL, closed terminal) cannot be intercepted; every path that
    // can be - normal exit, Ctrl+C, unhandled exception - puts the background back
    private static void RegisterRestoreHandlers()
    {
        if(_handlersRegistered) return;
        _handlersRegistered = true;

        Console.CancelKeyPress += (_, _) => Restore();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Restore();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => Restore();
    }

    private static void Write(string sequence)
    {
        TextWriter writer = AnsiConsole.Console.Profile.Out.Writer;
        writer.Write(sequence);
        writer.Flush();
    }
}
