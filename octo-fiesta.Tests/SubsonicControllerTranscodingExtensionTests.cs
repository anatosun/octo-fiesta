using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using octo_fiesta.Controllers;
using octo_fiesta.Models.Domain;
using octo_fiesta.Models.Settings;
using octo_fiesta.Services;
using octo_fiesta.Services.Local;
using octo_fiesta.Services.Subsonic;
using octo_fiesta.Services.Transcoding;

namespace octo_fiesta.Tests;

/// <summary>
/// OpenSubsonic transcoding extension (getTranscodeDecision / getTranscodeStream) for ext- songs,
/// as used by clients such as Puffer against a Navidrome that advertises the extension.
/// </summary>
public class SubsonicControllerTranscodingExtensionTests
{
    private const string PhoneOnCellular = """
        {
          "name": "Puffer",
          "maxAudioBitrate": 128000,
          "directPlayProfiles": [
            { "containers": ["flac"], "audioCodecs": ["flac"], "protocols": ["http"] },
            { "containers": ["mp3"], "audioCodecs": ["mp3"], "protocols": ["http"] }
          ],
          "transcodingProfiles": [
            { "container": "mp3", "audioCodec": "mp3", "protocol": "http" }
          ]
        }
        """;

    private readonly Mock<ILocalLibraryService> _library = new();
    private readonly Mock<IMusicMetadataService> _metadata = new();
    private readonly Mock<IDownloadService> _downloads = new();
    private readonly Mock<IAudioTranscoder> _transcoder = new();

    public SubsonicControllerTranscodingExtensionTests()
    {
        _library.Setup(x => x.ParseSongId("ext-deezer-song-123")).Returns((true, "deezer", "123"));
        _metadata.Setup(x => x.GetSongAsync("deezer", "123")).ReturnsAsync(new Song
        {
            Title = "Track",
            ExternalProvider = "deezer",
            ExternalId = "123",
            ExpectedFormat = AudioFormat.Flac()
        });
        _transcoder.Setup(x => x.IsAvailableAsync()).ReturnsAsync(true);
    }

    private SubsonicController CreateController(string query, string method = "GET", string? jsonBody = null)
    {
        var settings = Options.Create(new SubsonicSettings { Url = "http://localhost:4533" });
        var responseBuilder = new SubsonicResponseBuilder();

        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory.Setup(x => x.CreateClient(It.IsAny<string>()))
            .Returns(new HttpClient(new Mock<HttpMessageHandler>().Object));
        var proxyService = new SubsonicProxyService(httpClientFactory.Object, settings,
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() });

        var hostLifetime = new Mock<IHostApplicationLifetime>();
        hostLifetime.SetupGet(x => x.ApplicationStopping).Returns(CancellationToken.None);

        var controller = new SubsonicController(
            settings,
            _metadata.Object,
            _library.Object,
            _downloads.Object,
            new SubsonicRequestParser(),
            responseBuilder,
            new SubsonicModelMapper(responseBuilder, new Mock<ILogger<SubsonicModelMapper>>().Object),
            proxyService,
            hostLifetime.Object,
            new Mock<ILogger<SubsonicController>>().Object,
            transcoder: _transcoder.Object);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = method;
        httpContext.Request.QueryString = new QueryString(query);
        if (jsonBody != null)
        {
            var bytes = Encoding.UTF8.GetBytes(jsonBody);
            httpContext.Request.ContentType = "application/json";
            httpContext.Request.ContentLength = bytes.Length;
            httpContext.Request.Body = new MemoryStream(bytes);
        }
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    private static JsonElement Decision(IActionResult result)
    {
        var json = JsonSerializer.Serialize(Assert.IsType<JsonResult>(result).Value);
        return JsonDocument.Parse(json).RootElement.GetProperty("subsonic-response").GetProperty("transcodeDecision");
    }

    [Fact]
    public async Task Decision_WithClientCap_OffersMp3TranscodeOfTheFlac()
    {
        var controller = CreateController("?mediaId=ext-deezer-song-123&mediaType=song&f=json", "POST", PhoneOnCellular);

        var decision = Decision(await controller.GetTranscodeDecision());

        Assert.False(decision.GetProperty("canDirectPlay").GetBoolean());
        Assert.True(decision.GetProperty("canTranscode").GetBoolean());
        Assert.Equal("of1-mp3-128", decision.GetProperty("transcodeParams").GetString());
        Assert.Equal("flac", decision.GetProperty("sourceStream").GetProperty("codec").GetString());
        var target = decision.GetProperty("transcodeStream");
        Assert.Equal("mp3", target.GetProperty("codec").GetString());
        Assert.Equal(128_000, target.GetProperty("audioBitrate").GetInt32());
        Assert.Equal("http", target.GetProperty("protocol").GetString());
    }

    [Fact]
    public async Task Decision_WithoutFfmpeg_FallsBackToDirectPlay()
    {
        _transcoder.Setup(x => x.IsAvailableAsync()).ReturnsAsync(false);
        var controller = CreateController("?mediaId=ext-deezer-song-123&mediaType=song&f=json", "POST", PhoneOnCellular);

        var decision = Decision(await controller.GetTranscodeDecision());

        // Promising MP3 would make the client expect a format getTranscodeStream cannot deliver
        Assert.True(decision.GetProperty("canDirectPlay").GetBoolean());
        Assert.False(decision.GetProperty("canTranscode").GetBoolean());
        Assert.Equal(TranscodeToken.Raw, decision.GetProperty("transcodeParams").GetString());
        Assert.False(decision.TryGetProperty("transcodeStream", out _));
    }

    [Fact]
    public async Task Decision_WithoutClientInfo_StaysDirectPlay()
    {
        var controller = CreateController("?mediaId=ext-deezer-song-123&mediaType=song&f=json");

        var decision = Decision(await controller.GetTranscodeDecision());

        Assert.True(decision.GetProperty("canDirectPlay").GetBoolean());
    }

    [Fact]
    public async Task TranscodeStream_ServesTheEncodingTheTokenNames()
    {
        var source = new MemoryStream([1, 2, 3]);
        _downloads.Setup(x => x.DownloadAndStreamAsync("deezer", "123", It.IsAny<CancellationToken>()))
            .ReturnsAsync(((Stream)source, "01 - Track.flac"));
        var encoded = new MemoryStream([9, 9]);
        TranscodeRequest? captured = null;
        _transcoder.Setup(x => x.TryTranscodeAsync(source, "01 - Track.flac", It.IsAny<TranscodeRequest>(), It.IsAny<CancellationToken>()))
            .Callback<Stream, string, TranscodeRequest, CancellationToken>((_, _, request, _) => captured = request)
            .ReturnsAsync(new TranscodedStream(encoded, new TranscodeTarget(TranscodeProfile.Mp3, 128)));

        var controller = CreateController("?mediaId=ext-deezer-song-123&mediaType=song&transcodeParams=of1-mp3-128&offset=42");

        var result = Assert.IsType<FileStreamResult>(await controller.GetTranscodeStream());

        Assert.Same(encoded, result.FileStream);
        Assert.Equal("audio/mpeg", result.ContentType);
        Assert.Equal(new TranscodeRequest("mp3", 128, 42), captured);
    }

    [Fact]
    public async Task TranscodeStream_WithForeignToken_IsGone()
    {
        var controller = CreateController("?mediaId=ext-deezer-song-123&mediaType=song&transcodeParams=eyJhbGciOiJIUzI1NiJ9.e30.x");

        var result = await controller.GetTranscodeStream();

        Assert.Equal(StatusCodes.Status410Gone, Assert.IsType<StatusCodeResult>(result).StatusCode);
        _downloads.Verify(x => x.DownloadAndStreamAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
