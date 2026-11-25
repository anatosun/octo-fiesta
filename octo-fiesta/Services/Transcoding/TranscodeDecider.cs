namespace octo_fiesta.Services.Transcoding;

/// <summary>
/// An output format ffmpeg can produce on a pipe: the encoder, the muxer and the
/// bit rate range that makes sense for it (kbps).
/// </summary>
public sealed record TranscodeProfile(
    string Format,
    string ContentType,
    string Encoder,
    string Muxer,
    int DefaultBitRate,
    int MinBitRate,
    int MaxBitRate,
    IReadOnlyList<string> ExtraArguments)
{
    public static readonly TranscodeProfile Mp3 =
        new("mp3", "audio/mpeg", "libmp3lame", "mp3", 192, 32, 320, []);

    public static readonly TranscodeProfile Opus =
        new("opus", "audio/ogg", "libopus", "ogg", 128, 16, 256, []);

    public static readonly TranscodeProfile Vorbis =
        new("ogg", "audio/ogg", "libvorbis", "ogg", 192, 64, 320, []);

    public static readonly TranscodeProfile Aac =
        new("aac", "audio/aac", "aac", "adts", 256, 32, 320, []);

    /// <summary>
    /// AAC in MP4. A pipe cannot be rewound to write the index at the start, so the MP4 is
    /// fragmented: the index comes first and the audio follows in self-contained chunks.
    /// </summary>
    public static readonly TranscodeProfile M4a =
        new("m4a", "audio/mp4", "aac", "mp4", 256, 32, 320,
            ["-movflags", "frag_keyframe+empty_moov+default_base_moof"]);

    /// <summary>
    /// The format sent when the client caps the bit rate without naming a format.
    /// MP3 is the one every Subsonic client can play.
    /// </summary>
    public static TranscodeProfile Default => Mp3;

    private static readonly Dictionary<string, TranscodeProfile> ByFormat = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mp3"] = Mp3,
        ["opus"] = Opus,
        ["ogg"] = Vorbis,
        ["oga"] = Vorbis,
        ["vorbis"] = Vorbis,
        ["aac"] = Aac,
        ["adts"] = Aac,
        ["m4a"] = M4a,
        ["mp4"] = M4a,
    };

    public static TranscodeProfile? Find(string? format) =>
        string.IsNullOrWhiteSpace(format) ? null : ByFormat.GetValueOrDefault(format.Trim());
}

/// <summary>
/// What is known about the file about to be served: its suffix, its bit rate (kbps, an
/// estimate when the file is still downloading) and whether it is lossless.
/// </summary>
public sealed record SourceAudio(string Suffix, int BitRate, bool Lossless);

/// <summary>
/// Encoding the client gets instead of the original file.
/// </summary>
public sealed record TranscodeTarget(TranscodeProfile Profile, int BitRate);

/// <summary>
/// Reads the Subsonic <c>stream</c> parameters the way Subsonic servers do: <c>format=raw</c>
/// asks for the original, <c>format</c> names the codec wanted, <c>maxBitRate</c> (kbps,
/// 0 = no limit) caps the bit rate.
/// </summary>
public static class TranscodeDecider
{
    /// <summary>
    /// Returns the encoding to send, or null to serve the file as it is.
    /// </summary>
    public static TranscodeTarget? Decide(SourceAudio source, string? format, int maxBitRate)
    {
        if (string.Equals(format?.Trim(), "raw", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var overCap = maxBitRate > 0 && source.BitRate > maxBitRate;
        var profile = TranscodeProfile.Find(format);

        if (profile == null)
        {
            // No format, or one we cannot encode (flac, wav...): only a bit rate cap forces a change
            if (!overCap)
            {
                return null;
            }
            profile = TranscodeProfile.Default;
        }
        else if (!source.Lossless && !overCap && IsSameFormat(profile, source.Suffix))
        {
            // Already what the client asked for, within its cap: re-encoding would only lose quality
            return null;
        }

        var bitRate = maxBitRate > 0 ? maxBitRate : profile.DefaultBitRate;
        if (!source.Lossless && source.BitRate > 0)
        {
            // Encoding a lossy file at a higher rate than its own costs data and adds nothing
            bitRate = Math.Min(bitRate, source.BitRate);
        }

        return new TranscodeTarget(profile, Math.Clamp(bitRate, profile.MinBitRate, profile.MaxBitRate));
    }

    private static bool IsSameFormat(TranscodeProfile profile, string suffix) =>
        TranscodeProfile.Find(suffix) == profile;
}
