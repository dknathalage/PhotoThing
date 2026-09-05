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
}
