using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace DistributedHls.Core.Tests;

public sealed class PlaybackGrantTests
{
    private static readonly byte[] Key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    private static readonly Guid Item = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Expiry = Now.AddHours(24);

    // The documented encoding, written independently of the production code.
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string text) => Convert.FromBase64String(text.Replace('-', '+').Replace('_', '/').PadRight((text.Length + 3) / 4 * 4, '='));

    // Signs any payload text exactly like PlaybackGrant.Create does, to craft tokens that are correctly signed but otherwise wrong.
    private static string Sign(string payloadJson, byte[]? key = null)
    {
        var payload = Base64Url(Encoding.UTF8.GetBytes(payloadJson));
        return payload + "." + Base64Url(HMACSHA256.HashData(key ?? Key, Encoding.ASCII.GetBytes(payload)));
    }

    private static string Payload(string template) => template
        .Replace("ITEMD", Item.ToString("D"))
        .Replace("ITEM", Item.ToString("N"))
        .Replace("EXP", Expiry.ToUnixTimeSeconds().ToString());

    private static bool Verify(string token, DateTimeOffset? now = null) => PlaybackGrant.TryVerify(token, Key, now ?? Now, out _);

    [Fact]
    public void CreatedTokenVerifiesAndReturnsTheItem()
    {
        var token = PlaybackGrant.Create(Item, Expiry, Key);
        Assert.True(PlaybackGrant.TryVerify(token, Key, Now, out var item));
        Assert.Equal(Item, item);
    }

    [Fact]
    public void TokenIsAUrlSafePayloadAndHmacSeparatedByADot()
    {
        var token = PlaybackGrant.Create(Item, Expiry, Key);
        Assert.Matches("^[A-Za-z0-9_-]+\\.[A-Za-z0-9_-]+$", token);
        Assert.True(token.Length <= 512);
        var parts = token.Split('.');
        Assert.Equal($$"""{"v":1,"i":"{{Item:N}}","e":{{Expiry.ToUnixTimeSeconds()}}}""", Encoding.UTF8.GetString(FromBase64Url(parts[0])));
        Assert.Equal(Base64Url(HMACSHA256.HashData(Key, Encoding.ASCII.GetBytes(parts[0]))), parts[1]);
    }

    [Fact]
    public void ManyRandomGrantsStayUrlSafeAndVerify()
    {
        var random = new Random(7);
        for (var i = 0; i < 200; i++)
        {
            var item = new Guid(random.GetItems<byte>(Enumerable.Range(0, 256).Select(b => (byte)b).ToArray(), 16));
            var expiry = Now.AddSeconds(random.Next(1, 10_000_000));
            var token = PlaybackGrant.Create(item, expiry, Key);
            Assert.Matches("^[A-Za-z0-9_-]+\\.[A-Za-z0-9_-]+$", token);
            Assert.True(PlaybackGrant.TryVerify(token, Key, Now, out var verified));
            Assert.Equal(item, verified);
        }
    }

    [Fact]
    public void PaddingIsRestoredForEveryPayloadLength()
    {
        // Payload byte lengths 0, 1 and 2 modulo 3 need no, two or one "=" characters when decoded.
        foreach (var spaces in new[] { 0, 1, 2, 3 })
        {
            var token = Sign(Payload("{\"v\":1,\"i\":\"ITEM\",\"e\":EXP}") + new string(' ', spaces));
            Assert.True(PlaybackGrant.TryVerify(token, Key, Now, out var item), $"{spaces} trailing spaces");
            Assert.Equal(Item, item);
        }
    }

    [Fact]
    public void RejectsAnotherKey()
    {
        var token = PlaybackGrant.Create(Item, Expiry, Key);
        Assert.False(PlaybackGrant.TryVerify(token, [.. Key.Reverse()], Now, out var item));
        Assert.Equal(Guid.Empty, item);
    }

    [Fact]
    public void RejectsAnEmptyKeyOnBothSides()
    {
        Assert.Throws<ArgumentException>(() => PlaybackGrant.Create(Item, Expiry, []));
        Assert.False(PlaybackGrant.TryVerify(Sign(Payload("{\"v\":1,\"i\":\"ITEM\",\"e\":EXP}"), []), [], Now, out _));
    }

    [Fact]
    public void RejectsAnyChangedCharacter()
    {
        var token = PlaybackGrant.Create(Item, Expiry, Key);
        for (var position = 0; position < token.Length; position++)
        {
            if (token[position] == '.') continue;
            var changed = token[position] == 'A' ? 'B' : 'A';
            Assert.False(Verify(token[..position] + changed + token[(position + 1)..]), $"position {position}");
        }
    }

    [Fact]
    public void RejectsANonCanonicalSpellingOfTheSameSignature()
    {
        // A 32 byte signature leaves two unused bits in its last character; a lenient decoder ignores them,
        // which would make a second string valid for the same grant.
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
        var token = PlaybackGrant.Create(Item, Expiry, Key);
        var signature = token.Split('.')[1];
        var variant = signature[..^1] + alphabet[alphabet.IndexOf(signature[^1]) | 1];
        Assert.NotEqual(signature, variant);
        Assert.Equal(FromBase64Url(signature), FromBase64Url(variant));
        Assert.False(Verify(token.Split('.')[0] + "." + variant));
    }

    [Fact]
    public void RejectsAPayloadMovedToAnotherSignature()
    {
        var mine = PlaybackGrant.Create(Item, Expiry, Key);
        var other = PlaybackGrant.Create(Guid.NewGuid(), Expiry, Key);
        Assert.False(Verify(other.Split('.')[0] + "." + mine.Split('.')[1]));
        Assert.False(Verify(mine.Split('.')[0] + "." + other.Split('.')[1]));
    }

    [Fact]
    public void RejectsATruncatedOrExtendedSignature()
    {
        var token = PlaybackGrant.Create(Item, Expiry, Key);
        Assert.False(Verify(token[..^1]));
        Assert.False(Verify(token[..^2]));
        Assert.False(Verify(token + "A"));
        Assert.False(Verify(token + "AA"));
    }

    [Fact]
    public void ExpiresExactlyAtTheEncodedSecond()
    {
        var token = PlaybackGrant.Create(Item, Expiry, Key);
        Assert.True(Verify(token, Expiry.AddSeconds(-1)));
        Assert.True(Verify(token, Expiry.AddMilliseconds(-500)));
        Assert.False(Verify(token, Expiry));
        Assert.False(Verify(token, Expiry.AddMilliseconds(500)));
        Assert.False(Verify(token, Expiry.AddSeconds(1)));
        Assert.False(Verify(token, Expiry.AddDays(30)));
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a")]
    [InlineData("a.b.c")]
    [InlineData("abc.")]
    [InlineData(".abc")]
    [InlineData("a b.c")]
    [InlineData("%%%.%%%")]
    [InlineData("YWJj.YWJj")]                                           // well-formed Base64Url, wrong signature
    [InlineData("YWJj=.YWJj")]                                          // padding is not part of the format
    [InlineData("YW Jj.YWJj")]
    [InlineData("YWJj\n.YWJj")]
    [InlineData("é.é")]
    public void RejectsMalformedTokens(string token) => Assert.False(Verify(token));

    [Fact]
    public void RejectsNullWithoutThrowing()
    {
        Assert.False(PlaybackGrant.TryVerify(null!, Key, Now, out var item));
        Assert.Equal(Guid.Empty, item);
    }

    [Fact]
    public void RejectsTokensLongerThan512Characters()
    {
        string TokenWithPadding(int length) => Sign(Payload("{\"v\":1,\"i\":\"ITEM\",\"e\":EXP,\"x\":\"") + new string('x', length) + "\"}");
        // 351 payload bytes encode to 468 characters: 468 + "." + 43 signature characters = exactly 512.
        var length = 351 - Encoding.UTF8.GetByteCount(Payload("{\"v\":1,\"i\":\"ITEM\",\"e\":EXP,\"x\":\"\"}"));
        var exact = TokenWithPadding(length);
        Assert.Equal(512, exact.Length);
        Assert.True(Verify(exact));
        // One more payload byte is the smallest step: 352 bytes encode to 470 characters, so the token has 514.
        var longer = TokenWithPadding(length + 1);
        Assert.Equal(514, longer.Length);
        Assert.False(Verify(longer));
    }

    [Theory]
    [InlineData("{\"v\":1,\"i\":\"ITEM\",\"e\":EXP}", true)]
    [InlineData("{\"e\":EXP,\"i\":\"ITEM\",\"v\":1}", true)]
    [InlineData("{\"v\":1,\"i\":\"ITEM\",\"e\":EXP,\"other\":[1,2]}", true)]
    [InlineData("{\"v\":2,\"i\":\"ITEM\",\"e\":EXP}", false)]
    [InlineData("{\"v\":0,\"i\":\"ITEM\",\"e\":EXP}", false)]
    [InlineData("{\"v\":\"1\",\"i\":\"ITEM\",\"e\":EXP}", false)]
    [InlineData("{\"v\":1.5,\"i\":\"ITEM\",\"e\":EXP}", false)]
    [InlineData("{\"i\":\"ITEM\",\"e\":EXP}", false)]
    [InlineData("{\"v\":1,\"e\":EXP}", false)]
    [InlineData("{\"v\":1,\"i\":\"ITEM\"}", false)]
    [InlineData("{\"v\":1,\"i\":5,\"e\":EXP}", false)]
    [InlineData("{\"v\":1,\"i\":null,\"e\":EXP}", false)]
    [InlineData("{\"v\":1,\"i\":\"\",\"e\":EXP}", false)]
    [InlineData("{\"v\":1,\"i\":\"not-a-guid\",\"e\":EXP}", false)]
    [InlineData("{\"v\":1,\"i\":\"ITEMD\",\"e\":EXP}", false)]          // hyphenated "D" format is not the "N" format
    [InlineData("{\"v\":1,\"i\":\"ITEM\",\"e\":\"EXP\"}", false)]
    [InlineData("{\"v\":1,\"i\":\"ITEM\",\"e\":EXP.5}", false)]
    [InlineData("{\"v\":1,\"i\":\"ITEM\",\"e\":1e30}", false)]
    [InlineData("{\"v\":1,\"i\":\"ITEM\",\"e\":-1}", false)]
    [InlineData("{\"v\":1,\"i\":\"ITEM\",\"e\":null}", false)]
    [InlineData("[1]", false)]
    [InlineData("123", false)]
    [InlineData("\"text\"", false)]
    [InlineData("null", false)]
    [InlineData("true", false)]
    [InlineData("not json", false)]
    [InlineData("{", false)]
    [InlineData("", false)]
    [InlineData("{\"v\":1,\"i\":\"ITEM\",\"e\":EXP} trailing", false)]
    [InlineData("{\"v\":1,\"i\":\"ITEM\",\"e\":EXP,}", false)]
    public void ChecksTheSignedPayloadContent(string template, bool valid)
    {
        var token = Sign(Payload(template));
        Assert.Equal(valid, PlaybackGrant.TryVerify(token, Key, Now, out var item));
        Assert.Equal(valid ? Item : Guid.Empty, item);
    }

    [Fact]
    public void NeverThrowsOnArbitraryInput()
    {
        var random = new Random(42);
        var alphabet = "ABCxyz019-_=.+/ \n\t%é€\0\"{}[]:,";
        for (var i = 0; i < 2000; i++)
        {
            var token = new string(Enumerable.Range(0, random.Next(0, 700)).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());
            Assert.False(Verify(token), token);
        }
    }
}
