using FluentAssertions;
using PhotoThing.Core;
using Xunit;

public class AppSettingsTests
{
    [Fact]
    public void Save_then_load_roundtrips_all_fields()
    {
        var path = Path.Combine(Path.GetTempPath(), "pt-settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var s = new AppSettings(
                ProjectId: "my-proj", BucketName: "my-bucket",
                SourceRoots: new[] { "/a", "/b" },
                GracePeriodDays: 15, ArchiveAfterDays: 90,
                ThumbnailMaxEdge: 256, MaxParallelUploads: 8, FfmpegPath: "/usr/bin/ffmpeg");
            s.Save(path);
            var loaded = AppSettings.Load(path);
            loaded.Should().BeEquivalentTo(s);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_partial_json_applies_defaults_for_optional_fields()
    {
        var path = Path.Combine(Path.GetTempPath(), "pt-settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            // Write minimal JSON with only required fields
            var minimalJson = """{"ProjectId":null,"BucketName":"my-bucket","SourceRoots":[]}""";
            File.WriteAllText(path, minimalJson);

            var loaded = AppSettings.Load(path);

            // Verify defaults are applied for optional fields
            loaded.ProjectId.Should().BeNull();
            loaded.BucketName.Should().Be("my-bucket");
            loaded.SourceRoots.Should().BeEmpty();
            loaded.GracePeriodDays.Should().Be(30);
            loaded.ArchiveAfterDays.Should().Be(180);
            loaded.ThumbnailMaxEdge.Should().Be(512);
            loaded.MaxParallelUploads.Should().Be(4);
            loaded.FfmpegPath.Should().Be("ffmpeg");
        }
        finally { File.Delete(path); }
    }
}
