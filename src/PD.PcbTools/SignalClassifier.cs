using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PD.PcbTools;

/// <summary>Retained, ordered classification policy from the reference checker, not SDK pair metadata.</summary>
public sealed class SignalClassifier
{
    private sealed record Rule(string Pattern, string Category, int Priority, string Risk);
    private readonly (Regex Pattern, string Category, string Risk)[] _rules;
    public static SignalClassifier Default { get; } = new();

    private SignalClassifier()
    {
        using var stream = typeof(SignalClassifier).Assembly.GetManifestResourceStream("PD.PcbTools.SignalCategories.json")
            ?? throw new InvalidOperationException("Signal classification data is missing.");
        var rules = JsonSerializer.Deserialize<Rule[]>(stream) ?? throw new InvalidDataException("Signal classification data is invalid.");
        _rules = rules.Select(rule => (new Regex(ConvertSkillPattern(rule.Pattern),
            RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100)),
            rule.Category, rule.Risk)).ToArray();
    }

    public (string Category, string Risk) Classify(string net)
    {
        string name = net.ToUpperInvariant();
        foreach (var rule in _rules)
        {
            if (rule.Pattern.IsMatch(name))
            {
                return (rule.Category, rule.Risk);
            }
        }
        return ("UNKNOWN", "CRITICAL");
    }

    // The bundled SKILL patterns use escaped groups/alternation and literal '?'.
    // This is a translator for that retained finite rule set, not a general SKILL regex engine.
    private static string ConvertSkillPattern(string source)
    {
        var result = new StringBuilder();
        bool inClass = false;
        for (int index = 0; index < source.Length; index++)
        {
            char value = source[index];
            if (value == '\\' && index + 1 < source.Length)
            {
                char escaped = source[++index];
                if (!inClass && escaped is '(' or ')' or '|')
                {
                    result.Append(escaped);
                }
                else
                {
                    result.Append('\\').Append(escaped);
                }
            }
            else
            {
                if (value == '[')
                {
                    inClass = true;
                }
                if (!inClass && value is '(' or ')' or '|' or '?' or '{' or '}')
                {
                    result.Append('\\');
                }
                result.Append(value);
                if (value == ']')
                {
                    inClass = false;
                }
            }
        }
        return result.ToString();
    }

    public static bool IsIgnoredAggressor(string net)
    {
        string name = net.ToUpperInvariant();
        if (name is "GND" or "NC" || name.StartsWith('+'))
        {
            return true;
        }
        string[] prefixes = ["DGND", "AGND", "PGND", "PD_", "PU_", "NC_", "NU_", "VDD", "VCC", "VSS", "PWR", "POWER"];
        if (prefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
        {
            return true;
        }
        return Regex.IsMatch(name, @"(^GND[_0-9]|_GND($|[_0-9])|/GND|^NC[0-9])", RegexOptions.CultureInvariant);
    }
}
