using DevicePulse.Api.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DevicePulse.Api.Data.Configurations;

public sealed class DeviceConfiguration : IEntityTypeConfiguration<Device>
{
    public void Configure(EntityTypeBuilder<Device> builder)
    {
        builder.HasKey(d => d.DeviceId);
        builder.Property(d => d.DeviceCode).HasMaxLength(50).IsRequired();
        builder.Property(d => d.DeviceName).HasMaxLength(200).IsRequired();
        builder.Property(d => d.ApiKeyHash).HasMaxLength(128);

        // Enums are stored as strings. A stored int is unreadable in a SQL window and breaks
        // silently if an enum member is ever reordered; the few extra bytes are worth it.
        builder.Property(d => d.LifecycleStatus).HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(d => d.ConnectivityStatus).HasConversion<string>().HasMaxLength(30).IsRequired();

        builder.Property(d => d.RowVersion).IsRowVersion();

        builder.HasIndex(d => d.DeviceCode).IsUnique();

        // These three back the actual query patterns the dashboard and device list use
        // (§35: indexes driven by real queries, not guesses).
        builder.HasIndex(d => d.ConnectivityStatus);
        builder.HasIndex(d => d.LifecycleStatus);
        builder.HasIndex(d => d.LocationId);

        // Restrict: a device type or location that is still in use cannot be deleted out from
        // under live devices. Deactivating it is the supported operation instead.
        builder.HasOne(d => d.DeviceType).WithMany(t => t.Devices)
            .HasForeignKey(d => d.DeviceTypeId).OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(d => d.Location).WithMany(l => l.Devices)
            .HasForeignKey(d => d.LocationId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class DeviceTypeConfiguration : IEntityTypeConfiguration<DeviceType>
{
    public void Configure(EntityTypeBuilder<DeviceType> builder)
    {
        builder.HasKey(t => t.DeviceTypeId);
        builder.Property(t => t.Name).HasMaxLength(100).IsRequired();
        builder.Property(t => t.Description).HasMaxLength(500);
        builder.HasIndex(t => t.Name).IsUnique();
    }
}

public sealed class LocationConfiguration : IEntityTypeConfiguration<Location>
{
    public void Configure(EntityTypeBuilder<Location> builder)
    {
        builder.HasKey(l => l.LocationId);
        builder.Property(l => l.Name).HasMaxLength(150).IsRequired();
        builder.Property(l => l.Description).HasMaxLength(500);
        builder.HasIndex(l => l.Name).IsUnique();
    }
}

public sealed class TelemetryConfiguration : IEntityTypeConfiguration<Telemetry>
{
    public void Configure(EntityTypeBuilder<Telemetry> builder)
    {
        builder.HasKey(t => t.TelemetryId);
        builder.Property(t => t.MessageId).HasMaxLength(100);

        builder.HasOne(t => t.Device).WithMany(d => d.Telemetries)
            .HasForeignKey(t => t.DeviceId).OnDelete(DeleteBehavior.Cascade);

        // The dominant query is "readings for device X, newest first" — descending on
        // RecordedAt so the index can satisfy the sort without a separate sort step.
        builder.HasIndex(t => new { t.DeviceId, t.RecordedAt })
            .IsDescending(false, true)
            .HasDatabaseName("IX_Telemetry_DeviceId_RecordedAt");

        // Retention sweeps scan by age across all devices.
        builder.HasIndex(t => t.RecordedAt);

        // Idempotency (Appendix D.1). Filtered so the many rows with no MessageId don't all
        // collide on NULL — a plain unique index would reject the second null-keyed reading.
        builder.HasIndex(t => new { t.DeviceId, t.MessageId })
            .IsUnique()
            .HasFilter("[MessageId] IS NOT NULL")
            .HasDatabaseName("UX_Telemetry_DeviceId_MessageId");
    }
}
