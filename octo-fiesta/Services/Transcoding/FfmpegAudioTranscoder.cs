using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using octo_fiesta.Models.Settings;

namespace octo_fiesta.Services.Transcoding;

/// <summary>
/// What the client asked for in its <c>stream</c> request.
/// </summary>
/// <param name="Format">The <c>format</c> parameter (mp3, opus, raw...), null when absent</param>
/// <param name="MaxBitRate">The <c>maxBitRate</c> parameter in kbps, 0 for no limit</param>
/// <param name="TimeOffsetSeconds">The <c>timeOffset</c> parameter: where in the track to start</param>
public sealed record TranscodeRequest(string? Format, int MaxBitRate, double TimeOffsetSeconds = 0);

/// <summary>
/// An encoded stream ready to send. Disposing it stops the encoder and closes the source.
/// </summary>
public sealed record TranscodedStream(Stream Stream, TranscodeTarget Target)
{
    public string ContentType => Target.Profile.ContentType;
}

public interface IAudioTranscoder
{
    /// <summary>
    /// Encodes <paramref name="source"/> as the client asked, or returns null when the original
    /// should be served: nothing to change, transcoding disabled, or ffmpeg unavailable/failing.
    /// On a non-null result the transcoder owns <paramref name="source"/>. On null the source is
    /// left at its start, ready to be served as is.
    /// </summary>
    Task<TranscodedStream?> TryTranscodeAsync(Stream source, string filePath, TranscodeRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Transcodes with an ffmpeg process: the source goes in by path when it is a complete file,
/// through stdin when it is still downloading, and the encoded audio comes out on stdout.
/// The file kept in the library is never touched.
/// </summary>
public sealed class FfmpegAudioTranscoder : IAudioTranscoder
{
    private const int MaxStderrChars = 4000;

    private readonly SubsonicSettings _settings;
    private readonly ILogger<FfmpegAudioTranscoder> _logger;
    private readonly Lazy<Task<bool>> _available;

    public FfmpegAudioTranscoder(IOptions<SubsonicSettings> settings, ILogger<FfmpegAudioTranscoder> logger)
    {
        _settings = settings.Value;
        _logger = logger;
        _available = new Lazy<Task<bool>>(CheckAvailabilityAsync);
    }

    public async Task<TranscodedStream?> TryTranscodeAsync(Stream source, string filePath, TranscodeRequest request, CancellationToken cancellationToken)
    {
        if (!_settings.EnableTranscoding)
        {
            return null;
        }

        var audio = SourceAudioProbe.Probe(source, filePath);
        var target = TranscodeDecider.Decide(audio, request.Format, request.MaxBitRate);
        if (target == null || !await _available.Value)
        {
            return null;
        }

        // A complete file is read by path, which lets ffmpeg seek for timeOffset and read
        // MP4 files whose index sits at the end. A download in progress is fed through stdin.
        var inputPath = source is FileStream file && File.Exists(file.Name) ? file.Name : null;
        var startInfo = BuildStartInfo(inputPath, target, request.TimeOffsetSeconds);

        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new InvalidOperationException("ffmpeg did not start");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Could not start ffmpeg, serving {File} untranscoded", Path.GetFileName(filePath));
            return null;
        }

        var stderr = ReadStderrAsync(process);
        var feedCancellation = new CancellationTokenSource();
        var feed = inputPath == null ? FeedStdinAsync(source, process, feedCancellation.Token) : Task.CompletedTask;

        // Wait for the first encoded bytes, so a file ffmpeg cannot read is still served untranscoded
        var head = new byte[16 * 1024];
        int headLength;
        try
        {
            headLength = await process.StandardOutput.BaseStream.ReadAsync(head, cancellationToken);
        }
        catch
        {
            await StopAsync(process, feedCancellation, feed);
            throw;
        }

        if (headLength == 0)
        {
            await StopAsync(process, feedCancellation, feed);
            _logger.LogWarning("ffmpeg produced no audio for {File}, serving it untranscoded: {Error}",
                Path.GetFileName(filePath), await stderr);
            if (source.CanSeek)
            {
                source.Position = 0;
            }
            return null;
        }

        _logger.LogInformation("Transcoding {File} ({Suffix}, ~{SourceRate} kbps) to {Format} at {BitRate} kbps",
            Path.GetFileName(filePath), audio.Suffix, audio.BitRate, target.Profile.Format, target.BitRate);

        var output = new FfmpegOutputStream(process, head.AsMemory(0, headLength), source, feed, feedCancellation, stderr, _logger, filePath);
        return new TranscodedStream(output, target);
    }

    internal ProcessStartInfo BuildStartInfo(string? inputPath, TranscodeTarget target, double timeOffsetSeconds)
    {
        var startInfo = new ProcessStartInfo(_settings.FfmpegPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = inputPath == null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        var args = startInfo.ArgumentList;
        args.Add("-hide_banner");
        args.Add("-loglevel");
        args.Add("error");
        if (inputPath != null)
        {
            // stdin carries the audio otherwise, and must not be read as keyboard commands
            args.Add("-nostdin");
        }
        if (timeOffsetSeconds > 0)
        {
            args.Add("-ss");
            args.Add(timeOffsetSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        }
        args.Add("-i");
        args.Add(inputPath ?? "pipe:0");
        // Audio only: embedded cover art would otherwise be encoded as a video stream
        args.Add("-map");
        args.Add("0:a:0");
        args.Add("-vn");
        args.Add("-c:a");
        args.Add(target.Profile.Encoder);
        args.Add("-b:a");
        args.Add($"{target.BitRate}k");
        foreach (var extra in target.Profile.ExtraArguments)
        {
            args.Add(extra);
        }
        args.Add("-f");
        args.Add(target.Profile.Muxer);
        args.Add("pipe:1");

        return startInfo;
    }

    private async Task<bool> CheckAvailabilityAsync()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(_settings.FfmpegPath, "-version")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process == null)
            {
                throw new InvalidOperationException("ffmpeg did not start");
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var version = await process.StandardOutput.ReadLineAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            if (process.ExitCode == 0)
            {
                _logger.LogInformation("Transcoding enabled with {Version}", version);
                return true;
            }
            _logger.LogWarning("'{Path} -version' exited with code {Code}; transcoding disabled, original files will be served",
                _settings.FfmpegPath, process.ExitCode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("ffmpeg not found at '{Path}' ({Reason}); transcoding disabled, original files will be served. " +
                               "Install ffmpeg or set FFMPEG_PATH.", _settings.FfmpegPath, ex.Message);
        }
        return false;
    }

    /// <summary>
    /// Stops ffmpeg and waits for the feeder to let go of the source, so the source can be reused.
    /// </summary>
    private static async Task StopAsync(Process process, CancellationTokenSource feedCancellation, Task feed)
    {
        feedCancellation.Cancel();
        Kill(process);
        await feed;
        feedCancellation.Dispose();
        process.Dispose();
    }

    private static async Task FeedStdinAsync(Stream source, Process process, CancellationToken cancellationToken)
    {
        try
        {
            await using var stdin = process.StandardInput.BaseStream;
            await source.CopyToAsync(stdin, cancellationToken);
        }
        catch (Exception)
        {
            // ffmpeg exited or was stopped (client gone), or the download failed: either way
            // stdin closes and ffmpeg ends the stream, which is reported where it is read
        }
    }

    private static async Task<string> ReadStderrAsync(Process process)
    {
        var text = new StringBuilder();
        var buffer = new char[1024];
        try
        {
            int read;
            while ((read = await process.StandardError.ReadAsync(buffer)) > 0)
            {
                if (text.Length < MaxStderrChars)
                {
                    text.Append(buffer, 0, Math.Min(read, MaxStderrChars - text.Length));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
        return text.ToString().Trim();
    }

    internal static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // Already gone
        }
    }
}

/// <summary>
/// ffmpeg's stdout, starting with the bytes already read to check it works. Not seekable: a
/// client that seeks in a transcoded track asks again with a timeOffset.
/// </summary>
internal sealed class FfmpegOutputStream : Stream
{
    private readonly Process _process;
    private readonly Stream _stdout;
    private readonly Stream _source;
    private readonly Task _feed;
    private readonly CancellationTokenSource _feedCancellation;
    private readonly Task<string> _stderr;
    private readonly ILogger _logger;
    private readonly string _filePath;
    private ReadOnlyMemory<byte> _head;
    private bool _ended;
    private int _disposed;

    public FfmpegOutputStream(Process process, ReadOnlyMemory<byte> head, Stream source, Task feed,
        CancellationTokenSource feedCancellation, Task<string> stderr, ILogger logger, string filePath)
    {
        _process = process;
        _stdout = process.StandardOutput.BaseStream;
        _head = head;
        _source = source;
        _feed = feed;
        _feedCancellation = feedCancellation;
        _stderr = stderr;
        _logger = logger;
        _filePath = filePath;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!_head.IsEmpty)
        {
            var count = Math.Min(buffer.Length, _head.Length);
            _head[..count].CopyTo(buffer);
            _head = _head[count..];
            return count;
        }

        var read = await _stdout.ReadAsync(buffer, cancellationToken);
        if (read == 0 && !_ended)
        {
            _ended = true;
            await ReportExitAsync();
        }
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    private async Task ReportExitAsync()
    {
        try
        {
            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            if (_process.ExitCode != 0)
            {
                _logger.LogWarning("ffmpeg exited with code {Code} while transcoding {File}: {Error}",
                    _process.ExitCode, Path.GetFileName(_filePath), await _stderr);
            }
        }
        catch (TimeoutException)
        {
        }
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() { }

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            // The client is done (or gone): stop encoding, then release the source
            _feedCancellation.Cancel();
            FfmpegAudioTranscoder.Kill(_process);
            _process.Dispose();
            _feed.ContinueWith(_ =>
            {
                _source.Dispose();
                _feedCancellation.Dispose();
            }, TaskScheduler.Default);
        }
        base.Dispose(disposing);
    }
}
