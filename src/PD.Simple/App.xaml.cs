using System.Windows;
using CircuitHub.AllegroBridge.Engine.Live;

namespace PD.Simple;

public partial class App : Application
{
    internal const string StartHiddenVariable = "PD_SIMPLE_START_HIDDEN";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        IEngineLicenseKeyProvider? licenseKeyProvider;
        IPdSimplePreferenceStore preferenceStore;
        try
        {
            licenseKeyProvider = PdLicenseConfiguration.ReadProvider();
            preferenceStore = JsonPdSimplePreferenceStore.CreateDefault();
        }
        catch (ArgumentException error)
        {
            MessageBox.Show(error.Message, "PD Simple configuration", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(2);
            return;
        }
        bool startHidden = string.Equals(
            Environment.GetEnvironmentVariable(StartHiddenVariable),
            "1",
            StringComparison.Ordinal);
        var window = new MainWindow(e.Args, preferenceStore, licenseKeyProvider);
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
