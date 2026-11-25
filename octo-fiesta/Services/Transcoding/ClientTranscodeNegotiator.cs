using System.Globalization;
using System.Text.Json;

namespace octo_fiesta.Services.Transcoding;

/// <summary>
/// The capabilities a client sends with OpenSubsonic <c>getTranscodeDecision</c> (the ClientInfo
/// body). Bit rates are in bits per second, as in the spec.
/// </summary>
public sealed class ClientInfo
{
    public string? Name { get; set; }
    public string? Platform { get; set; }
    public int MaxAudioBitrate { get; set; }
    public int MaxTranscodingAudioBitrate { get; set; }
    public List<DirectPlayProfile> DirectPlayProfiles { get; set; } = [];
    public List<TranscodingProfile> TranscodingProfiles { get; set; } = [];
    public List<CodecProfile> CodecProfiles { get; set; } = [];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>Parses a ClientInfo body, or returns null when it is missing or malformed.</summary>
    public static ClientInfo? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<ClientInfo>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public sealed class DirectPlayProfile
{
    public List<string> Containers { get; set; } = [];
    public List<string> AudioCodecs { get; set; } = [];
    public List<string> Protocols { get; set; } = [];
    public int MaxAudioChannels { get; set; }
}

public sealed class TranscodingProfile
{
    public string? Container { get; set; }
    public string? AudioCodec { get; set; }
    public string? Protocol { get; set; }
    public int MaxAudioChannels { get; set; }
}

public sealed class CodecProfile
{
    public string? Type { get; set; }
    public string? Name { get; set; }
    public List<CodecLimitation> Limitations { get; set; } = [];
}

public sealed class CodecLimitation
{
    public string? Name { get; set; }
    public string? Comparison { get; set; }
    public List<string> Values { get; set; } = [];
    public bool Required { get; set; }
}

/// <summary>
/// A stream as the transcodeDecision response describes it. Bit rate in kbps here, converted
/// to bps when written out.
/// </summary>
public sealed record StreamDetails(string Container, string Codec, int BitRate, int Channels = 2);

public sealed record NegotiatedDecision(
    bool CanDirectPlay,
    bool CanTranscode,
    IReadOnlyList<string> TranscodeReasons,
    string ErrorReason,
    StreamDetails SourceStream,
    StreamDetails? TranscodeStream,
    string TranscodeParams);

/// <summary>
/// Makes the OpenSubsonic transcode decision for tracks octo-fiesta serves itself (ext- ids),
/// following Navidrome's order: the global bit rate cap, then the direct play profiles, then the
/// transcoding profiles in the client's order of preference. Only the http protocol is offered.
/// </summary>
public static class ClientTranscodeNegotiator
{
    private const string Http = "http";

    public static NegotiatedDecision Decide(StreamDetails source, bool sourceLossless, ClientInfo client)
    {
        var reasons = new List<string>();
        var maxAudio = ToKbps(client.MaxAudioBitrate);

        if (maxAudio > 0 && source.BitRate > maxAudio)
        {
            reasons.Add("audio bitrate not supported");
        }
        else
        {
            foreach (var profile in client.DirectPlayProfiles)
            {
                var reason = CheckDirectPlay(source, profile, client);
                if (reason == null)
                {
                    return new NegotiatedDecision(true, false, [], "", source, null, TranscodeToken.Raw);
                }
                reasons.Add(reason);
            }
        }

        foreach (var profile in client.TranscodingProfiles)
        {
            if (TryTranscode(source, sourceLossless, profile, client) is { } result)
            {
                return new NegotiatedDecision(false, true, reasons, "", source, result.Details,
                    TranscodeToken.Create(result.Target));
            }
        }

        return new NegotiatedDecision(false, false, reasons, "no compatible playback profile found", source, null, "");
    }

    private static string? CheckDirectPlay(StreamDetails source, DirectPlayProfile profile, ClientInfo client)
    {
        if (profile.Protocols.Count > 0 && !Contains(profile.Protocols, Http))
        {
            return "protocol not supported";
        }
        if (profile.Containers.Count > 0 && !profile.Containers.Any(c => SameContainer(c, source.Container)))
        {
            return $"container '{source.Container}' not supported";
        }
        if (profile.AudioCodecs.Count > 0 && !Contains(profile.AudioCodecs, source.Codec))
        {
            return $"audio codec '{source.Codec}' not supported";
        }
        if (profile.MaxAudioChannels > 0 && source.Channels > profile.MaxAudioChannels)
        {
            return $"audio channels {source.Channels} not supported";
        }
        foreach (var codecProfile in CodecProfilesFor(client, source.Codec))
        {
            foreach (var limitation in codecProfile.Limitations)
            {
                if (Violates(source, limitation))
                {
                    return $"{limitation.Name} not supported";
                }
            }
        }
        return null;
    }

    private static (TranscodeTarget Target, StreamDetails Details)? TryTranscode(
        StreamDetails source, bool sourceLossless, TranscodingProfile profile, ClientInfo client)
    {
        if (!string.IsNullOrEmpty(profile.Protocol) && !string.Equals(profile.Protocol, Http, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var output = ResolveProfile(profile.Container, profile.AudioCodec);
        if (output == null)
        {
            // A lossless or unknown target: nothing ffmpeg here is set up to produce
            return null;
        }

        var maxAudio = ToKbps(client.MaxAudioBitrate);
        int bitRate;
        if (sourceLossless)
        {
            var maxTranscoding = ToKbps(client.MaxTranscodingAudioBitrate);
            bitRate = maxTranscoding > 0 ? maxTranscoding : maxAudio > 0 ? maxAudio : output.DefaultBitRate;
        }
        else
        {
            // Lossy to lossy: keep the source rate, a higher one only costs data
            bitRate = source.BitRate > 0 ? source.BitRate : output.DefaultBitRate;
        }
        if (maxAudio > 0)
        {
            bitRate = Math.Min(bitRate, maxAudio);
        }

        var codec = CodecName(output);
        foreach (var codecProfile in CodecProfilesFor(client, codec))
        {
            foreach (var limitation in codecProfile.Limitations)
            {
                if (IsName(limitation, "audioBitrate") &&
                    IsComparison(limitation, "LessThanEqual") &&
                    TryFirstInt(limitation, out var limitBps) && limitBps > 0)
                {
                    bitRate = Math.Min(bitRate, ToKbps(limitBps));
                }
            }
        }

        bitRate = Math.Clamp(bitRate, output.MinBitRate, output.MaxBitRate);
        var channels = profile.MaxAudioChannels > 0 ? Math.Min(source.Channels, profile.MaxAudioChannels) : source.Channels;
        var container = string.IsNullOrEmpty(profile.Container) ? output.Format : profile.Container.ToLowerInvariant();

        return (new TranscodeTarget(output, bitRate), new StreamDetails(container, codec, bitRate, channels));
    }

    /// <summary>
    /// Maps a transcoding profile to an encoder: the codec decides when given (mp4 + aac,
    /// ogg + opus), the container otherwise.
    /// </summary>
    internal static TranscodeProfile? ResolveProfile(string? container, string? audioCodec)
    {
        var codec = audioCodec?.Trim().ToLowerInvariant();
        var box = container?.Trim().ToLowerInvariant();
        return codec switch
        {
            "mp3" => TranscodeProfile.Mp3,
            "opus" => TranscodeProfile.Opus,
            "vorbis" => TranscodeProfile.Vorbis,
            "aac" => box is "mp4" or "m4a" ? TranscodeProfile.M4a : TranscodeProfile.Aac,
            null or "" => box switch
            {
                "mp3" => TranscodeProfile.Mp3,
                "opus" => TranscodeProfile.Opus,
                "ogg" or "oga" => TranscodeProfile.Vorbis,
                "aac" => TranscodeProfile.Aac,
                "mp4" or "m4a" => TranscodeProfile.M4a,
                _ => null
            },
            _ => null
        };
    }

    private static string CodecName(TranscodeProfile profile) => profile.Encoder switch
    {
        "libmp3lame" => "mp3",
        "libopus" => "opus",
        "libvorbis" => "vorbis",
        _ => "aac"
    };

    private static IEnumerable<CodecProfile> CodecProfilesFor(ClientInfo client, string codec) =>
        client.CodecProfiles.Where(p =>
            (string.IsNullOrEmpty(p.Type) || string.Equals(p.Type, "AudioCodec", StringComparison.OrdinalIgnoreCase)) &&
            string.Equals(p.Name, codec, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Only the bit rate and channel count of a track not downloaded yet are known; limitations
    /// on anything else (sample rate, bit depth, profile) cannot be checked and are let through.
    /// </summary>
    private static bool Violates(StreamDetails source, CodecLimitation limitation)
    {
        int value;
        if (IsName(limitation, "audioBitrate"))
        {
            value = source.BitRate * 1000;
        }
        else if (IsName(limitation, "audioChannels"))
        {
            value = source.Channels;
        }
        else
        {
            return false;
        }

        var values = limitation.Values
            .Select(v => int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? (int?)n : null)
            .Where(n => n.HasValue)
            .Select(n => n!.Value)
            .ToList();
        if (values.Count == 0)
        {
            return false;
        }

        return limitation.Comparison?.ToLowerInvariant() switch
        {
            "lessthanequal" => value > values[0],
            "greaterthanequal" => value < values[0],
            "equals" => !values.Contains(value),
            "notequals" => values.Contains(value),
            _ => false
        };
    }

    private static bool IsName(CodecLimitation limitation, string name) =>
        string.Equals(limitation.Name, name, StringComparison.OrdinalIgnoreCase);

    private static bool IsComparison(CodecLimitation limitation, string comparison) =>
        string.Equals(limitation.Comparison, comparison, StringComparison.OrdinalIgnoreCase);

    private static bool TryFirstInt(CodecLimitation limitation, out int value)
    {
        value = 0;
        return limitation.Values.Count > 0 &&
               int.TryParse(limitation.Values[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool Contains(IEnumerable<string> values, string value) =>
        values.Any(v => string.Equals(v, value, StringComparison.OrdinalIgnoreCase));

    /// <summary>m4a and mp4 name the same container, as do ogg and oga.</summary>
    private static bool SameContainer(string a, string b) => NormalizeContainer(a) == NormalizeContainer(b);

    private static string NormalizeContainer(string container) => container.Trim().ToLowerInvariant() switch
    {
        "m4a" or "m4b" => "mp4",
        "oga" => "ogg",
        var other => other
    };

    private static int ToKbps(int bps) => bps <= 0 ? 0 : (bps + 500) / 1000;
}

/// <summary>
/// The opaque <c>transcodeParams</c> handed to the client and sent back with
/// <c>getTranscodeStream</c>: "of1-raw" for the original, "of1-{format}-{kbps}" otherwise. It
/// carries nothing the client could not ask for directly through <c>stream</c>.
/// </summary>
public static class TranscodeToken
{
    private const string Prefix = "of1-";
    public const string Raw = Prefix + "raw";

    public static string Create(TranscodeTarget target) =>
        $"{Prefix}{target.Profile.Format}-{target.BitRate.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// Reads a token back into a stream request, or returns null for one octo-fiesta did not issue.
    /// </summary>
    public static TranscodeRequest? TryParse(string? token, double timeOffsetSeconds = 0)
    {
        if (string.IsNullOrEmpty(token) || !token.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }
        if (token == Raw)
        {
            return new TranscodeRequest("raw", 0, timeOffsetSeconds);
        }

        var parts = token[Prefix.Length..].Split('-');
        if (parts.Length == 2 &&
            TranscodeProfile.Find(parts[0]) is { } profile && profile.Format == parts[0] &&
            int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var bitRate) && bitRate > 0)
        {
            return new TranscodeRequest(profile.Format, bitRate, timeOffsetSeconds);
        }
        return null;
    }
}
