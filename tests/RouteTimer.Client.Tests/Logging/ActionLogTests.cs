using RouteTimer.Client.Logging;

namespace RouteTimer.Client.Tests.Logging;

public class ActionLogTests
{
    private const string Key = "AIzaSyExampleKeyValue0123456789abcdefg";

    [Fact]
    public void EntriesRecordLevelAndMessage()
    {
        var log = new ActionLog();
        log.Info("starting");
        log.Warn("careful", "detail text");
        log.Error("stopped");
        log.Success("done");

        Assert.Equal(
            new[] { ActionLevel.Info, ActionLevel.Warn, ActionLevel.Error, ActionLevel.Success },
            log.Entries.Select(e => e.Level));
        Assert.Equal("detail text", log.Entries[1].Detail);
    }

    [Fact]
    public void MessagesAndDetailsAreRedactedOnWrite()
    {
        var log = new ActionLog();
        log.UseRedactionKey(Key);
        log.Info($"key={Key}", $"also {Key}");

        Assert.DoesNotContain(Key, log.Entries[0].Message);
        Assert.DoesNotContain(Key, log.Entries[0].Detail);
    }

    [Fact]
    public void PlainTextExportIsAlsoRedacted()
    {
        var log = new ActionLog();
        log.UseRedactionKey(Key);
        log.Info($"loader url carries {Key}");

        Assert.DoesNotContain(Key, log.ToPlainText());
    }

    [Fact]
    public void ChangedFiresOnEveryWriteAndOnClear()
    {
        var log = new ActionLog();
        var count = 0;
        log.Changed += () => count++;

        log.Info("one");
        log.Info("two");
        log.Clear();

        Assert.Equal(3, count);
        Assert.Empty(log.Entries);
    }

    [Fact]
    public void UseRedactionKeyIsRetroactiveForMessage()
    {
        var log = new ActionLog();
        log.Info($"key={Key}");

        log.UseRedactionKey(Key);

        Assert.DoesNotContain(Key, log.Entries[0].Message);
    }

    [Fact]
    public void UseRedactionKeyIsRetroactiveForDetail()
    {
        var log = new ActionLog();
        log.Info("message", $"detail {Key}");

        log.UseRedactionKey(Key);

        Assert.DoesNotContain(Key, log.Entries[0].Detail);
    }

    [Fact]
    public void UseRedactionKeyIsRetroactiveInPlainText()
    {
        var log = new ActionLog();
        log.Info($"key={Key}");

        log.UseRedactionKey(Key);

        Assert.DoesNotContain(Key, log.ToPlainText());
    }

    [Fact]
    public void UseRedactionKeyPreservesAtAndLevelWhenRedactingRetroactively()
    {
        var log = new ActionLog();
        var beforeAt = DateTimeOffset.UtcNow;
        log.Warn("message", "detail");
        var afterAt = DateTimeOffset.UtcNow;

        log.UseRedactionKey(Key);

        var entry = log.Entries[0];
        Assert.Equal(ActionLevel.Warn, entry.Level);
        Assert.True(entry.At >= beforeAt && entry.At <= afterAt);
    }

    [Fact]
    public void UseRedactionKeyFiresChangedWhenRedactingRetroactively()
    {
        var log = new ActionLog();
        log.Info($"key={Key}");

        var changeCount = 0;
        log.Changed += () => changeCount++;

        log.UseRedactionKey(Key);

        Assert.Equal(1, changeCount);
    }
}
