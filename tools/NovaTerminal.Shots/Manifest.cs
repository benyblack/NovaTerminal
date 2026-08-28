using System.Text.Json;

namespace NovaTerminal.Shots;

public sealed record ShotAsset(
    string Name,
    int Tier,
    string File,
    int Width,
    int Height,
    string Scenario,
    string Commit,
    string Os,
    string TimestampUtc);

public static class Manifest
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static void Write(string path, IReadOnlyList<ShotAsset> assets)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(assets, Options));
    }

    public static IReadOnlyList<ShotAsset> Read(string path) =>
        JsonSerializer.Deserialize<List<ShotAsset>>(File.ReadAllText(path)) ?? [];
}
