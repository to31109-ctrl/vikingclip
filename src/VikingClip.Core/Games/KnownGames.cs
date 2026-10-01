using System.Reflection;
using System.Text.Json;
using VikingClip.Core.Logging;

namespace VikingClip.Core.Games;

public sealed class TitleRule
{
    public string Exe { get; set; } = "";
    public string TitleContains { get; set; } = "";
    public string Name { get; set; } = "";
}

/// <summary>The curated exe → game list shipped in known-games.json (friends: edit that file, it's just JSON).</summary>
public sealed class KnownGames
{
    public Dictionary<string, string> Games { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> NotGames { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<TitleRule> TitleRules { get; private set; } = new();

    private sealed class Dto
    {
        public Dictionary<string, string>? games { get; set; }
        public string[]? notGames { get; set; }
        public TitleRule[]? titleRules { get; set; }
    }

    public static KnownGames Load()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("known-games.json")
                               ?? throw new FileNotFoundException("known-games.json resource missing");
            return Parse(stream);
        }
        catch (Exception ex)
        {
            Log.Error("Could not load known-games.json", ex);
            return new KnownGames();
        }
    }

    public static KnownGames Parse(Stream json)
    {
        var dto = JsonSerializer.Deserialize<Dto>(json, new JsonSerializerOptions
        {
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            PropertyNameCaseInsensitive = true,
        }) ?? new Dto();
        return new KnownGames
        {
            Games = new Dictionary<string, string>(dto.games ?? new(), StringComparer.OrdinalIgnoreCase),
            NotGames = new HashSet<string>(dto.notGames ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase),
            TitleRules = (dto.titleRules ?? Array.Empty<TitleRule>()).ToList(),
        };
    }
}
