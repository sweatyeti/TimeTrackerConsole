using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Views;

// Phase 2 (T2.1, T2.3, T2.5, T2.6, T2.7) of the Terminal.Gui migration: the blocking
// dialogs the TUI uses to collect input. Every dialog returns "null = cancelled" (Esc or
// the Cancel button) so the callers can keep the same cancel semantics as the Spectre
// prompts (ESC cancels, nothing is mutated).
//
// All dialogs follow the Dialog button convention verified against 2.5.0: the LAST button
// added is the default one (Enter), and Dialog.Result is the 0-based index of the button
// pressed - or null when the dialog was dismissed without a button (Esc).
internal static class EntryDialogs
{
    private const int FieldWidth = 60;
    private const int ListWidth = 64;
    private const int MaxListRows = 12;

    // T2.1/T2.3: single text field. Returns the typed text, or null when cancelled.
    public static string? PromptForText(IApplication app, TuiPalette palette, string title, string message, string initialValue)
    {
        using Dialog dialog = new() { Title = title };
        dialog.SetScheme(palette.BaseScheme);

        Label prompt = new() { X = 1, Y = 1, Text = message };
        TextField field = new() { X = 1, Y = 2, Width = FieldWidth, Text = initialValue ?? string.Empty };

        dialog.Add(prompt);
        dialog.Add(field);
        dialog.AddButton(new Button { Title = "Cancel" });
        dialog.AddButton(new Button { Title = "OK" });

        // pre-select the current value so typing replaces it rather than appending to it,
        // which is what the Spectre prompts do with DefaultValue + ShowDefaultValue(false)
        field.SelectAll();
        field.SetFocus();

        app.Run(dialog);

        return dialog.Result == 1 ? field.Text : null;
    }

    // T2.3: the update-entry dialog. showLogged follows the Spectre rule exactly (only
    // completed, non-"none" entries have a logged state); when it is false no check box is
    // shown and the caller gets null back for logged, so the field is left untouched.
    public static EntryUpdateResult? PromptForEntryUpdate(IApplication app, TuiPalette palette, TimeEntry entry, bool showLogged)
    {
        using Dialog dialog = new() { Title = $"Update entry #{entry.Id}" };
        dialog.SetScheme(palette.BaseScheme);

        dialog.Add(new Label { X = 1, Y = 1, Text = $"Entry #{entry.Id} ({(entry.IsComplete ? "completed" : "in progress")})" });

        dialog.Add(new Label { X = 1, Y = 2, Text = "Task:" });
        TextField taskField = new() { X = 1, Y = 3, Width = FieldWidth, Text = entry.Task };
        dialog.Add(taskField);

        dialog.Add(new Label { X = 1, Y = 4, Text = "Description:" });
        TextField descriptionField = new() { X = 1, Y = 5, Width = FieldWidth, Text = entry.Description };
        dialog.Add(descriptionField);

        CheckBox? loggedField = null;
        if(showLogged)
        {
            loggedField = new CheckBox
            {
                X = 1,
                Y = 6,
                Text = "Logged",
                Value = entry.Logged ? CheckState.Checked : CheckState.UnChecked
            };
            dialog.Add(loggedField);
        }

        dialog.AddButton(new Button { Title = "Cancel" });
        dialog.AddButton(new Button { Title = "OK" });

        taskField.SelectAll();
        taskField.SetFocus();

        app.Run(dialog);

        if(dialog.Result != 1) return null;

        return new EntryUpdateResult(
            taskField.Text ?? string.Empty,
            descriptionField.Text ?? string.Empty,
            loggedField is null ? null : loggedField.Value == CheckState.Checked);
    }

    // T2.5/T2.6/T2.7: pick one row from a list of already-formatted labels. Returns the
    // selected index, or null when cancelled (or when there is nothing to pick).
    public static int? SelectFromList(IApplication app, TuiPalette palette, string title, string message, IReadOnlyList<string> items)
    {
        if(items.Count == 0) return null;

        using Dialog dialog = new() { Title = title };
        dialog.SetScheme(palette.BaseScheme);

        dialog.Add(new Label { X = 1, Y = 1, Text = message });

        ListView list = new()
        {
            X = 1,
            Y = 2,
            Width = ListWidth,
            Height = Math.Min(items.Count, MaxListRows) + 1,
            Source = new ListWrapper<string>(new ObservableCollection<string>(items))
        };
        list.SetScheme(palette.BaseScheme);

        // Enter on the list accepts the dialog, exactly like the Spectre SelectionPrompt.
        // Result has to be set explicitly here: the dialog only sets it when one of ITS
        // buttons is pressed, and RequestStop() alone would leave Result null (= cancelled).
        list.Accepting += (_, args) =>
        {
            args.Handled = true;
            dialog.Result = 1;
            dialog.RequestStop();
        };

        dialog.Add(list);
        dialog.AddButton(new Button { Title = "Cancel" });
        dialog.AddButton(new Button { Title = "Select" });

        list.SelectedItem = 0;
        list.SetFocus();

        app.Run(dialog);

        if(dialog.Result != 1) return null;

        int? selected = list.SelectedItem;
        return selected is null || selected < 0 || selected >= items.Count ? null : selected;
    }

    // T2.4/T2.6: yes/no confirmation, as MessageBox. The LAST button is the default one
    // (verified against 2.5.0), so the button order is chosen to reproduce the Spectre
    // AnsiConsole.Confirm(defaultValue: ...) calls of the same flows: the destructive delete
    // question defaults to cancel, the restore question defaults to yes.
    public static bool Confirm(IApplication app, string title, string message, string affirmative, string negative, bool defaultIsAffirmative)
    {
        string[] buttons = defaultIsAffirmative
            ? new[] { negative, affirmative }
            : new[] { affirmative, negative };

        int affirmativeIndex = defaultIsAffirmative ? 1 : 0;

        // null means Esc - neither button, so false for both question shapes
        return MessageBox.Query(app, title, message, buttons) == affirmativeIndex;
    }

    // a plain informational message (used where the Spectre path prints an error line and
    // waits for a key press, e.g. "No deleted entries.")
    public static void ShowMessage(IApplication app, string title, string message)
    {
        MessageBox.Query(app, title, message, new[] { "OK" });
    }

    internal readonly record struct EntryUpdateResult(string Task, string Description, bool? Logged);
}
