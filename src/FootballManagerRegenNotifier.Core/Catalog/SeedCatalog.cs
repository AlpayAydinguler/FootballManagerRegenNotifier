using System.Globalization;
using System.Reflection;
using FootballManagerRegenNotifier.Core.Model;

namespace FootballManagerRegenNotifier.Core.Catalog;

/// <summary>
/// The built-in youth intake dataset, embedded in the assembly.
/// </summary>
/// <remarks>
/// Embedded rather than loaded from disk so that the app always has something to
/// fall back on: a missing, deleted or unreadable spreadsheet degrades to "seed
/// a fresh one", never to a dead application. That also keeps the answer to a
/// data takedown request down to deleting one file.
/// </remarks>
public static class SeedCatalog
{
    private const string ResourceName =
        "FootballManagerRegenNotifier.Core.Catalog.countries.seed.psv";

    public static IReadOnlyList<CountryRule> Load()
    {
        using var stream = typeof(SeedCatalog).GetTypeInfo().Assembly
            .GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' is missing.");

        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    public static IReadOnlyList<CountryRule> Parse(string text)
    {
        var rules = new List<CountryRule>();

        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.Trim('\r', ' ', '\t', '﻿');
            if (line.Length == 0 || line[0] == '#') continue;
            if (line.StartsWith("Code|", StringComparison.Ordinal)) continue;

            var f = line.Split('|');
            if (f.Length < 10) continue;

            rules.Add(new CountryRule
            {
                Code = f[0].Trim(),
                Country = f[1].Trim(),
                Continent = f[2].Trim(),
                YouthRating = Int(f[3]),
                WindowStartMonth = Int(f[4]) ?? 1,
                WindowStartDay = Int(f[5]) ?? 1,
                WindowEndMonth = Int(f[6]) ?? 1,
                WindowEndDay = Int(f[7]) ?? 1,
                Confidence = Confidence(f[8]),
                EnabledByDefault = bool.TryParse(f[9].Trim(), out bool e) && e,
                Notes = f.Length > 10 ? f[10].Trim() : string.Empty,
            });
        }

        return rules;
    }

    internal static int? Int(string s) =>
        int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : null;

    internal static DataConfidence Confidence(string s) => s.Trim().ToUpperInvariant() switch
    {
        "HIGH" => DataConfidence.High,
        "LOW" => DataConfidence.Low,
        _ => DataConfidence.Medium,
    };
}
