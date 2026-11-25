using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using octo_fiesta.Models.Settings;
using octo_fiesta.Services.Transcoding;

namespace octo_fiesta.Tests;

/// <summary>
/// Runs the real ffmpeg. These tests pass trivially where ffmpeg is not installed; the CI
/// runners have it.
/// </summary>
public class FfmpegAudioTranscoderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "octo-fiesta-transcode-" + Guid.NewGuid().ToString("N"));

    public FfmpegAudioTranscoderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static FfmpegAudioTranscoder CreateTranscoder(bool enabled = true, string ffmpegPath = "ffmpeg") =>
        new(Options.Create(new SubsonicSettings { EnableTranscoding = enabled, FfmpegPath = ffmpegPath }),
            NullLogger<FfmpegAudioTranscoder>.Instance);

    private static readonly Lazy<bool> FfmpegInstalled = new(() =>
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("ffmpeg", "-version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            process!.WaitForExit(10_000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    });

    /// <summary>
    /// Five seconds of white noise as CD-quality FLAC. Noise barely compresses, so the file runs
    /// near 1411 kbps like real music; a pure tone would compress below any lossy cap and rightly
    /// be served untranscoded.
    /// </summary>
    private string CreateFlac()
    {
        var path = Path.Combine(_dir, "tone.flac");
        using var process = Process.Start(new ProcessStartInfo("ffmpeg")
        {
            ArgumentList = { "-hide_banner", "-loglevel", "error", "-nostdin", "-f", "lavfi",
                "-i", "anoisesrc=duration=5:sample_rate=44100:amplitude=0.5", "-ac", "2", "-c:a", "flac", path },
            RedirectStandardError = true,
        })!;
        process.WaitForExit(30_000);
        Assert.Equal(0, process.ExitCode);
        return path;
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    private static bool LooksLikeMp3(byte[] data) =>
        data.Length > 3 &&
        ((data[0] == 'I' && data[1] == 'D' && data[2] == '3') || (data[0] == 0xFF && (data[1] & 0xE0) == 0xE0));

    [Fact]
    public async Task CompleteFile_IsEncodedToMp3_AndLeftUntouched()
    {
        if (!FfmpegInstalled.Value) return;

        var flac = CreateFlac();
        var originalBytes = await File.ReadAllBytesAsync(flac);
        var source = new FileStream(flac, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);

        var result = await CreateTranscoder().TryTranscodeAsync(source, flac, new TranscodeRequest(null, 128), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("audio/mpeg", result.ContentType);
        byte[] encoded;
        await using (result.Stream)
        {
            encoded = await ReadAllAsync(result.Stream);
        }
        Assert.True(LooksLikeMp3(encoded));
        // 5 s at 128 kbps is ~80 KB, far below the FLAC
        Assert.InRange(encoded.Length, 40_000, 120_000);
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(flac));
    }

    [Fact]
    public async Task StreamStillDownloading_IsFedThroughStdin()
    {
        if (!FfmpegInstalled.Value) return;

        var flac = CreateFlac();
        // Not a FileStream: the transcoder cannot reopen it by path, as with a download in progress
        var source = new MemoryStream(await File.ReadAllBytesAsync(flac));

        var result = await CreateTranscoder().TryTranscodeAsync(source, flac, new TranscodeRequest("opus", 0), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("audio/ogg", result.ContentType);
        byte[] encoded;
        await using (result.Stream)
        {
            encoded = await ReadAllAsync(result.Stream);
        }
        Assert.Equal("OggS"u8.ToArray(), encoded[..4]);
    }

    [Fact]
    public async Task UnreadableInput_FallsBackToOriginal_AtItsStart()
    {
        if (!FfmpegInstalled.Value) return;

        var source = new MemoryStream(new byte[64 * 1024]);

        var result = await CreateTranscoder().TryTranscodeAsync(source, "broken.flac", new TranscodeRequest("mp3", 128), CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(0, source.Position);
    }

    [Fact]
    public async Task MissingFfmpeg_FallsBackToOriginal()
    {
        var source = new MemoryStream([1, 2, 3]);

        var result = await CreateTranscoder(ffmpegPath: Path.Combine(_dir, "no-such-ffmpeg"))
            .TryTranscodeAsync(source, "song.flac", new TranscodeRequest("mp3", 128), CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(0, source.Position);
    }

    [Fact]
    public async Task Disabled_FallsBackToOriginal()
    {
        var source = new MemoryStream([1, 2, 3]);

        var result = await CreateTranscoder(enabled: false)
            .TryTranscodeAsync(source, "song.flac", new TranscodeRequest("mp3", 128), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public void Arguments_SeekAndPipeAsExpected()
    {
        var transcoder = CreateTranscoder();
        var target = new TranscodeTarget(TranscodeProfile.M4a, 256);

        var fromPipe = transcoder.BuildStartInfo(null, target, 12.5).ArgumentList.ToList();
        Assert.DoesNotContain("-nostdin", fromPipe);
        Assert.Equal("12.5", fromPipe[fromPipe.IndexOf("-ss") + 1]);
        Assert.Equal("pipe:0", fromPipe[fromPipe.IndexOf("-i") + 1]);
        Assert.Equal("256k", fromPipe[fromPipe.IndexOf("-b:a") + 1]);
        Assert.Contains("frag_keyframe+empty_moov+default_base_moof", fromPipe);
        Assert.Equal("pipe:1", fromPipe[^1]);

        var fromFile = transcoder.BuildStartInfo("/music/a.flac", target, 0).ArgumentList.ToList();
        Assert.Contains("-nostdin", fromFile);
        Assert.DoesNotContain("-ss", fromFile);
        Assert.Equal("/music/a.flac", fromFile[fromFile.IndexOf("-i") + 1]);
    }
}
