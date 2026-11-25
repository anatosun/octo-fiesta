using octo_fiesta.Services.Transcoding;

namespace octo_fiesta.Tests;

public class TranscodeDeciderTests
{
    private static readonly SourceAudio Flac = new("flac", 1411, Lossless: true);
    private static readonly SourceAudio Mp3At320 = new("mp3", 320, Lossless: false);
    private static readonly SourceAudio Mp3At128 = new("mp3", 128, Lossless: false);
    private static readonly SourceAudio TidalFlacInMp4 = new("m4a", 1000, Lossless: true);

    [Fact]
    public void NoRequest_ServesOriginal()
    {
        Assert.Null(TranscodeDecider.Decide(Flac, null, 0));
    }

    [Theory]
    [InlineData("raw", 128)]
    [InlineData("RAW", 0)]
    public void RawFormat_ServesOriginalEvenWithCap(string format, int maxBitRate)
    {
        Assert.Null(TranscodeDecider.Decide(Flac, format, maxBitRate));
    }

    [Fact]
    public void LosslessOverCap_WithoutFormat_EncodesMp3AtCap()
    {
        var target = TranscodeDecider.Decide(Flac, null, 128);

        Assert.NotNull(target);
        Assert.Same(TranscodeProfile.Mp3, target.Profile);
        Assert.Equal(128, target.BitRate);
    }

    [Fact]
    public void LosslessUnderCap_WithoutFormat_ServesOriginal()
    {
        // A FLAC that compresses below the cap (a quiet or mono track) already fits the client's budget
        Assert.Null(TranscodeDecider.Decide(new SourceAudio("flac", 110, Lossless: true), null, 128));
    }

    [Fact]
    public void LossyUnderCap_ServesOriginal()
    {
        Assert.Null(TranscodeDecider.Decide(Mp3At128, null, 320));
        Assert.Null(TranscodeDecider.Decide(Mp3At320, null, 320));
    }

    [Fact]
    public void LossyOverCap_IsReencodedAtCap()
    {
        var target = TranscodeDecider.Decide(Mp3At320, null, 96);

        Assert.NotNull(target);
        Assert.Equal(96, target.BitRate);
    }

    [Fact]
    public void RequestedFormat_WithoutCap_UsesFormatDefault()
    {
        var target = TranscodeDecider.Decide(Flac, "opus", 0);

        Assert.NotNull(target);
        Assert.Same(TranscodeProfile.Opus, target.Profile);
        Assert.Equal(TranscodeProfile.Opus.DefaultBitRate, target.BitRate);
    }

    [Fact]
    public void RequestedFormat_MatchingLossySource_WithinCap_ServesOriginal()
    {
        Assert.Null(TranscodeDecider.Decide(Mp3At320, "mp3", 0));
        Assert.Null(TranscodeDecider.Decide(Mp3At320, "mp3", 320));
    }

    [Fact]
    public void RequestedM4a_FromLosslessMp4_IsEncodedToAac()
    {
        // Tidal delivers FLAC inside MP4: same suffix, but not what a client asking for m4a can afford
        var target = TranscodeDecider.Decide(TidalFlacInMp4, "m4a", 256);

        Assert.NotNull(target);
        Assert.Same(TranscodeProfile.M4a, target.Profile);
        Assert.Equal(256, target.BitRate);
    }

    [Fact]
    public void LossySource_IsNotUpsampledAboveItsOwnRate()
    {
        var target = TranscodeDecider.Decide(Mp3At128, "opus", 256);

        Assert.NotNull(target);
        Assert.Equal(128, target.BitRate);
    }

    [Fact]
    public void BitRate_IsClampedToWhatTheEncoderSupports()
    {
        var target = TranscodeDecider.Decide(Flac, "mp3", 1000);

        Assert.NotNull(target);
        Assert.Equal(320, target.BitRate);
    }

    [Fact]
    public void UnsupportedFormat_WithoutCap_ServesOriginal()
    {
        Assert.Null(TranscodeDecider.Decide(Flac, "flac", 0));
        Assert.Null(TranscodeDecider.Decide(Flac, "wma", 0));
    }

    [Fact]
    public void UnsupportedFormat_WithCap_FallsBackToMp3()
    {
        var target = TranscodeDecider.Decide(Flac, "wma", 192);

        Assert.NotNull(target);
        Assert.Same(TranscodeProfile.Mp3, target.Profile);
        Assert.Equal(192, target.BitRate);
    }
}
