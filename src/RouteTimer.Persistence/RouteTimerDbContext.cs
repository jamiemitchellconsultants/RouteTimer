using Microsoft.EntityFrameworkCore;
using RouteTimer.Persistence.Entities;

namespace RouteTimer.Persistence;

public sealed class RouteTimerDbContext(DbContextOptions<RouteTimerDbContext> options) : DbContext(options)
{
    public DbSet<PredictionEntity> Predictions => Set<PredictionEntity>();
    public DbSet<AnalysisJobEntity> Jobs => Set<AnalysisJobEntity>();
    public DbSet<RiderProfileEntity> Profiles => Set<RiderProfileEntity>();
    public DbSet<StoredUploadEntity> Uploads => Set<StoredUploadEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var prediction = modelBuilder.Entity<PredictionEntity>();
        prediction.ToTable("predictions");
        prediction.HasKey(entity => entity.Id);
        prediction.Property(entity => entity.ModelVersion).HasMaxLength(128).IsRequired();
        prediction.Property(entity => entity.CreatedAt).HasColumnType("timestamp with time zone");
        prediction.HasIndex(entity => entity.CreatedAt);

        var job = modelBuilder.Entity<AnalysisJobEntity>();
        job.ToTable("analysis_jobs");
        job.HasKey(entity => entity.Id);
        job.Property(entity => entity.Type).HasMaxLength(64).IsRequired();
        job.Property(entity => entity.State).HasMaxLength(32).IsRequired();
        job.Property(entity => entity.WorkerId).HasMaxLength(128);
        job.Property(entity => entity.LeaseExpiresAt).HasColumnType("timestamp with time zone");
        job.Property(entity => entity.CreatedAt).HasColumnType("timestamp with time zone");
        job.HasIndex(entity => new { entity.State, entity.LeaseExpiresAt, entity.CreatedAt });

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
    }
}
