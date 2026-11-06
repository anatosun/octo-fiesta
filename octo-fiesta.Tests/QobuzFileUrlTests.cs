using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using octo_fiesta.Models.Domain;
using octo_fiesta.Models.Settings;
using octo_fiesta.Services;
using octo_fiesta.Services.Local;
using octo_fiesta.Services.Qobuz;

namespace octo_fiesta.Tests;

/// <summary>
/// How many getFileUrl calls a download costs: Qobuz may ban an account that floods it.
/// </summary>
public class QobuzFileUrlTests : IDisposable
{
    private const string GoodSecret = "good-secret";
    private static readonly byte[] FlacBytes = "fLaC"u8.ToArray().Concat(new byte[60]).ToArray();

    private readonly string _downloadPath =
        Path.Combine(Path.GetTempPath(), "octo-fiesta-qobuz-fileurl-tests-" + Guid.NewGuid());

    public QobuzFileUrlTests() => Directory.CreateDirectory(_downloadPath);

    public void Dispose()
    {
        if (Directory.Exists(_downloadPath))
        {
            Directory.Delete(_downloadPath, true);
        }
    }

    [Fact]
    public async Task RemembersTheSecretQobuzAccepted()
    {
        var qobuz = new FakeQobuz();
        var service = CreateService(qobuz, secrets: ["stale-secret", GoodSecret]);

        await service.DownloadSongAsync("qobuz", "1");
        await service.DownloadSongAsync("qobuz", "2");

        // Track 1 finds the right secret, track 2 starts with it
        Assert.Equal(new[] { "1", "1", "2" }, qobuz.FileUrlRequests.Select(r => r.TrackId));
    }

    [Fact]
    public async Task StopsAtTheFirstRejectedLogin()
    {
        var qobuz = new FakeQobuz { Status = HttpStatusCode.Unauthorized };
        var service = CreateService(qobuz, secrets: [GoodSecret, "other-secret"]);

        await Assert.ThrowsAnyAsync<Exception>(() => service.DownloadSongAsync("qobuz", "1"));

        // Neither the lower formats nor the other secret are tried
        Assert.Single(qobuz.FileUrlRequests);
    }

    [Fact]
    public async Task NeverAsksAboveTheConfiguredQuality()
    {
        var qobuz = new FakeQobuz { Status = HttpStatusCode.NotFound };
        var service = CreateService(qobuz, secrets: [GoodSecret], quality: "FLAC_16");

        await Assert.ThrowsAnyAsync<Exception>(() => service.DownloadSongAsync("qobuz", "1"));

        // CD quality, then MP3: never the 24-bit formats
        Assert.Equal(new[] { 6, 5 }, qobuz.FileUrlRequests.Select(r => r.FormatId));
    }

    [Fact]
    public void FormatPriority_StartsAtThePreferenceAndOnlyGoesDown()
    {
        Assert.Equal(new[] { 27, 7, 6, 5 }, QobuzDownloadService.GetFormatPriority(27));
        Assert.Equal(new[] { 5 }, QobuzDownloadService.GetFormatPriority(5));
    }

    private QobuzDownloadService CreateService(FakeQobuz qobuz, List<string> secrets, string? quality = null)
    {
        var httpClient = new HttpClient(qobuz);
        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        var bundle = new Mock<QobuzBundleService>(httpClientFactory.Object, NullLogger<QobuzBundleService>.Instance,
            Options.Create(new QobuzSettings())) { CallBase = false };
        bundle.Setup(b => b.GetAppIdAsync()).ReturnsAsync("app-id");
        bundle.Setup(b => b.GetSecretsAsync()).ReturnsAsync(secrets);
        qobuz.Secrets = secrets;

        var metadata = new Mock<IMusicMetadataService>();
        metadata
            .Setup(m => m.GetSongAsync("qobuz", It.IsAny<string>()))
            .ReturnsAsync((string _, string id) => new Song { ExternalId = id, Title = "Track " + id, Artist = "Artist", Album = "Album" });

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Library:DownloadPath"] = _downloadPath })
            .Build();

        return new QobuzDownloadService(
            httpClientFactory.Object,
            config,
            new Mock<ILocalLibraryService>().Object,
            metadata.Object,
            bundle.Object,
            Options.Create(new SubsonicSettings()),
            Options.Create(new QobuzSettings { UserAuthToken = "token", UserId = "1", Quality = quality }),
            new Mock<IServiceProvider>().Object,
            NullLogger<QobuzDownloadService>.Instance);
    }

    /// <summary>
    /// Accepts getFileUrl requests signed with <see cref="GoodSecret"/> and answers with
    /// <see cref="Status"/>, or rejects the signature like Qobuz does; serves a tiny FLAC file.
    /// </summary>
    private sealed class FakeQobuz : HttpMessageHandler
    {
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        public List<string> Secrets { get; set; } = [];
        public ConcurrentQueue<(string TrackId, int FormatId)> FileUrlRequestsQueue { get; } = new();
        public IReadOnlyList<(string TrackId, int FormatId)> FileUrlRequests => FileUrlRequestsQueue.ToList();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (uri.Host == "files.example")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(FlacBytes) });
            }

            var query = HttpUtility.ParseQueryString(uri.Query);
            var trackId = query["track_id"]!;
            var formatId = int.Parse(query["format_id"]!);
            FileUrlRequestsQueue.Enqueue((trackId, formatId));

            if (query["request_sig"] != Sign(trackId, formatId, query["request_ts"]!, GoodSecret))
            {
                return Json(HttpStatusCode.BadRequest,
                    """{"status":"error","code":400,"message":"Invalid Request Signature parameter (request_sig)"}""");
            }
            if (Status != HttpStatusCode.OK)
            {
                return Json(Status, """{"status":"error"}""");
            }
            return Json(HttpStatusCode.OK,
                $$"""{"url":"https://files.example/{{trackId}}.flac","format_id":{{formatId}},"mime_type":"audio/flac","bit_depth":16,"sampling_rate":44.1}""");
        }

        private static Task<HttpResponseMessage> Json(HttpStatusCode status, string body) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });

        private static string Sign(string trackId, int formatId, string timestamp, string secret)
        {
            var hash = MD5.HashData(Encoding.UTF8.GetBytes($"trackgetFileUrlformat_id{formatId}intentstreamtrack_id{trackId}{timestamp}{secret}"));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
    }
}
