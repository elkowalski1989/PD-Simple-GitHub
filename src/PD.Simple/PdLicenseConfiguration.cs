using CircuitHub.AllegroBridge.Engine.Live;

namespace PD.Simple;

/// <summary>PD chooses a credential source explicitly; Engine owns credential retrieval and admission.</summary>
internal static class PdLicenseConfiguration
{
    internal const string ProviderVariable = "PD_SIMPLE_LICENSE_PROVIDER";

    internal static IEngineLicenseKeyProvider? ReadProvider() =>
        CreateProvider(Environment.GetEnvironmentVariable(ProviderVariable));

    internal static IEngineLicenseKeyProvider? CreateProvider(string? selection)
    {
        if (string.IsNullOrWhiteSpace(selection) || selection == "bundled")
        {
            return null;
        }
        if (selection == "environment")
        {
            return EngineLicenseKeyProviders.FromEnvironment();
        }
        throw new ArgumentException(
            "PD_SIMPLE_LICENSE_PROVIDER must be absent, bundled, or environment. " +
            "For an unbundled package, select environment and configure the Engine license-key variable. " +
            "The supplied configuration value was not logged.");
    }
}
