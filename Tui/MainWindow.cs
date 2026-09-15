using Terminal.Gui.Input;
using Terminal.Gui.Views;

// The main window, for one reason: Esc.
//
// Terminal.Gui binds Esc to Command.Quit as an APPLICATION-scoped key binding, and the docs are
// explicit that application-scoped bindings have the lowest priority - they are invoked only "if
// no View handles the key event" - which is exactly the documented mechanism built-in views use
// to override application keys (the docs cite Editor overriding Key.Tab for Command.NextTabStop).
//
// So the main window declares Command.Quit and reports it handled. Esc then stops here instead of
// reaching the app-level binding that calls RequestStop, and the window stays open. Dialogs are
// separate runnables with their own Esc handling, so they still cancel.
internal sealed class MainWindow : Window
{
    public MainWindow()
    {
        // Both halves are required. AddCommand alone is not enough: the application-scoped
        // Esc -> Command.Quit binding still fires, because declaring support for a command does
        // not give the view a binding for the KEY. Binding Esc to Command.Quit in the window's own
        // KeyBindings is what makes the view "handle the key event", and returning true from the
        // handler stops input processing there - so the app-level binding never runs.
        AddCommand(Command.Quit, () => true);
        KeyBindings.Add(Key.Esc, Command.Quit);
    }
}
