using Dynastream.Fit;
using RouteTimer.Domain.Routes;
using RouteTimer.Services.Validation;

namespace RouteTimer.Services.Activities;

public sealed class FitActivityParser : IFitActivityParser
{
    private static readonly DateTimeOffset GarminEpoch = new(1989, 12, 31, 0, 0, 0, TimeSpan.Zero);

    public Task<ParsedFitActivity> ParseAsync(Stream input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var decoder = new Decode();
            if (!decoder.CheckIntegrity(input))
            {
                throw new ActivityInputException("integrity_failed", "The FIT upload failed its integrity check.");
            }

            input.Position = 0;
            var samples = new List<RawRideSample>();
            var timerRunning = false;
            FileIdMesg? fileId = null;
            SessionMesg? session = null;

            var broadcaster = new MesgBroadcaster();
            decoder.MesgEvent += broadcaster.OnMesg;
            decoder.MesgDefinitionEvent += broadcaster.OnMesgDefinition;
            broadcaster.FileIdMesgEvent += (_, args) => fileId = args.mesg as FileIdMesg;
            broadcaster.SessionMesgEvent += (_, args) => session = args.mesg as SessionMesg;
            broadcaster.EventMesgEvent += (_, args) =>
            {
                var eventMessage = args.mesg as EventMesg;
                if (eventMessage?.GetEvent() == Event.Timer)
                {
                    timerRunning = eventMessage.GetEventType() == EventType.Start;
                }
            };
            broadcaster.RecordMesgEvent += (_, args) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var record = args.mesg as RecordMesg;
                if (record is not null)
                {
                    samples.Add(ToSample(record, timerRunning));
                }
            };

            decoder.Read(input);

            if (fileId?.GetType() != Dynastream.Fit.File.Activity)
            {
                throw new ActivityInputException("not_activity", "The FIT upload is not an activity file.");
            }

            if (session?.GetSport() != Sport.Cycling)
            {
                throw new ActivityInputException("not_cycling", "The FIT activity is not a cycling session.");
            }

            if (samples.Count == 0)
            {
                throw new ActivityInputException("no_records", "The FIT activity contains no record messages.");
            }

            var startedAt = ToDateTimeOffset(session.GetStartTime());
            var endedAt = ResolveEndedAt(session, samples);
            if (endedAt < startedAt)
            {
                throw new ActivityInputException("invalid-session-time", "The FIT activity session end precedes its start.");
            }

            return Task.FromResult(new ParsedFitActivity(
                "Unnamed activity",
                ActivitySport.Cycling,
                startedAt,
                endedAt,
                fileId.GetManufacturer().ToString(),
                NormalizeText(fileId.GetProductNameAsString()) ?? fileId.GetProduct()?.ToString(),
                samples,
                TimeSpan.FromSeconds(session.GetTotalTimerTime() ?? 0),
                session.GetTotalDistance(),
                session.GetTotalAscent()));
        }
        catch (ActivityInputException)
        {
            throw;
        }
        catch (FitException exception)
        {
            throw new ActivityInputException("decode_failed", "The FIT upload could not be decoded.", exception);
        }
    }

    private static RawRideSample ToSample(RecordMesg record, bool timerRunning)
    {
        var timestamp = ToDateTimeOffset(record.GetTimestamp());
        var latitude = record.GetPositionLat();
        var longitude = record.GetPositionLong();
        GeoPoint? position = latitude.HasValue && longitude.HasValue
            ? new GeoPoint(latitude.Value * (180d / 2_147_483_648d), longitude.Value * (180d / 2_147_483_648d), record.GetEnhancedAltitude() ?? record.GetAltitude() ?? 0)
            : null;

        return new RawRideSample(
            timestamp,
            position,
            record.GetEnhancedSpeed() ?? record.GetSpeed(),
            record.GetPower(),
            record.GetHeartRate(),
            record.GetCadence(),
            timerRunning);
    }

    private static DateTimeOffset ResolveEndedAt(SessionMesg session, IReadOnlyList<RawRideSample> samples)
    {
        var sessionTimestamp = session.GetTimestamp();
        if (sessionTimestamp is not null && sessionTimestamp.GetTimeStamp() != 0)
        {
            return ToDateTimeOffset(sessionTimestamp);
        }

        return samples.MaxBy(sample => sample.Timestamp)?.Timestamp
            ?? throw new ActivityInputException("invalid-session-time", "The FIT activity session end could not be resolved.");
    }

    private static string? NormalizeText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DateTimeOffset ToDateTimeOffset(Dynastream.Fit.DateTime? timestamp)
    {
        if (timestamp is null || timestamp.GetTimeStamp() == 0)
        {
            throw new ActivityInputException("invalid_timestamp", "The FIT activity contains an invalid timestamp.");
        }

        return GarminEpoch.AddSeconds(timestamp.GetTimeStamp());
    }
}
