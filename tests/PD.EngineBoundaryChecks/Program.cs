using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

string repositoryRoot = args.Length > 0
    ? Path.GetFullPath(args[0])
    : FindRepositoryRoot(AppContext.BaseDirectory);
string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ??
    throw new InvalidOperationException("Could not determine the active build configuration.");

var forbiddenOrdinaryDependencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "PcbSdk.Host",
    "CircuitHub.PcbSdk.Bridge.Contracts",
    "CircuitHub.PcbSdk.Bridge.Host",
    "CircuitHub.PcbSdk.Bridge.Licensing",
    "CircuitHub.PcbSdk.Bridge.Protocol",
    "CircuitHub.PcbSdk.Bridge",
    "CircuitHub.PcbSdk.Bridge.Transport",
    "CircuitHub.PcbSdk.Bridge.Windows",
};

CheckAssembly(
    "PD.PcbTools",
    Path.Combine(
        repositoryRoot,
        "src",
        "PD.PcbTools",
        "bin",
        configuration,
        "net10.0",
        "PD.PcbTools.dll"),
    forbiddenOrdinaryDependencies.Append("CircuitHub.PcbSdk.Wpf"));
CheckAssembly(
    "PD.Simple",
    Path.Combine(
        repositoryRoot,
        "src",
        "PD.Simple",
        "bin",
        configuration,
        "net10.0-windows",
        "PD.Simple.dll"),
    forbiddenOrdinaryDependencies);

Console.WriteLine(
    "PASS: emitted PD.PcbTools and PD.Simple assemblies have zero direct lower-SDK dependencies; WPF remains absent from reusable PD policy.");

static void CheckAssembly(
    string name,
    string path,
    IEnumerable<string> forbiddenDependencies)
{
    if (!File.Exists(path))
    {
        throw new FileNotFoundException(
            $"The compiled-boundary gate requires the emitted {name} assembly. " +
            "Its project reference should have built it first.",
            path);
    }

    using FileStream stream = File.OpenRead(path);
    using var peReader = new PEReader(stream);
    if (!peReader.HasMetadata)
    {
        throw new BadImageFormatException($"{path} has no managed metadata.");
    }

    MetadataReader reader = peReader.GetMetadataReader();
    string[] referencedAssemblies = reader.AssemblyReferences
        .Select(handle => reader.GetString(reader.GetAssemblyReference(handle).Name))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Order(StringComparer.OrdinalIgnoreCase)
        .ToArray();
    string[] forbidden = referencedAssemblies
        .Intersect(forbiddenDependencies, StringComparer.OrdinalIgnoreCase)
        .Order(StringComparer.OrdinalIgnoreCase)
        .ToArray();
    if (forbidden.Length > 0)
    {
        throw new InvalidOperationException(
            $"{name} directly references forbidden lower layers: {string.Join(", ", forbidden)}.");
    }
    if (!referencedAssemblies.Contains(
            "CircuitHub.PcbSdk",
            StringComparer.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException($"{name} does not directly reference Engine.");
    }
}

static string FindRepositoryRoot(string startDirectory)
{
    DirectoryInfo? directory = new(startDirectory);
    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.targets")) &&
            Directory.Exists(Path.Combine(directory.FullName, "src", "PD.Simple")))
        {
            return directory.FullName;
        }

        directory = directory.Parent;
    }

    throw new InvalidOperationException("Could not locate the PD Simple repository root.");
}
