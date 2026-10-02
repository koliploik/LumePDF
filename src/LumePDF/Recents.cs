using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LumePDF;

public sealed partial class RecentFile
{
    public string Path { get; set; } = "";
    public int Page { get; set; }
    public double Zoom { get; set; } = 1;
    public int Fit { get; set; }
    public DateTime Opened { get; set; }

    [JsonIgnore] public string Name => System.IO.Path.GetFileName(Path);
    [JsonIgnore] public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
}

[JsonSerializable(typeof(List<RecentFile>))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class RecentJson : JsonSerializerContext;

/// <summary>Recently opened files with their last page and zoom, in %LOCALAPPDATA%\LumePDF.</summary>
public static class Recents
{
    const int Max = 20;
    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LumePDF", "recent.json");

    static List<RecentFile>? _items;

    static List<RecentFile> Items => _items ??= Load();

    static List<RecentFile> Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize(File.ReadAllText(FilePath), RecentJson.Default.ListRecentFile) ?? [];
        }
        catch
        {
            // A corrupt history is not worth bothering the user about.
        }
        return [];
    }

    public static List<RecentFile> Existing() => Items.Where(r => File.Exists(r.Path)).ToList();

    public static RecentFile? Get(string path) =>
        Items.FirstOrDefault(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));

    public static void Update(string path, int page, double zoom, int fit)
    {
        var item = Get(path) ?? new RecentFile { Path = path };
        Items.Remove(item);
        item.Page = page;
        item.Zoom = zoom;
        item.Fit = fit;
        item.Opened = DateTime.Now;
        Items.Insert(0, item);
        if (Items.Count > Max)
            Items.RemoveRange(Max, Items.Count - Max);
        Save();
    }

    public static void Remove(string path)
    {
        if (Get(path) is { } item && Items.Remove(item))
            Save();
    }

    static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Items, RecentJson.Default.ListRecentFile));
        }
        catch
        {
            // Read-only profile or full disk: history is optional.
        }
    }
}
