using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using octo_fiesta.Models.Domain;
using octo_fiesta.Models.Settings;
using octo_fiesta.Models.Download;
using octo_fiesta.Models.Search;
using octo_fiesta.Models.Subsonic;
using octo_fiesta.Services.Local;
using octo_fiesta.Services.Common;
using octo_fiesta.Services.Subsonic;
using Microsoft.Extensions.Options;
using IOFile = System.IO.File;

namespace octo_fiesta.Services.Qobuz;

/// <summary>
/// Download service implementation for Qobuz
/// Handles track downloading with MD5 signature for authentication
/// </summary>
public class QobuzDownloadService : BaseDownloadService
{
    private readonly HttpClient _httpClient;
    private readonly QobuzBundleService _bundleService;
    private readonly string? _userAuthToken;
    private readonly string? _userId;
    private readonly string? _preferredQuality;

    // Index of the bundle secret Qobuz last accepted, tried first on the next track
    private volatile int _workingSecretIndex = -1;

    private const string BaseUrl = "https://www.qobuz.com/api.json/0.2/";
    
    // Quality format IDs
    private const int FormatMp3320 = 5;
    private const int FormatFlac16 = 6;      // CD quality (16-bit 44.1kHz)
    private const int FormatFlac24Low = 7;   // 24-bit < 96kHz
    private const int FormatFlac24High = 27; // 24-bit >= 96kHz

    protected override string ProviderName => "qobuz";

    // The OAuth login stored on disk wins over the configured token; the configured values stay as fallback.
    private string? CurrentUserAuthToken => _bundleService.GetCredentials()?.UserAuthToken ?? _userAuthToken;
    private string? CurrentUserId => _bundleService.GetCredentials()?.UserId ?? _userId;

    public QobuzDownloadService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILocalLibraryService localLibraryService,
        IMusicMetadataService metadataService,
        QobuzBundleService bundleService,
        IOptions<SubsonicSettings> subsonicSettings,
        IOptions<QobuzSettings> qobuzSettings,
        IServiceProvider serviceProvider,
        ILogger<QobuzDownloadService> logger)
        : base(httpClientFactory, configuration, localLibraryService, metadataService, subsonicSettings.Value, serviceProvider, logger)
    {
        _httpClient = httpClientFactory.CreateClient();
        _bundleService = bundleService;
        
        var qobuzConfig = qobuzSettings.Value;
        _userAuthToken = qobuzConfig.UserAuthToken;
        _userId = qobuzConfig.UserId;
        _preferredQuality = qobuzConfig.Quality;
    }

    #region BaseDownloadService Implementation

    public override async Task<bool> IsAvailableAsync()
    {
        if (string.IsNullOrEmpty(CurrentUserAuthToken) || string.IsNullOrEmpty(CurrentUserId))
        {
            Logger.LogWarning("Qobuz user auth token or user ID not configured. Log in at /qobuz/oauth/login");
            return false;
        }

        try
        {
            await _bundleService.GetAppIdAsync();
            await _bundleService.GetSecretsAsync();
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Qobuz service not available");
            return false;
        }
    }

    protected override string? ExtractExternalIdFromAlbumId(string albumId)
    {
        const string prefix = "ext-qobuz-album-";
        if (albumId.StartsWith(prefix))
        {
            return albumId[prefix.Length..];
        }
        return null;
    }
    
    protected override string? GetTargetQuality() => _preferredQuality ?? "FLAC_24_HIGH";

    protected override async Task<DownloadResult> DownloadTrackAsync(string trackId, Song song, CancellationToken cancellationToken)
    {
        // Get the download URL with signature
        var downloadInfo = await GetTrackDownloadUrlAsync(trackId, cancellationToken);
        
        Logger.LogInformation("Download URL obtained for: {Title} - {Artist}", song.Title, song.Artist);
        Logger.LogInformation("Quality: {BitDepth}bit/{SamplingRate}kHz, Format: {MimeType}", 
            downloadInfo.BitDepth, downloadInfo.SamplingRate, downloadInfo.MimeType);

        // Check if it's a demo/sample
        if (downloadInfo.IsSample)
        {
            throw new Exception("Track is only available as a demo/sample");
        }

        // Determine extension based on MIME type
        var extension = downloadInfo.MimeType?.Contains("flac") == true ? ".flac" : ".mp3";
        
        // Determine quality string for storage
        var downloadedQuality = downloadInfo.FormatId switch
        {
            FormatFlac24High => "FLAC_24_HIGH",
            FormatFlac24Low => "FLAC_24_LOW",
            FormatFlac16 => "FLAC_16",
            FormatMp3320 => "MP3_320",
            _ => downloadInfo.MimeType?.Contains("flac") == true ? "FLAC" : "MP3_320"
        };

        var response = await _httpClient.GetAsync(downloadInfo.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var responseStream = await HttpResponseStream.CreateAsync(response, cancellationToken);

        return new DownloadResult(responseStream, extension, downloadedQuality)
        {
            ContentLength = response.Content.Headers.ContentLength
        };
    }

    #endregion

    #region Qobuz Download Methods

    /// <summary>
    /// Gets the download URL for a track with proper MD5 signature
    /// </summary>
    /// <remarks>
    /// Keeps the number of getFileUrl calls low: the secret that signed the last request is tried
    /// first (so usually one call per track), a rejected login ends the attempt at once, and only
    /// the configured quality and those below it are asked for. Qobuz already downgrades a format
    /// a track lacks, so a lower format is only asked for when the preferred one errors.
    /// </remarks>
    private async Task<QobuzDownloadResult> GetTrackDownloadUrlAsync(string trackId, CancellationToken cancellationToken)
    {
        var secrets = await _bundleService.GetSecretsAsync();

        if (secrets.Count == 0)
        {
            throw new Exception("No secrets available for signing");
        }

        var formats = GetFormatPriority(GetFormatId(_preferredQuality));
        var workingSecret = _workingSecretIndex;
        var secretOrder = Enumerable.Range(0, secrets.Count)
            .OrderBy(i => i == workingSecret ? 0 : 1)
            .ToList();

        Exception? lastException = null;

        foreach (var secretIndex in secretOrder)
        {
            foreach (var format in formats)
            {
                try
                {
                    var result = await TryGetTrackDownloadUrlAsync(trackId, format, secrets[secretIndex], cancellationToken);
                    _workingSecretIndex = secretIndex;

                    if (result.WasQualityDowngraded)
                    {
                        Logger.LogWarning("Requested quality not available, Qobuz downgraded to {BitDepth}bit/{SamplingRate}kHz",
                            result.BitDepth, result.SamplingRate);
                    }

                    return result;
                }
                catch (QobuzAuthException)
                {
                    // Another secret or format cannot fix a rejected login
                    throw;
                }
                catch (QobuzSignatureException ex)
                {
                    lastException = ex;
                    Logger.LogDebug("Secret {SecretIndex} rejected by Qobuz, trying the next one", secretIndex);
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    lastException = ex;
                    Logger.LogDebug("Failed to get download URL with secret {SecretIndex}, format {Format}: {Error}",
                        secretIndex, format, ex.Message);
                }
            }

            // This secret was accepted, so the other ones would fail on the same track
            if (secretIndex == workingSecret && lastException is not QobuzSignatureException)
            {
                break;
            }
        }

        throw new Exception($"Failed to get download URL for track {trackId}", lastException);
    }

    private sealed class QobuzAuthException(string message) : HttpRequestException(message);

    private sealed class QobuzSignatureException(string message) : HttpRequestException(message);

    private async Task<QobuzDownloadResult> TryGetTrackDownloadUrlAsync(string trackId, int formatId, string secret, CancellationToken cancellationToken)
    {
        var unix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var appId = await _bundleService.GetAppIdAsync();
        var signature = ComputeMD5Signature(trackId, formatId, unix, secret);
        
        var url = $"{BaseUrl}track/getFileUrl?format_id={formatId}&intent=stream&request_ts={unix}&track_id={trackId}&request_sig={signature}";
        
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        
        request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:83.0) Gecko/20100101 Firefox/83.0");
        request.Headers.Add("X-App-Id", appId);
        
        var userAuthToken = CurrentUserAuthToken;
        if (!string.IsNullOrEmpty(userAuthToken))
        {
            request.Headers.Add("X-User-Auth-Token", userAuthToken);
        }
        
        var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        
        if (!response.IsSuccessStatusCode)
        {
            Logger.LogDebug("Qobuz getFileUrl failed - Status: {StatusCode}, TrackId: {TrackId}, FormatId: {FormatId}",
                response.StatusCode, trackId, formatId);

            var failure = $"Response status code does not indicate success: {response.StatusCode} ({response.ReasonPhrase})";
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                _bundleService.ReportAuthFailure();
                throw new QobuzAuthException(failure);
            }
            // "Invalid Request Signature parameter (request_sig)": signed with the wrong secret
            if (response.StatusCode == System.Net.HttpStatusCode.BadRequest
                && responseBody.Contains("request_sig", StringComparison.OrdinalIgnoreCase))
            {
                throw new QobuzSignatureException(failure);
            }
            throw new HttpRequestException(failure);
        }
        
        var doc = JsonDocument.Parse(responseBody);
        var root = doc.RootElement;
        
        if (!root.TryGetProperty("url", out var urlElement) || string.IsNullOrEmpty(urlElement.GetString()))
        {
            throw new Exception("No download URL in response");
        }
        
        var downloadUrl = urlElement.GetString()!;
        var mimeType = root.TryGetProperty("mime_type", out var mime) ? mime.GetString() : null;
        var bitDepth = root.TryGetProperty("bit_depth", out var bd) ? bd.GetInt32() : 16;
        var samplingRate = root.TryGetProperty("sampling_rate", out var sr) ? sr.GetDouble() : 44.1;
        
        var isSample = root.TryGetProperty("sample", out var sampleEl) && sampleEl.GetBoolean();
        if (samplingRate == 0)
        {
            isSample = true;
        }
        
        var wasDowngraded = false;
        if (root.TryGetProperty("restrictions", out var restrictions))
        {
            foreach (var restriction in restrictions.EnumerateArray())
            {
                if (restriction.TryGetProperty("code", out var code))
                {
                    var codeStr = code.GetString();
                    if (codeStr == "FormatRestrictedByFormatAvailability")
                    {
                        wasDowngraded = true;
                    }
                }
            }
        }
        
        return new QobuzDownloadResult
        {
            Url = downloadUrl,
            FormatId = formatId,
            MimeType = mimeType,
            BitDepth = bitDepth,
            SamplingRate = samplingRate,
            IsSample = isSample,
            WasQualityDowngraded = wasDowngraded
        };
    }

    /// <summary>
    /// Computes MD5 signature for track download request
    /// </summary>
    private string ComputeMD5Signature(string trackId, int formatId, long timestamp, string secret)
    {
        var toSign = $"trackgetFileUrlformat_id{formatId}intentstreamtrack_id{trackId}{timestamp}{secret}";
        
        using var md5 = MD5.Create();
        var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(toSign));
        var signature = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        
        return signature;
    }

    /// <summary>
    /// Gets the format ID based on quality preference
    /// </summary>
    private int GetFormatId(string? quality)
    {
        if (string.IsNullOrEmpty(quality))
        {
            return FormatFlac24High;
        }
        
        return quality.ToUpperInvariant() switch
        {
            "FLAC" => FormatFlac24High,
            "FLAC_24_HIGH" or "24_192" => FormatFlac24High,
            "FLAC_24_LOW" or "24_96" => FormatFlac24Low,
            "FLAC_16" or "CD" => FormatFlac16,
            "MP3_320" or "MP3" => FormatMp3320,
            _ => FormatFlac24High
        };
    }

    /// <summary>
    /// The preferred format followed by the lower ones, best first. Never above the preference.
    /// </summary>
    internal static List<int> GetFormatPriority(int preferredFormat)
    {
        var allFormats = new List<int> { FormatFlac24High, FormatFlac24Low, FormatFlac16, FormatMp3320 };
        var start = allFormats.IndexOf(preferredFormat);
        return allFormats.Skip(Math.Max(start, 0)).ToList();
    }

    #endregion

    private class QobuzDownloadResult
    {
        public string Url { get; set; } = string.Empty;
        public int FormatId { get; set; }
        public string? MimeType { get; set; }
        public int BitDepth { get; set; }
        public double SamplingRate { get; set; }
        public bool IsSample { get; set; }
        public bool WasQualityDowngraded { get; set; }
    }
}
