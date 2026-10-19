using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DistributedHls.Core;

// Anonymous playback grant carried in the URL path: Base64Url(JSON {"v":1,"i":"<item N>","e":<unix seconds>}) "." Base64Url(HMACSHA256).
public static class PlaybackGrant
{
    private const int MaxTokenLength = 512;

    public static string Create(Guid itemId, DateTimeOffset exp, byte[] key)
    {
        if (key.Length == 0) throw new ArgumentException("签名密钥不能为空。");
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { v = 1, i = itemId.ToString("N"), e = exp.ToUnixTimeSeconds() }));
        return payload + "." + Base64Url(HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(payload)));
    }

    // Never throws: anything that is not a well-formed, correctly signed, unexpired version 1 grant is simply false.
    public static bool TryVerify(string token, byte[] key, DateTimeOffset now, out Guid itemId)
    {
        itemId = default;
        if (string.IsNullOrEmpty(token) || token.Length > MaxTokenLength || key.Length == 0) return false;
        var parts = token.Split('.');
        if (parts.Length != 2 || !TryFromBase64Url(parts[0], out var json) || !TryFromBase64Url(parts[1], out var signature)) return false;
        if (!CryptographicOperations.FixedTimeEquals(signature, HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(parts[0])))) return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("v", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var v) || v != 1
                || !root.TryGetProperty("e", out var expires) || expires.ValueKind != JsonValueKind.Number || !expires.TryGetInt64(out var e) || e <= now.ToUnixTimeSeconds()
                || !root.TryGetProperty("i", out var item) || item.ValueKind != JsonValueKind.String || !Guid.TryParseExact(item.GetString(), "N", out var id)) return false;
            itemId = id;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // Strict inverse of Base64Url: only the URL-safe alphabet, padding restored from the length, and only the canonical spelling is accepted.
    private static bool TryFromBase64Url(string text, out byte[] bytes)
    {
        bytes = [];
        if (text.Length == 0 || !text.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) return false;
        try
        {
            bytes = Convert.FromBase64String(text.Replace('-', '+').Replace('_', '/').PadRight((text.Length + 3) / 4 * 4, '='));
        }
        catch (FormatException)
        {
            return false;
        }
        return Base64Url(bytes) == text;
    }
}
