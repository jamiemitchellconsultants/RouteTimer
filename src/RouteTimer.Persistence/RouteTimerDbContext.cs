using Microsoft.EntityFrameworkCore;
using RouteTimer.Persistence.Entities;

namespace RouteTimer.Persistence;

public sealed class RouteTimerDbContext(DbContextOptions<RouteTimerDbContext> options) : DbContext(options)
{
    public DbSet<PredictionEntity> Predictions => Set<PredictionEntity>();
    public DbSet<StoredUploadEntity> Uploads => Set<StoredUploadEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var prediction = modelBuilder.Entity<PredictionEntity>();
        prediction.ToTable("predictions");
        prediction.HasKey(entity => entity.Id);
        prediction.Property(entity => entity.ModelVersion).HasMaxLength(128).IsRequired();
        prediction.Property(entity => entity.CreatedAt).HasColumnType("timestamp with time zone");
        prediction.HasIndex(entity => entity.CreatedAt);

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
