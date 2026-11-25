using octo_fiesta.Services.Common;

namespace octo_fiesta.Services.Transcoding;

/// <summary>
/// Works out the bit rate of the stream about to be served, without decoding it.
/// </summary>
public static class SourceAudioProbe
{
    /// <summary>Above this rate (kbps) a file is lossless whatever its container says (FLAC or ALAC in MP4).</summary>
    private const int LosslessThreshold = 600;

    public static SourceAudio Probe(Stream stream, string filePath)
    {
        var suffix = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();
        var bitRate = MeasureBitRate(stream) ?? GuessBitRate(suffix);
        var lossless = suffix is "flac" or "wav" or "aiff" or "alac" || bitRate >= LosslessThreshold;
        return new SourceAudio(suffix, bitRate, lossless);
    }

    private static int? MeasureBitRate(Stream stream)
    {
        switch (stream)
        {
            // Still downloading: the announced size over the announced duration
            case ProgressiveDownloadStream { Download.DurationSeconds: > 0 } progressive:
                return ToKbps(progressive.Download.Length, progressive.Download.DurationSeconds!.Value);

            case FileStream file:
                try
                {
                    using var tagFile = TagLib.File.Create(file.Name);
                    var properties = tagFile.Properties;
                    if (properties == null)
                    {
                        return null;
                    }
                    if (properties.AudioBitrate > 0)
                    {
                        return properties.AudioBitrate;
                    }
                    return properties.Duration.TotalSeconds > 0
                        ? ToKbps(file.Length, properties.Duration.TotalSeconds)
                        : null;
                }
                catch (Exception)
                {
                    // Unreadable or unsupported container: fall back to what the suffix says
                    return null;
                }

            default:
                return null;
        }
    }

    private static int ToKbps(long bytes, double seconds) => (int)Math.Round(bytes * 8 / seconds / 1000);

    /// <summary>
    /// The rate a provider usually delivers for this suffix, erring high so a cap is enforced
    /// rather than ignored.
    /// </summary>
    private static int GuessBitRate(string suffix) => suffix switch
    {
        "flac" or "wav" or "aiff" or "alac" => 1411,
        _ => 320
    };
}
