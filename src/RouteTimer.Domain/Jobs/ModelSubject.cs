namespace RouteTimer.Domain.Jobs;

/// <summary>
/// RouteTimer supports exactly one rider, so BuildModel jobs have no natural per-entity subject the
/// way ParseTraining (an upload id) or PredictRoute (a GPX upload id) do. This well-known id stands in
/// for "the rider's model" so BuildModel jobs can still use AnalysisJob's (Type, SubjectId) shape.
/// </summary>
public static class ModelSubject
{
    public static readonly Guid Id = Guid.Empty;
}
