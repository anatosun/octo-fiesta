using System.Text.Json;
using IOFile = System.IO.File;

namespace octo_fiesta.Services.Qobuz;

/// <summary>
/// Credentials obtained through the Qobuz OAuth login.
/// </summary>
public class QobuzCredentials
{
    public string UserAuthToken { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public DateTimeOffset ObtainedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Persists the OAuth credentials in a small JSON file so they survive restarts.
/// </summary>
public class QobuzTokenStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
    private readonly object _lock = new();

    public QobuzTokenStore(string? path)
    {
        Path = string.IsNullOrWhiteSpace(path) ? "./qobuz-token.json" : path;
    }

    public string Path { get; }

    public QobuzCredentials? Load()
    {
        try
        {
            lock (_lock)
            {
                if (!IOFile.Exists(Path)) return null;
                var creds = JsonSerializer.Deserialize<QobuzCredentials>(IOFile.ReadAllText(Path));
                return creds is { UserAuthToken.Length: > 0, UserId.Length: > 0 } ? creds : null;
            }
        }
        catch
        {
            return null;
        }
    }

    public void Save(QobuzCredentials credentials)
    {
        lock (_lock)
        {
            var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path));
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Write then move so a crash never leaves a truncated token file.
            var tmp = Path + ".tmp";
            IOFile.WriteAllText(tmp, JsonSerializer.Serialize(credentials, SerializerOptions));
            IOFile.Move(tmp, Path, overwrite: true);
            if (!OperatingSystem.IsWindows())
            {
                IOFile.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
    }
}
