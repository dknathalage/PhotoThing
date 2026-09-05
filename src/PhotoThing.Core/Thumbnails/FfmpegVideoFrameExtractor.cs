using System.Diagnostics;

namespace PhotoThing.Core.Thumbnails;

/// Extracts a poster frame by invoking ffmpeg. Requires ffmpeg on PATH.
public sealed class FfmpegVideoFrameExtractor : IVideoFrameExtractor
{
    private readonly string _ffmpegPath;
    public FfmpegVideoFrameExtractor(string ffmpegPath = "ffmpeg") => _ffmpegPath = ffmpegPath;

    public async Task<byte[]> ExtractPosterFrameAsync(string videoPath, CancellationToken ct = default)
    {
        // Grab one frame ~1s in; write PNG to stdout.
        var psi = new ProcessStartInfo(_ffmpegPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in new[] { "-ss", "1", "-i", videoPath, "-frames:v", "1", "-f", "image2pipe", "-vcodec", "png", "pipe:1" })
            psi.ArgumentList.Add(arg);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start ffmpeg.");
        using var outBuf = new MemoryStream();
        var copy = proc.StandardOutput.BaseStream.CopyToAsync(outBuf, ct);
        var err = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        await copy;

        if (proc.ExitCode != 0 || outBuf.Length == 0)
            throw new InvalidOperationException($"ffmpeg failed ({proc.ExitCode}): {await err}");
        return outBuf.ToArray();
    }
}
