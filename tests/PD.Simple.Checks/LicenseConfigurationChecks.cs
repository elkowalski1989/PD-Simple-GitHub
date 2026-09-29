using CircuitHub.AllegroBridge.Engine.Live;
using PD.Simple;

internal static class LicenseConfigurationChecks
{
    internal static async Task<int> RunAsync()
    {
        int assertions = 0;
        foreach (string? bundled in new[] { null, string.Empty, " ", "bundled" })
        {
            Check(PdLicenseConfiguration.CreateProvider(bundled) is null,
                "Default or explicit bundled selection unexpectedly selected a supplied credential provider.");
        }
        IEngineLicenseKeyProvider? environment = PdLicenseConfiguration.CreateProvider("environment");
        Check(environment is not null, "Explicit environment selection did not configure the established Engine provider.");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            _ = await environment!.GetLicenseKeyAsync(cancellation.Token);
            throw new InvalidOperationException("Cancelled credential acquisition was not stopped.");
        }
        catch (OperationCanceledException)
        {
            assertions++;
        }
        string variable = EngineLicenseKeyProviders.DefaultEnvironmentVariableName;
        string? priorValue = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, null);
            try
            {
                _ = await environment!.GetLicenseKeyAsync(CancellationToken.None);
                throw new InvalidOperationException("The explicitly selected provider fell back when its key was missing.");
            }
            catch (InvalidOperationException error) when (error.Message.Contains("is not set", StringComparison.Ordinal))
            {
                Check(error.Message.Contains(variable, StringComparison.Ordinal),
                    "Missing supplied credentials did not identify the required variable.");
            }
            const string fixtureKey = "public-test-fixture-not-a-license";
            Environment.SetEnvironmentVariable(variable, fixtureKey);
            Check(await environment!.GetLicenseKeyAsync(CancellationToken.None) == fixtureKey,
                "The selected Engine provider did not read its configured source at acquisition time.");
            Check(PdLicenseConfiguration.CreateProvider(null) is null,
                "Merely setting the credential variable enabled supplied mode.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, priorValue);
        }
        foreach (string invalid in new[] { "ENVIRONMENT", "unrecognized-fixture-value", "environment\n" })
        {
            try
            {
                _ = PdLicenseConfiguration.CreateProvider(invalid);
                throw new InvalidOperationException("An unknown credential-provider selector was accepted.");
            }
            catch (ArgumentException error)
            {
                Check(error.Message.Contains(PdLicenseConfiguration.ProviderVariable, StringComparison.Ordinal) &&
                    !error.Message.Contains(invalid, StringComparison.Ordinal),
                    "Invalid configuration did not identify its selector safely without echoing the supplied value.");
            }
        }
        return assertions;

        void Check(bool condition, string message)
        {
            assertions++;
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
