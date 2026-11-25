using octo_fiesta.Services.Transcoding;

namespace octo_fiesta.Tests;

public class ClientTranscodeNegotiatorTests
{
    private static readonly StreamDetails Flac = new("flac", "flac", 1411);
    private static readonly StreamDetails Mp3At320 = new("mp3", "mp3", 320);
    private static readonly StreamDetails FlacInMp4 = new("m4a", "flac", 1000);

    private static ClientInfo Client(
        int maxAudioBitrate = 0,
        int maxTranscodingAudioBitrate = 0,
        DirectPlayProfile[]? directPlay = null,
        TranscodingProfile[]? transcoding = null,
        CodecProfile[]? codecs = null) => new()
    {
        Name = "test",
        MaxAudioBitrate = maxAudioBitrate,
        MaxTranscodingAudioBitrate = maxTranscodingAudioBitrate,
        DirectPlayProfiles = (directPlay ?? [Plays("flac", "flac"), Plays("mp3", "mp3")]).ToList(),
        TranscodingProfiles = (transcoding ?? [Encodes("mp3", "mp3")]).ToList(),
        CodecProfiles = (codecs ?? []).ToList(),
    };

    private static DirectPlayProfile Plays(string container, string codec) =>
        new() { Containers = [container], AudioCodecs = [codec], Protocols = ["http"] };

    private static TranscodingProfile Encodes(string container, string codec, string protocol = "http") =>
        new() { Container = container, AudioCodec = codec, Protocol = protocol };

    private static CodecProfile BitrateAtMost(string codec, int bps) => new()
    {
        Type = "AudioCodec",
        Name = codec,
        Limitations = [new CodecLimitation { Name = "audioBitrate", Comparison = "LessThanEqual", Values = [bps.ToString()], Required = true }]
    };

    [Fact]
    public void PhoneOnCellular_CappedAt128_GetsMp3At128()
    {
        // What Puffer sends with a 128 kbps cellular quality: FLAC would play, but not at that rate
        var decision = ClientTranscodeNegotiator.Decide(Flac, sourceLossless: true, Client(maxAudioBitrate: 128_000));

        Assert.False(decision.CanDirectPlay);
        Assert.True(decision.CanTranscode);
        Assert.Contains("audio bitrate not supported", decision.TranscodeReasons);
        Assert.NotNull(decision.TranscodeStream);
        Assert.Equal("mp3", decision.TranscodeStream.Codec);
        Assert.Equal(128, decision.TranscodeStream.BitRate);
        Assert.Equal("of1-mp3-128", decision.TranscodeParams);
    }

    [Fact]
    public void NoCap_AndPlayableFormat_DirectPlays()
    {
        var decision = ClientTranscodeNegotiator.Decide(Flac, sourceLossless: true, Client());

        Assert.True(decision.CanDirectPlay);
        Assert.False(decision.CanTranscode);
        Assert.Null(decision.TranscodeStream);
        Assert.Equal(TranscodeToken.Raw, decision.TranscodeParams);
    }

    [Fact]
    public void UnplayableContainer_LosslessSource_UsesFormatDefaultWithoutCaps()
    {
        var decision = ClientTranscodeNegotiator.Decide(Flac, sourceLossless: true,
            Client(directPlay: [Plays("mp3", "mp3")]));

        Assert.True(decision.CanTranscode);
        Assert.Equal(TranscodeProfile.Mp3.DefaultBitRate, decision.TranscodeStream!.BitRate);
    }

    [Fact]
    public void LosslessSource_PrefersMaxTranscodingBitrate()
    {
        var decision = ClientTranscodeNegotiator.Decide(Flac, sourceLossless: true,
            Client(maxAudioBitrate: 320_000, maxTranscodingAudioBitrate: 192_000, directPlay: [Plays("mp3", "mp3")]));

        Assert.Equal(192, decision.TranscodeStream!.BitRate);
    }

    [Fact]
    public void LossySource_KeepsItsRate_UnderTheGlobalCap()
    {
        var decision = ClientTranscodeNegotiator.Decide(Mp3At320, sourceLossless: false,
            Client(maxAudioBitrate: 160_000, transcoding: [Encodes("ogg", "opus")]));

        Assert.Equal("opus", decision.TranscodeStream!.Codec);
        Assert.Equal(160, decision.TranscodeStream.BitRate);
        Assert.Equal("of1-opus-160", decision.TranscodeParams);
    }

    [Fact]
    public void CodecLimitation_BlocksDirectPlay_AndCapsTheTarget()
    {
        var decision = ClientTranscodeNegotiator.Decide(Mp3At320, sourceLossless: false,
            Client(codecs: [BitrateAtMost("mp3", 192_000)]));

        Assert.False(decision.CanDirectPlay);
        Assert.Equal(192, decision.TranscodeStream!.BitRate);
    }

    [Fact]
    public void Mp4Container_MatchesM4aSource()
    {
        var decision = ClientTranscodeNegotiator.Decide(FlacInMp4, sourceLossless: true,
            Client(directPlay: [Plays("mp4", "flac")]));

        Assert.True(decision.CanDirectPlay);
    }

    [Fact]
    public void Mp4AacProfile_EncodesAacInMp4()
    {
        var decision = ClientTranscodeNegotiator.Decide(Flac, sourceLossless: true,
            Client(maxAudioBitrate: 256_000, transcoding: [Encodes("mp4", "aac")]));

        Assert.Equal("mp4", decision.TranscodeStream!.Container);
        Assert.Equal("aac", decision.TranscodeStream.Codec);
        Assert.Equal("of1-m4a-256", decision.TranscodeParams);
    }

    [Fact]
    public void UnusableProfiles_AreSkipped_InOrder()
    {
        // HLS is not offered, and a lossless target cannot shrink anything
        var decision = ClientTranscodeNegotiator.Decide(Flac, sourceLossless: true,
            Client(maxAudioBitrate: 128_000, transcoding: [Encodes("mp3", "mp3", "hls"), Encodes("flac", "flac"), Encodes("ogg", "opus")]));

        Assert.Equal("opus", decision.TranscodeStream!.Codec);
    }

    [Fact]
    public void NothingFits_ReportsAnError()
    {
        var decision = ClientTranscodeNegotiator.Decide(Flac, sourceLossless: true,
            Client(maxAudioBitrate: 128_000, transcoding: [Encodes("flac", "flac")]));

        Assert.False(decision.CanDirectPlay);
        Assert.False(decision.CanTranscode);
        Assert.Equal("no compatible playback profile found", decision.ErrorReason);
        Assert.Equal("", decision.TranscodeParams);
    }

    [Fact]
    public void ParsesTheSpecExample()
    {
        const string body = """
            {
              "name": "Play:1", "platform": "Sonos",
              "maxAudioBitrate": 512000, "maxTranscodingAudioBitrate": 256000,
              "directPlayProfiles": [
                { "containers": [ "mp3" ], "audioCodecs": [ "mp3" ], "protocols": [ "http" ], "maxAudioChannels": 2 },
                { "containers": [ "flac" ], "audioCodecs": [ "flac" ], "protocols": [], "maxAudioChannels": 2 }
              ],
              "transcodingProfiles": [
                { "container": "mp3", "audioCodec": "mp3", "protocol": "http", "maxAudioChannels": 2 }
              ],
              "codecProfiles": [
                { "type": "AudioCodec", "name": "mp3",
                  "limitations": [ { "name": "audioBitrate", "comparison": "LessThanEqual", "values": [ "320000" ], "required": true } ] }
              ]
            }
            """;

        var client = ClientInfo.TryParse(body);

        Assert.NotNull(client);
        Assert.Equal(512_000, client.MaxAudioBitrate);
        Assert.Equal(2, client.DirectPlayProfiles.Count);
        Assert.Equal("320000", client.CodecProfiles[0].Limitations[0].Values[0]);
        Assert.Null(ClientInfo.TryParse("not json"));
        Assert.Null(ClientInfo.TryParse(""));
    }

    [Theory]
    [InlineData("of1-mp3-128", "mp3", 128)]
    [InlineData("of1-m4a-256", "m4a", 256)]
    [InlineData("of1-raw", "raw", 0)]
    public void Token_RoundTrips(string token, string format, int bitRate)
    {
        var request = TranscodeToken.TryParse(token, 30);

        Assert.Equal(new TranscodeRequest(format, bitRate, 30), request);
    }

    [Theory]
    [InlineData("eyJhbGciOiJIUzI1NiJ9.e30.x")] // a Navidrome token
    [InlineData("of1-wma-128")]
    [InlineData("of1-mp3-0")]
    [InlineData("of1-mp3")]
    [InlineData("")]
    [InlineData(null)]
    public void Token_RejectsWhatOctoFiestaDidNotIssue(string? token)
    {
        Assert.Null(TranscodeToken.TryParse(token));
    }
}
