using Dynastream.Fit;

namespace RouteTimer.Services.Tests.Activities;

internal static class FitTestFileBuilder
{
    public static MemoryStream ActivityWithPause() => CyclingActivity(
        startedAt: new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero),
        endedAt: new DateTimeOffset(2026, 1, 1, 12, 0, 2, TimeSpan.Zero),
        recordOffsetsSeconds: [0, 2],
        timerEventOffsetsSeconds: [(0, EventType.Start), (1, EventType.Stop)],
        powersWatts: [220, 0],
        totalTimerSeconds: 1,
        totalDistanceMetres: 10,
        totalAscentMetres: null,
        manufacturer: Manufacturer.Development,
        product: 1,
        productName: null,
        includeSessionTimestamp: true);

    public static MemoryStream CyclingActivity(
        DateTimeOffset startedAt,
        DateTimeOffset endedAt,
        double? totalDistanceMetres,
        ushort? totalAscentMetres,
        bool includeSessionTimestamp = true,
        ushort manufacturer = Manufacturer.Garmin,
        ushort product = 1,
        string? productName = "Edge",
        IReadOnlyList<int>? recordOffsetsSeconds = null,
        IReadOnlyList<(int OffsetSeconds, EventType EventType)>? timerEventOffsetsSeconds = null,
        IReadOnlyList<ushort>? powersWatts = null,
        float totalTimerSeconds = 1)
    {
        var stream = new MemoryStream();
        var start = new Dynastream.Fit.DateTime(startedAt.UtcDateTime);
        var encoder = new Encode(ProtocolVersion.V20);
        encoder.Open(stream);

        var fileId = new FileIdMesg();
        fileId.SetType(Dynastream.Fit.File.Activity);
        fileId.SetManufacturer(manufacturer);
        fileId.SetProduct(product);
        if (!string.IsNullOrWhiteSpace(productName))
        {
            fileId.SetProductName(productName);
        }

        fileId.SetSerialNumber(1);
        fileId.SetTimeCreated(start);
        encoder.Write(fileId);

        var offsets = recordOffsetsSeconds ?? [0, 2];
        var powers = powersWatts ?? [220, 0];
        var events = timerEventOffsetsSeconds ?? [(0, EventType.Start), (1, EventType.Stop)];
        var timeline = events
            .Select(item => (OffsetSeconds: item.OffsetSeconds, Order: 0, Message: (Mesg)TimerEvent(new Dynastream.Fit.DateTime(start.GetTimeStamp() + (uint)item.OffsetSeconds), item.EventType)))
            .Concat(offsets.Select((offsetSeconds, index) => (OffsetSeconds: offsetSeconds, Order: 1, Message: (Mesg)Record(new Dynastream.Fit.DateTime(start.GetTimeStamp() + (uint)offsetSeconds), powers[index]))))
            .OrderBy(item => item.OffsetSeconds)
            .ThenBy(item => item.Order);
        foreach (var item in timeline)
        {
            encoder.Write(item.Message);
        }

        var session = new SessionMesg();
        session.SetSport(Sport.Cycling);
        session.SetStartTime(start);
        if (includeSessionTimestamp)
        {
            session.SetTimestamp(new Dynastream.Fit.DateTime(endedAt.UtcDateTime));
        }

        session.SetTotalTimerTime(totalTimerSeconds);
        if (totalDistanceMetres.HasValue)
        {
            session.SetTotalDistance((float)totalDistanceMetres.Value);
        }

        if (totalAscentMetres.HasValue)
        {
            session.SetTotalAscent(totalAscentMetres.Value);
        }

        encoder.Write(session);
        encoder.Close();
        stream.Position = 0;
        return stream;
    }

    private static EventMesg TimerEvent(Dynastream.Fit.DateTime timestamp, EventType eventType)
    {
        var message = new EventMesg();
        message.SetTimestamp(timestamp);
        message.SetEvent(Event.Timer);
        message.SetEventType(eventType);
        return message;
    }

    private static RecordMesg Record(Dynastream.Fit.DateTime timestamp, ushort power)
    {
        var message = new RecordMesg();
        message.SetTimestamp(timestamp);
        message.SetPositionLat(608_920_000);
        message.SetPositionLong(-11_932_000);
        message.SetEnhancedAltitude(100);
        message.SetEnhancedSpeed(8);
        message.SetPower(power);
        return message;
    }
}
