using System.Text.Json;
using Xunit;

namespace LumePDF.Tests;

public class RecentsTests
{
    [Fact]
    public void A_history_from_older_versions_still_loads_and_keeps_no_reading_position()
    {
        // Documents always open at their top: nothing that could bring a page back is stored.
        const string json = """
            [ { "Path": "C:\\Docs\\a.pdf", "Page": 81, "Zoom": 1.5, "Fit": 0, "Opened": "2026-10-03T10:25:16+02:00" } ]
            """;
        var items = JsonSerializer.Deserialize(json, RecentJson.Default.ListRecentFile)!;
        var item = Assert.Single(items);
        Assert.Equal(@"C:\Docs\a.pdf", item.Path);
        Assert.Equal(1.5, item.Zoom);
        Assert.Null(typeof(RecentFile).GetProperty("Page"));
        Assert.DoesNotContain("Page", JsonSerializer.Serialize(items, RecentJson.Default.ListRecentFile));
    }
}
