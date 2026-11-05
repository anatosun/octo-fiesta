using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using octo_fiesta.Models.Settings;
using octo_fiesta.Services.Qobuz;

namespace octo_fiesta.Tests;

public class QobuzOAuthTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "qobuz-oauth-" + Guid.NewGuid().ToString("N"));
    private readonly List<HttpRequestMessage> _requests = new();

    public QobuzOAuthTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private string StorePath => Path.Combine(_dir, "qobuz-token.json");

    private QobuzBundleService CreateService(QobuzSettings settings, HttpStatusCode exchangeStatus,
        string exchangeBody, HttpStatusCode checkStatus, string? storePath = null)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage req, CancellationToken _) =>
            {
                _requests.Add(req);
                var isExchange = req.RequestUri!.AbsolutePath.EndsWith("/oauth/callback");
                return new HttpResponseMessage(isExchange ? exchangeStatus : checkStatus)
                {
                    Content = new StringContent(isExchange ? exchangeBody : "{}")
                };
            });

        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient(handler.Object));

        settings.AppId ??= "123456789";
        settings.AppSecret ??= "secret";
        settings.PrivateKey ??= "privkey";
        settings.TokenStore = storePath ?? StorePath;
        return new QobuzBundleService(factory.Object, Mock.Of<ILogger<QobuzBundleService>>(), Options.Create(settings));
    }

    [Fact]
    public void TokenStore_RoundTripsCredentials()
    {
        var store = new QobuzTokenStore(StorePath);
        Assert.Null(store.Load());

        store.Save(new QobuzCredentials { UserAuthToken = "tok", UserId = "42" });

        var loaded = new QobuzTokenStore(StorePath).Load();
        Assert.Equal("tok", loaded!.UserAuthToken);
        Assert.Equal("42", loaded.UserId);
    }

    [Fact]
    public void TokenStore_CorruptFile_ReturnsNull()
    {
        File.WriteAllText(StorePath, "{ not json");
        Assert.Null(new QobuzTokenStore(StorePath).Load());
    }

    [Fact]
    public void GetCredentials_FallsBackToConfiguredToken_WhenNothingStored()
    {
        var svc = CreateService(new QobuzSettings { UserAuthToken = "cfg", UserId = "1" },
            HttpStatusCode.OK, "{}", HttpStatusCode.OK);

        var creds = svc.GetCredentials();
        Assert.Equal("cfg", creds!.UserAuthToken);
        Assert.Equal("1", creds.UserId);
    }

    [Fact]
    public void GetCredentials_ReturnsNull_WhenNothingConfigured()
    {
        var svc = CreateService(new QobuzSettings(), HttpStatusCode.OK, "{}", HttpStatusCode.OK);
        Assert.Null(svc.GetCredentials());
    }

    [Fact]
    public void GetCredentials_PrefersStoredToken_OverConfiguredToken()
    {
        new QobuzTokenStore(StorePath).Save(new QobuzCredentials { UserAuthToken = "stored", UserId = "9" });
        var svc = CreateService(new QobuzSettings { UserAuthToken = "cfg", UserId = "1" },
            HttpStatusCode.OK, "{}", HttpStatusCode.OK);

        Assert.Equal("stored", svc.GetCredentials()!.UserAuthToken);
    }

    [Fact]
    public async Task ExchangeOAuthCode_Success_PersistsAndUsesToken()
    {
        var svc = CreateService(new QobuzSettings(), HttpStatusCode.OK,
            "{\"token\":\"new-token\",\"user_id\":\"8807208\"}", HttpStatusCode.OK);

        var creds = await svc.ExchangeOAuthCodeAsync("the-code");

        Assert.Equal("new-token", creds.UserAuthToken);
        Assert.Equal("8807208", creds.UserId);
        Assert.Equal("new-token", new QobuzTokenStore(StorePath).Load()!.UserAuthToken);
        Assert.Equal("new-token", svc.GetCredentials()!.UserAuthToken);

        var exchange = _requests.First(r => r.RequestUri!.AbsolutePath.EndsWith("/oauth/callback"));
        Assert.Contains("code=the-code", exchange.RequestUri!.Query);
        Assert.Contains("private_key=privkey", exchange.RequestUri.Query);
        Assert.Contains("app_id=123456789", exchange.RequestUri.Query);
    }

    [Fact]
    public async Task ExchangeOAuthCode_NumericUserId_IsAccepted()
    {
        var svc = CreateService(new QobuzSettings(), HttpStatusCode.OK,
            "{\"token\":\"t\",\"user_id\":8807208}", HttpStatusCode.OK);

        Assert.Equal("8807208", (await svc.ExchangeOAuthCodeAsync("c")).UserId);
    }

    [Fact]
    public async Task ExchangeOAuthCode_RejectedCode_ThrowsAndKeepsStoredToken()
    {
        new QobuzTokenStore(StorePath).Save(new QobuzCredentials { UserAuthToken = "old", UserId = "1" });
        var svc = CreateService(new QobuzSettings(), HttpStatusCode.Unauthorized, "{}", HttpStatusCode.OK);

        await Assert.ThrowsAsync<Exception>(() => svc.ExchangeOAuthCodeAsync("bad"));
        Assert.Equal("old", new QobuzTokenStore(StorePath).Load()!.UserAuthToken);
    }

    [Fact]
    public async Task ExchangeOAuthCode_TokenUnauthorized_ThrowsAndKeepsStoredToken()
    {
        new QobuzTokenStore(StorePath).Save(new QobuzCredentials { UserAuthToken = "old", UserId = "1" });
        var svc = CreateService(new QobuzSettings(), HttpStatusCode.OK,
            "{\"token\":\"new\",\"user_id\":\"2\"}", HttpStatusCode.Unauthorized);

        await Assert.ThrowsAsync<Exception>(() => svc.ExchangeOAuthCodeAsync("c"));
        Assert.Equal("old", new QobuzTokenStore(StorePath).Load()!.UserAuthToken);
    }

    [Fact]
    public async Task ExchangeOAuthCode_CheckReturnsForbidden_StillStoresToken()
    {
        var svc = CreateService(new QobuzSettings(), HttpStatusCode.OK,
            "{\"token\":\"new\",\"user_id\":\"2\"}", HttpStatusCode.Forbidden);

        var creds = await svc.ExchangeOAuthCodeAsync("c");

        Assert.Equal("new", creds.UserAuthToken);
        Assert.Equal("new", new QobuzTokenStore(StorePath).Load()!.UserAuthToken);
    }

    [Fact]
    public async Task ExchangeOAuthCode_MissingToken_Throws()
    {
        var svc = CreateService(new QobuzSettings(), HttpStatusCode.OK, "{\"user_id\":\"2\"}", HttpStatusCode.OK);
        await Assert.ThrowsAsync<Exception>(() => svc.ExchangeOAuthCodeAsync("c"));
    }

    [Fact]
    public void PendingLogin_IsConsumedOnce_AndRequiresBegin()
    {
        var svc = CreateService(new QobuzSettings(), HttpStatusCode.OK, "{}", HttpStatusCode.OK);

        Assert.False(svc.TryConsumePendingLogin());
        svc.BeginOAuthLogin();
        Assert.True(svc.TryConsumePendingLogin());
        Assert.False(svc.TryConsumePendingLogin());
    }

    [Fact]
    public async Task BuildOAuthUrl_UsesAppIdAndEncodedRedirect()
    {
        var svc = CreateService(new QobuzSettings(), HttpStatusCode.OK, "{}", HttpStatusCode.OK);

        var url = await svc.BuildOAuthUrlAsync("http://host:5000/qobuz/oauth/callback");

        Assert.StartsWith("https://www.qobuz.com/signin/oauth?ext_app_id=123456789&redirect_url=", url);
        Assert.Contains(Uri.EscapeDataString("http://host:5000/qobuz/oauth/callback"), url);
    }

    [Fact]
    public async Task ExchangeOAuthCode_StoreNotWritable_KeepsTokenInMemory()
    {
        // A regular file where a directory is needed makes saving fail.
        var blocker = Path.Combine(_dir, "blocker");
        File.WriteAllText(blocker, "x");
        var svc = CreateService(new QobuzSettings(), HttpStatusCode.OK,
            "{\"token\":\"new\",\"user_id\":\"2\"}", HttpStatusCode.OK,
            Path.Combine(blocker, "qobuz-token.json"));

        var creds = await svc.ExchangeOAuthCodeAsync("c");

        Assert.False(creds.Persisted);
        Assert.Equal("new", svc.GetCredentials()!.UserAuthToken);
    }
}
