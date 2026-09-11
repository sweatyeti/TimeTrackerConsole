using System.IO;
using Spectre.Console;

// Applies the active theme to the terminal's *default* colours (background, and
// foreground for light grounds) so the whole console - including the space the app
// never paints - matches the theme.
//
// Mechanism: OSC 11 / OSC 10 set the default background / foreground on entry and
// OSC 111 / OSC 110 reset them on exit (110 is the foreground reset, 111 the
// background one). Unlike per-segment markup backgrounds this also covers Spectre's
// prompt rows and table padding, which would otherwise show through as "holes".
// Terminals that don't understand the sequences ignore them, and they are only
// emitted when the console really renders ANSI to a terminal, so redirected output
// and non-ANSI hosts are never polluted with escape bytes.
internal static class ConsoleBackdrop
{
    private static bool _applied;
    private static bool _changedForeground;
    private static bool _changedBackground;
    private static bool _handlersRegistered;

    public static void Apply(ConsoleTheme theme)
    {
        if(theme.Background is null && theme.Foreground is null) return;
        if(!AnsiConsole.Console.Profile.Capabilities.Ansi) return;
        if(!AnsiConsole.Console.Profile.Out.IsTerminal) return;
        if(Console.IsOutputRedirected) return;

        RegisterRestoreHandlers();

        if(theme.Foreground is Color foreground)
        {
            Write($"\u001b]10;{foreground.ToMarkup()}\u0007");
            _changedForeground = true;
        }
        if(theme.Background is Color background)
        {
            Write($"\u001b]11;{background.ToMarkup()}\u0007");
            _changedBackground = true;
        }

        _applied = true;
    }

    // restores the user's own terminal colours; safe to call repeatedly
    public static void Restore()
    {
        if(!_applied) return;

        if(_changedForeground) Write("\u001b]110\u0007");
        if(_changedBackground) Write("\u001b]111\u0007");

        _applied = false;
        _changedForeground = false;
        _changedBackground = false;
    }

    // a hard kill (SIGKILL, closed terminal) cannot be intercepted; every path that
    // can be - normal exit, Ctrl+C, unhandled exception - puts the colours back
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
