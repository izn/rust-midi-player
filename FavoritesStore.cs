using System.Text.Json;

namespace RustMidiPlayer;

internal static class FavoritesStore
{
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    public static List<string> Load()
    {
        try
        {
            var filePath = GetFilePath();
            if (!File.Exists(filePath)) return new List<string>();

            var savedPaths = JsonSerializer.Deserialize<string[]>(File.ReadAllText(filePath)) ?? Array.Empty<string>();
            return savedPaths
                .Select(Path.GetFullPath)
                .Distinct(PathComparer)
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    public static void Save(IEnumerable<string> paths)
    {
        var filePath = GetFilePath();
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        File.WriteAllText(filePath, JsonSerializer.Serialize(paths));
    }

    private static string GetFilePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RustMidiPlayer",
        "favorites.json");
}
