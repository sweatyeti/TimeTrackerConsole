using System;
using Terminal.Gui.App;
using Terminal.Gui.Views;

internal sealed class TuiSessionWindow
{
    public void Run()
    {
        // Q4: full-screen is the default AppModel (verified) - no assignment needed
        using IApplication app = Application.Create();
        app.Init();
        using Window window = new() { Title = "TimeTracker" };
        app.Run(window);
    }
}
