using Microsoft.EntityFrameworkCore;
using MiniIdp.Models;

namespace MiniIdp.Data;

public class PlatformDbContext(DbContextOptions<PlatformDbContext> options) : DbContext(options)
{
    public DbSet<ServiceRecord> Services => Set<ServiceRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var service = modelBuilder.Entity<ServiceRecord>();

        // The API checks for duplicates first, but the unique index is the real guarantee:
        // two simultaneous requests for the same name cannot both be inserted.
        service.HasIndex(s => s.Name).IsUnique();
        service.Property(s => s.Name).HasMaxLength(30);

        // Store the status as readable text ("Running") instead of a number.
        service.Property(s => s.Status).HasConversion<string>();

        // SQLite has no time-zone-aware type. We always write UTC, so mark values read back as UTC;
        // otherwise the JSON would lack the "Z" suffix and browsers would treat times as local.
        service.Property(s => s.CreatedAt)
            .HasConversion(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
        service.Property(s => s.UpdatedAt)
            .HasConversion(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
    }
}
