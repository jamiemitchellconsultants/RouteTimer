using Dynastream.Fit;

namespace RouteTimer.Services.Tests.Activities;

internal static class FitTestFileBuilder
{
    public static MemoryStream ActivityWithPause()
    {
        var stream = new MemoryStream();
        var start = new Dynastream.Fit.DateTime(new System.DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc));
        var encoder = new Encode(ProtocolVersion.V20);
        encoder.Open(stream);

        var fileId = new FileIdMesg();
        fileId.SetType(Dynastream.Fit.File.Activity);
        fileId.SetManufacturer(Manufacturer.Development);
        fileId.SetProduct(1);
        fileId.SetSerialNumber(1);
        fileId.SetTimeCreated(start);
        encoder.Write(fileId);

        encoder.Write(TimerEvent(start, EventType.Start));
        encoder.Write(Record(start, 220));
        encoder.Write(TimerEvent(new Dynastream.Fit.DateTime(start.GetTimeStamp() + 1), EventType.Stop));
        encoder.Write(Record(new Dynastream.Fit.DateTime(start.GetTimeStamp() + 2), 0));

        var session = new SessionMesg();
        session.SetSport(Sport.Cycling);
        session.SetStartTime(start);
        session.SetTimestamp(new Dynastream.Fit.DateTime(start.GetTimeStamp() + 2));
        session.SetTotalTimerTime(1);
        session.SetTotalDistance(10);
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
