using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using RouteTimer.Persistence.Entities;

namespace RouteTimer.Persistence;

public sealed class RouteTimerDbContext(DbContextOptions<RouteTimerDbContext> options) : DbContext(options)
{
    public DbSet<PredictionEntity> Predictions => Set<PredictionEntity>();
    public DbSet<PredictionSegmentEntity> PredictionSegments => Set<PredictionSegmentEntity>();
    public DbSet<AnalysisJobEntity> Jobs => Set<AnalysisJobEntity>();
    public DbSet<RiderProfileEntity> Profiles => Set<RiderProfileEntity>();
    public DbSet<StoredUploadEntity> Uploads => Set<StoredUploadEntity>();
    public DbSet<TrainingActivityEntity> TrainingActivities => Set<TrainingActivityEntity>();
    public DbSet<ActivitySampleEntity> ActivitySamples => Set<ActivitySampleEntity>();
    public DbSet<RiderModelEntity> RiderModels => Set<RiderModelEntity>();
    public DbSet<PowerBandEntity> PowerBands => Set<PowerBandEntity>();
    public DbSet<RiderModelDescentLimitEntity> RiderModelDescentLimits => Set<RiderModelDescentLimitEntity>();
    public DbSet<LocalCredentialEntity> LocalCredentials => Set<LocalCredentialEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var prediction = modelBuilder.Entity<PredictionEntity>();
        prediction.ToTable("predictions");
        prediction.HasKey(entity => entity.Id);
        prediction.Property(entity => entity.ModelVersion).HasMaxLength(128).IsRequired();
        prediction.Property(entity => entity.ModelValidationStatus).HasMaxLength(32).IsRequired();
        prediction.Property(entity => entity.AssumptionSurface).HasMaxLength(32).IsRequired();
        prediction.Property(entity => entity.AssumptionWind).HasMaxLength(32).IsRequired();
        prediction.Property(entity => entity.AssumptionWeather).HasMaxLength(32).IsRequired();
        prediction.Property(entity => entity.State).HasMaxLength(32).IsRequired();
        prediction.Property(entity => entity.Confidence).HasMaxLength(32);
        prediction.Property(entity => entity.Warnings)
            .HasConversion(
                warnings => JsonSerializer.Serialize(warnings, JsonOptions),
                json => JsonSerializer.Deserialize<List<string>>(json, JsonOptions) ?? new List<string>())
            .HasColumnType("jsonb")
            .Metadata.SetValueComparer(ListComparer);
        prediction.Property(entity => entity.CreatedAt).HasColumnType("timestamp with time zone");
        prediction.Property(entity => entity.CompletedAt).HasColumnType("timestamp with time zone");
        prediction.HasIndex(entity => entity.CreatedAt);
        prediction.HasIndex(entity => entity.UploadId);
        prediction.HasIndex(entity => entity.RiderModelId);

        var predictionSegment = modelBuilder.Entity<PredictionSegmentEntity>();
        predictionSegment.ToTable("prediction_segments");
        predictionSegment.HasKey(entity => new { entity.PredictionId, entity.Sequence });
        predictionSegment.Property(entity => entity.Confidence).HasMaxLength(32).IsRequired();

        prediction.HasMany(entity => entity.Segments)
            .WithOne()
            .HasForeignKey(entity => entity.PredictionId)
            .OnDelete(DeleteBehavior.Cascade);

        var job = modelBuilder.Entity<AnalysisJobEntity>();
        job.ToTable("analysis_jobs", table => table.HasCheckConstraint("CK_analysis_jobs_progress", "\"ProgressPercent\" BETWEEN 0 AND 100"));
        job.HasKey(entity => entity.Id);
        job.Property(entity => entity.Type).HasMaxLength(64).IsRequired();
        job.Property(entity => entity.State).HasMaxLength(32).IsRequired();
        job.Property(entity => entity.ProgressStage).HasMaxLength(64).IsRequired();
        job.Property(entity => entity.StartedAt).HasColumnType("timestamp with time zone");
        job.Property(entity => entity.UpdatedAt).HasColumnType("timestamp with time zone");
        job.Property(entity => entity.CompletedAt).HasColumnType("timestamp with time zone");
        job.Property(entity => entity.WorkerId).HasMaxLength(128);
        job.Property(entity => entity.LeaseExpiresAt).HasColumnType("timestamp with time zone");
        job.Property(entity => entity.CreatedAt).HasColumnType("timestamp with time zone");
        job.Property(entity => entity.DiagnosticCode).HasMaxLength(128);
        job.Property(entity => entity.DiagnosticMessage).HasMaxLength(1024);
        job.HasIndex(entity => new { entity.State, entity.LeaseExpiresAt, entity.CreatedAt });

        // Backs EnqueueIfNotPendingAsync's queued-job coalescing and ClaimAsync's single-running-job
        // lease ownership: the database separately enforces at most one Queued row and at most one
        // Running row for a given (Type, SubjectId) pair, which allows a follow-up queued job to exist
        // behind an in-flight running job without letting either state duplicate itself.
        job.HasIndex(
                [nameof(AnalysisJobEntity.Type), nameof(AnalysisJobEntity.SubjectId)],
                "IX_analysis_jobs_queued_type_subject")
            .IsUnique()
            .HasFilter("\"State\" = 'Queued'");
        job.HasIndex(
                [nameof(AnalysisJobEntity.Type), nameof(AnalysisJobEntity.SubjectId)],
                "IX_analysis_jobs_running_type_subject")
            .IsUnique()
            .HasFilter("\"State\" = 'Running'");

        var profile = modelBuilder.Entity<RiderProfileEntity>();
        profile.ToTable("rider_profile");
        profile.HasKey(entity => entity.Id);
        profile.Property(entity => entity.UpdatedAt).HasColumnType("timestamp with time zone");

        var upload = modelBuilder.Entity<StoredUploadEntity>();
        upload.ToTable("stored_uploads");
        upload.HasKey(entity => entity.Id);
        upload.Property(entity => entity.Kind).HasMaxLength(32).IsRequired();
        upload.Property(entity => entity.FileName).HasMaxLength(512).IsRequired();
        upload.Property(entity => entity.Content).HasColumnType("bytea").IsRequired();
        upload.Property(entity => entity.Sha256).HasColumnType("bytea").HasMaxLength(32).IsRequired();
        upload.Property(entity => entity.CreatedAt).HasColumnType("timestamp with time zone");
        upload.HasIndex(entity => new { entity.Kind, entity.Sha256 }).IsUnique();

        prediction.HasOne(entity => entity.Upload)
            .WithMany()
            .HasForeignKey(entity => entity.UploadId)
            .OnDelete(DeleteBehavior.Restrict);

        var activity = modelBuilder.Entity<TrainingActivityEntity>();
        activity.ToTable("training_activities");
        activity.HasKey(entity => entity.Id);
        activity.Property(entity => entity.UploadId).IsRequired();
        activity.Property(entity => entity.Name).HasMaxLength(512).IsRequired();
        activity.Property(entity => entity.SourceFileName).HasMaxLength(512).IsRequired();
        activity.Property(entity => entity.StartedAt).HasColumnType("timestamp with time zone");
        activity.Property(entity => entity.EndedAt).HasColumnType("timestamp with time zone");
        activity.Property(entity => entity.DeviceManufacturer).HasMaxLength(128);
        activity.Property(entity => entity.DeviceProduct).HasMaxLength(128);
        activity.Property(entity => entity.Eligibility).HasMaxLength(32).IsRequired();
        activity.Property(entity => entity.CreatedAt).HasColumnType("timestamp with time zone");
        activity.Property(entity => entity.ExclusionCounts)
            .HasConversion(
                counts => JsonSerializer.Serialize(counts, JsonOptions),
                json => JsonSerializer.Deserialize<Dictionary<string, int>>(json, JsonOptions) ?? new Dictionary<string, int>())
            .HasColumnType("jsonb")
            .Metadata.SetValueComparer(DictionaryComparer);
        activity.Property(entity => entity.ReasonCodes)
            .HasConversion(
                codes => JsonSerializer.Serialize(codes, JsonOptions),
                json => JsonSerializer.Deserialize<List<string>>(json, JsonOptions) ?? new List<string>())
            .HasColumnType("jsonb")
            .Metadata.SetValueComparer(ListComparer);
        activity.HasIndex(entity => entity.UploadId);

        var sample = modelBuilder.Entity<ActivitySampleEntity>();
        sample.ToTable("activity_samples");
        sample.HasKey(entity => new { entity.ActivityId, entity.Sequence });
        sample.Property(entity => entity.Timestamp).HasColumnType("timestamp with time zone");
        sample.Property(entity => entity.CurvaturePerMetre).HasDefaultValue(0d);

        activity.HasMany(entity => entity.Samples)
            .WithOne()
            .HasForeignKey(entity => entity.ActivityId)
            .OnDelete(DeleteBehavior.Cascade);

        var riderModel = modelBuilder.Entity<RiderModelEntity>();
        riderModel.ToTable("rider_models");
        riderModel.HasKey(entity => entity.Id);
        riderModel.Property(entity => entity.CreatedAt).HasColumnType("timestamp with time zone");
        riderModel.Property(entity => entity.AlgorithmVersion).HasMaxLength(128).IsRequired();
        riderModel.Property(entity => entity.ValidationStatus).HasMaxLength(32).IsRequired();
        riderModel.Property(entity => entity.DescentWasLearned).HasDefaultValue(false);
        riderModel.HasIndex(entity => entity.CreatedAt);

        prediction.HasOne(entity => entity.RiderModel)
            .WithMany()
            .HasForeignKey(entity => entity.RiderModelId)
            .OnDelete(DeleteBehavior.Restrict);

        var powerBand = modelBuilder.Entity<PowerBandEntity>();
        powerBand.ToTable("power_bands");
        powerBand.HasKey(entity => new { entity.ModelId, entity.GradeKey, entity.DurationKey });
        powerBand.Property(entity => entity.GradeKey).HasMaxLength(32).IsRequired();
        powerBand.Property(entity => entity.DurationKey).HasMaxLength(32).IsRequired();
        powerBand.Property(entity => entity.Confidence).HasMaxLength(32).IsRequired();

        riderModel.HasMany(entity => entity.Bands)
            .WithOne()
            .HasForeignKey(entity => entity.ModelId)
            .OnDelete(DeleteBehavior.Cascade);

        var descentLimit = modelBuilder.Entity<RiderModelDescentLimitEntity>();
        descentLimit.ToTable("rider_model_descent_limits");
        descentLimit.HasKey(entity => new { entity.ModelId, entity.GradeKey, entity.CurvatureKey });
        descentLimit.Property(entity => entity.GradeKey).HasMaxLength(32).IsRequired();
        descentLimit.Property(entity => entity.CurvatureKey).HasMaxLength(32).IsRequired();
        descentLimit.Property(entity => entity.Confidence).HasMaxLength(32).IsRequired();

        riderModel.HasMany(entity => entity.DescentLimits)
            .WithOne(entity => entity.Model)
            .HasForeignKey(entity => entity.ModelId)
            .OnDelete(DeleteBehavior.Cascade);

        var localCredential = modelBuilder.Entity<LocalCredentialEntity>();
        localCredential.ToTable("local_credential", table => table.HasCheckConstraint(
            "CK_local_credential_singleton", "\"Id\" = 1"));
        localCredential.HasKey(entity => entity.Id);
        localCredential.Property(entity => entity.Id).ValueGeneratedNever();
        localCredential.Property(entity => entity.PasswordHash).HasMaxLength(256).IsRequired();
        localCredential.Property(entity => entity.CreatedAt).HasColumnType("timestamp with time zone");
        localCredential.Property(entity => entity.UpdatedAt).HasColumnType("timestamp with time zone");
    }

    private static readonly JsonSerializerOptions JsonOptions = new();

    private static readonly ValueComparer<IReadOnlyDictionary<string, int>> DictionaryComparer = new(
        (left, right) => DictionaryEquals(left ?? new Dictionary<string, int>(), right ?? new Dictionary<string, int>()),
        dictionary => dictionary.Aggregate(0, (hash, pair) => hash ^ HashCode.Combine(pair.Key, pair.Value)),
        dictionary => dictionary.ToDictionary(pair => pair.Key, pair => pair.Value));

    private static bool DictionaryEquals(IReadOnlyDictionary<string, int> left, IReadOnlyDictionary<string, int> right) =>
        left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var value) && value == pair.Value);

    private static readonly ValueComparer<IReadOnlyList<string>> ListComparer = new(
        (left, right) => (left ?? new List<string>()).SequenceEqual(right ?? new List<string>()),
        list => list.Aggregate(0, (hash, value) => HashCode.Combine(hash, value)),
        list => list.ToList());
}
