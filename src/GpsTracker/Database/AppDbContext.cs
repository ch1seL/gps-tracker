using GpsTracker.Models;
using Microsoft.EntityFrameworkCore;

namespace GpsTracker.Database;

public class AppDbContext : DbContext
{
    public DbSet<GpsPoint> GpsPoints => Set<GpsPoint>();

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<GpsPoint>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.Imei).IsRequired().HasMaxLength(20);
            entity.Property(e => e.Latitude).IsRequired();
            entity.Property(e => e.Longitude).IsRequired();
            entity.Property(e => e.Speed).HasDefaultValue(0);
            entity.Property(e => e.Course).HasDefaultValue(0);
            entity.Property(e => e.Satellites).HasDefaultValue(0);
            entity.Property(e => e.Timestamp).IsRequired();
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasIndex(e => e.Imei);
            entity.HasIndex(e => e.Timestamp);
        });
    }
}
