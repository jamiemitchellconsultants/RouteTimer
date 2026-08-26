using RouteTimer.Client.Logging;

namespace RouteTimer.Client.Tests.Logging;

public class KeyRedactorTests
{
    private const string Key = "AIzaSyExampleKeyValue0123456789abcdefg";

    [Fact]
    public void KeyEmbeddedInAMessageIsMasked()
    {
        var result = KeyRedactor.Redact($"loading https://maps.googleapis.com/maps/api/js?key={Key}&v=weekly", Key);

        Assert.DoesNotContain(Key, result);
        Assert.Contains("AIza…defg", result);
    }

    [Fact]
    public void EveryOccurrenceIsMasked()
    {
        var result = KeyRedactor.Redact($"{Key} and again {Key}", Key);
        Assert.DoesNotContain(Key, result);
    }

    [Fact]
    public void MessageWithoutTheKeyIsUnchanged()
    {
        const string message = "resolved short link to www.google.com/maps/dir/...";
        Assert.Equal(message, KeyRedactor.Redact(message, Key));
    }

    [Fact]
    public void NullTextStaysNull()
    {
        Assert.Null(KeyRedactor.Redact(null, Key));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AbsentKeyLeavesTextAlone(string? key)
    {
        Assert.Equal("some text", KeyRedactor.Redact("some text", key));
    }

    [Fact]
    public void ShortStringIsNotTreatedAsAKeyAndDoesNotCorruptTheText()
    {
        // "abc" is not a plausible key; redacting it would mangle every message containing
        // those three letters for no security benefit.
        Assert.Equal("abcdefg abc", KeyRedactor.Redact("abcdefg abc", "abc"));
    }

    [Fact]
    public void MaskShowsFirstAndLastFour()
    {
        Assert.Equal("AIza…defg", KeyRedactor.Mask(Key));
    }
}
