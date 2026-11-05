using System.IO.Compression;
using System.Text;

namespace octo_fiesta.Services.Qobuz;

public static class QobuzHttpExtensions
{
    /// <summary>
    /// Reads a response body as text, decoding gzip/deflate/brotli when the client did not do it itself
    /// (a client without automatic decompression otherwise yields binary garbage).
    /// </summary>
    public static async Task<string> ReadDecodedStringAsync(this HttpContent content, CancellationToken cancellationToken = default)
    {
        var bytes = await content.ReadAsByteArrayAsync(cancellationToken);
        return Decode(bytes, content.Headers.ContentEncoding.FirstOrDefault());
    }

    public static string Decode(byte[] bytes, string? contentEncoding)
    {
        try
        {
            var encoding = contentEncoding?.ToLowerInvariant();
            var isGzip = bytes.Length > 2 && bytes[0] == 0x1f && bytes[1] == 0x8b;

            if (isGzip || encoding == "gzip")
            {
                return Read(new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress));
            }

            if (encoding == "br")
            {
                return Read(new BrotliStream(new MemoryStream(bytes), CompressionMode.Decompress));
            }

            if (encoding == "deflate")
            {
                try
                {
                    return Read(new ZLibStream(new MemoryStream(bytes), CompressionMode.Decompress));
                }
                catch (InvalidDataException)
                {
                    return Read(new DeflateStream(new MemoryStream(bytes), CompressionMode.Decompress));
                }
            }
        }
        catch (Exception)
        {
            // Fall through to plain text.
        }

        return Encoding.UTF8.GetString(bytes);
    }

    private static string Read(Stream stream)
    {
        using (stream)
        using (var reader = new StreamReader(stream, Encoding.UTF8))
        {
            return reader.ReadToEnd();
        }
    }
}
