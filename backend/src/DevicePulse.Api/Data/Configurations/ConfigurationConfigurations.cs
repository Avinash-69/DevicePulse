using DevicePulse.Api.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DevicePulse.Api.Data.Configurations;

public sealed class SystemSettingConfiguration : IEntityTypeConfiguration<SystemSetting>
{
    public void Configure(EntityTypeBuilder<SystemSetting> builder)
    {
        builder.HasKey(s => s.SettingId);
        builder.Property(s => s.Key).HasMaxLength(150).IsRequired();
        builder.Property(s => s.Value).HasMaxLength(1000).IsRequired();
        builder.Property(s => s.Category).HasMaxLength(100).IsRequired();
        builder.Property(s => s.Description).HasMaxLength(500);
        builder.Property(s => s.Unit).HasMaxLength(50);
        builder.Property(s => s.AllowedValues).HasMaxLength(1000);
        builder.Property(s => s.ValueType).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(s => s.RowVersion).IsRowVersion();

        builder.HasIndex(s => s.Key).IsUnique();
        builder.HasIndex(s => s.Category);

        builder.HasOne(s => s.UpdatedByUser).WithMany()
            .HasForeignKey(s => s.UpdatedByUserId).OnDelete(DeleteBehavior.SetNull);
    }
}

public sealed class SettingHistoryConfiguration : IEntityTypeConfiguration<SettingHistory>
{
    public void Configure(EntityTypeBuilder<SettingHistory> builder)
    {
        builder.HasKey(h => h.SettingHistoryId);
        builder.Property(h => h.Key).HasMaxLength(150).IsRequired();
        builder.Property(h => h.OldValue).HasMaxLength(1000);
        builder.Property(h => h.NewValue).HasMaxLength(1000).IsRequired();
        builder.Property(h => h.ChangeReason).HasMaxLength(500);

        builder.HasOne(h => h.Setting).WithMany(s => s.History)
            .HasForeignKey(h => h.SettingId).OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(h => h.ChangedByUser).WithMany()
            .HasForeignKey(h => h.ChangedByUserId).OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(h => new { h.SettingId, h.Version });
    }
}

public sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.HasKey(a => a.AuditId);
        builder.Property(a => a.Action).HasMaxLength(100).IsRequired();
        builder.Property(a => a.EntityType).HasMaxLength(100).IsRequired();
        builder.Property(a => a.EntityId).HasMaxLength(100);
        builder.Property(a => a.UserEmail).HasMaxLength(256);
        builder.Property(a => a.IpAddress).HasMaxLength(64);
        builder.Property(a => a.CorrelationId).HasMaxLength(64);

        // Old/new values are serialised JSON of the changed fields only, never whole entities —
        // that keeps password hashes and tokens out of the audit trail by construction (§17, D.5).
        builder.Property(a => a.OldValue).HasMaxLength(4000);
        builder.Property(a => a.NewValue).HasMaxLength(4000);

        builder.HasOne(a => a.User).WithMany()
            .HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(a => a.Timestamp);
        builder.HasIndex(a => new { a.EntityType, a.EntityId });
        builder.HasIndex(a => a.UserId);
    }
}

public sealed class ServiceLogConfiguration : IEntityTypeConfiguration<ServiceLog>
{
    public void Configure(EntityTypeBuilder<ServiceLog> builder)
    {
        builder.HasKey(l => l.ServiceLogId);
        builder.Property(l => l.Level).HasMaxLength(20).IsRequired();
        builder.Property(l => l.Message).HasMaxLength(2000).IsRequired();
        builder.Property(l => l.ExceptionType).HasMaxLength(200);
        builder.Property(l => l.Source).HasMaxLength(300);
        builder.Property(l => l.RequestPath).HasMaxLength(500);
        builder.Property(l => l.RequestMethod).HasMaxLength(10);
        builder.Property(l => l.CorrelationId).HasMaxLength(64).IsRequired();

        builder.HasIndex(l => l.Timestamp);
        builder.HasIndex(l => l.CorrelationId);
    }
}
