using System.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using octo_fiesta.Models.Settings;
using octo_fiesta.Services.Qobuz;

namespace octo_fiesta.Controllers;

/// <summary>
/// One-time Qobuz OAuth login. Open /qobuz/oauth/login in a browser, sign in to Qobuz, and the
/// resulting token is stored on disk and used from then on in place of the configured token.
/// </summary>
[ApiController]
[Route("qobuz/oauth")]
public class QobuzOAuthController : ControllerBase
{
    private readonly QobuzBundleService _bundleService;
    private readonly QobuzSettings _settings;
    private readonly ILogger<QobuzOAuthController> _logger;

    public QobuzOAuthController(
        QobuzBundleService bundleService,
        IOptions<QobuzSettings> settings,
        ILogger<QobuzOAuthController> logger)
    {
        _bundleService = bundleService;
        _settings = settings.Value;
        _logger = logger;
    }

    [HttpGet("login")]
    public async Task<IActionResult> Login()
    {
        try
        {
            var redirectUrl = string.IsNullOrWhiteSpace(_settings.OAuthRedirectUrl)
                ? $"{Request.Scheme}://{Request.Host}/qobuz/oauth/callback"
                : _settings.OAuthRedirectUrl;

            var url = await _bundleService.BuildOAuthUrlAsync(redirectUrl);
            _bundleService.BeginOAuthLogin();
            _logger.LogInformation("Starting Qobuz OAuth login, callback {RedirectUrl}", redirectUrl);
            return Redirect(url);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not start the Qobuz OAuth login");
            return Page(StatusCodes.Status502BadGateway, "Could not start the Qobuz login", ex.Message);
        }
    }

    [HttpGet("callback")]
    public async Task<IActionResult> Callback(
        [FromQuery(Name = "code_autorisation")] string? codeAutorisation,
        [FromQuery] string? code,
        CancellationToken cancellationToken)
    {
        var authCode = !string.IsNullOrWhiteSpace(codeAutorisation) ? codeAutorisation : code;
        if (string.IsNullOrWhiteSpace(authCode))
        {
            return Page(StatusCodes.Status400BadRequest, "Login failed", "Qobuz did not return an authorization code.");
        }

        if (!_bundleService.TryConsumePendingLogin())
        {
            return Page(StatusCodes.Status400BadRequest, "No login in progress",
                "Start the login from /qobuz/oauth/login (it is valid for 10 minutes).");
        }

        try
        {
            var credentials = await _bundleService.ExchangeOAuthCodeAsync(authCode, cancellationToken);
            if (!credentials.Persisted)
            {
                return Page(StatusCodes.Status200OK, "Qobuz login successful, but not saved",
                    $"Logged in as user {credentials.UserId}, but the token could not be written to {_bundleService.TokenStorePath} " +
                    "and will be lost when octo-fiesta restarts. Set Qobuz__TokenStore to a writable path (for Docker, a volume mounted on /config) and log in again.");
            }

            return Page(StatusCodes.Status200OK, "Qobuz login successful",
                $"Logged in as user {credentials.UserId}. The token is stored in {_bundleService.TokenStorePath}. You can close this tab.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Qobuz OAuth code exchange failed");
            return Page(StatusCodes.Status502BadGateway, "Login failed", ex.Message);
        }
    }

    private ContentResult Page(int status, string title, string message) => new()
    {
        StatusCode = status,
        ContentType = "text/html; charset=utf-8",
        Content = $"<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">" +
                  $"<title>{WebUtility.HtmlEncode(title)}</title></head>" +
                  $"<body style=\"font-family:system-ui;text-align:center;padding:60px\"><h2>{WebUtility.HtmlEncode(title)}</h2>" +
                  $"<p>{WebUtility.HtmlEncode(message)}</p></body></html>"
    };
}
