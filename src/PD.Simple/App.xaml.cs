using System.Windows;

namespace PD.Simple;

public partial class App : Application
{
    internal const string StartHiddenVariable = "PD_SIMPLE_START_HIDDEN";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        bool startHidden = string.Equals(
            Environment.GetEnvironmentVariable(StartHiddenVariable),
            "1",
            StringComparison.Ordinal);
        var window = new MainWindow(e.Args);
        MainWindow = window;
        if (startHidden)
        {
            window.ShowInBackground();
        }
        else
        {
            window.Show();
        }
    }
}
