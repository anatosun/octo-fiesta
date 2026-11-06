using System.Text.Json;
using octo_fiesta.Models.Domain;
using octo_fiesta.Services.Deezer;
using octo_fiesta.Services.Qobuz;
using octo_fiesta.Services.Subsonic;

namespace octo_fiesta.Tests;

/// <summary>
/// The format announced for a track that is not downloaded yet must match what the stream
/// will carry: clients pick their decoder and buffer size from it.
/// </summary>
public class ExpectedFormatTests
{
    [Theory]
    [InlineData(27, 24, 192.0, "flac", 9216)]
    [InlineData(27, 24, 96.0, "flac", 4608)]
    [InlineData(27, 16, 44.1, "flac", 1411)]
    [InlineData(7, 24, 192.0, "flac", 4608)]   // capped at 96 kHz
    [InlineData(6, 24, 192.0, "flac", 1411)]   // CD quality asked for
    [InlineData(5, 24, 192.0, "mp3", 320)]
    [InlineData(27, null, null, "flac", 1411)] // resolution unknown: assume CD
    public void Qobuz_DeliversTheTrackResolutionCappedByTheConfiguredFormat(
        int formatId, int? bitDepth, double? sampleRate, string suffix, int bitRate)
    {
        var format = QobuzQuality.ExpectedFormat(formatId, bitDepth, sampleRate);

        Assert.Equal(suffix, format.Suffix);
        Assert.Equal(bitRate, format.BitRate);
    }

    [Theory]
    [InlineData(null, "flac", 1411)]
    [InlineData("FLAC", "flac", 1411)]
    [InlineData("MP3_320", "mp3", 320)]
    [InlineData("mp3_128", "mp3", 128)]
    public void Deezer_FollowsTheConfiguredQuality(string? quality, string suffix, int bitRate)
    {
        var format = DeezerMetadataService.GetExpectedFormat(quality);

        Assert.Equal(suffix, format.Suffix);
        Assert.Equal(bitRate, format.BitRate);
    }

    [Fact]
    public void UndownloadedSong_IsAnnouncedInItsExpectedFormat()
    {
        var song = new Song
        {
            Title = "Track",
            ExternalProvider = "qobuz",
            ExternalId = "1",
            Duration = 200,
            ExpectedFormat = AudioFormat.Flac(24, 96)
        };

        var json = new SubsonicResponseBuilder().ConvertSongToJson(song);

        Assert.Equal("flac", json["suffix"]);
        Assert.Equal("audio/flac", json["contentType"]);
        Assert.Equal(4608, json["bitRate"]);
        Assert.Equal(4608L * 125 * 200, json["size"]);
    }

    [Fact]
    public void TranscodeDecision_AnnouncesTheExpectedContainer()
    {
        var song = new Song { Title = "Track", ExternalProvider = "deezer", ExternalId = "1", ExpectedFormat = AudioFormat.Flac() };

        var result = (Microsoft.AspNetCore.Mvc.JsonResult)new SubsonicResponseBuilder().CreateTranscodeDecisionResponse(song, "http");
        var json = JsonSerializer.Serialize(result.Value);

        Assert.Contains("\"container\":\"flac\"", json);
        Assert.Contains("\"audioBitrate\":1411000", json);
    }
}
