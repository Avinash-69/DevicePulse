using DevicePulse.Api.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DevicePulse.Api.Data.Configurations;

public sealed class AlertRuleConfiguration : IEntityTypeConfiguration<AlertRule>
{
    public void Configure(EntityTypeBuilder<AlertRule> builder)
    {
        builder.HasKey(r => r.AlertRuleId);
        builder.Property(r => r.Name).HasMaxLength(150).IsRequired();
        builder.Property(r => r.Description).HasMaxLength(500);

        builder.Property(r => r.Metric).HasConversion<string>().HasMaxLength(40).IsRequired();
        builder.Property(r => r.Operator).HasConversion<string>().HasMaxLength(30).IsRequired();

        // Severity is stored as its numeric value, unlike the enums above. Severity is the one
        // that has a meaningful *order* -- Critical outranks High outranks Medium -- and a string
        // column would order it alphabetically, putting "Medium" above "High". The API still
        // exposes the names, because JsonStringEnumConverter is configured globally.
        builder.Property(r => r.Severity).HasConversion<int>().IsRequired();

        builder.Property(r => r.RowVersion).IsRowVersion();

        builder.HasIndex(r => r.Name).IsUnique();

        // The evaluator loads enabled rules on every ingest, so that filter is indexed.
        builder.HasIndex(r => r.IsEnabled);

        builder.HasOne(r => r.DeviceType).WithMany()
            .HasForeignKey(r => r.DeviceTypeId).OnDelete(DeleteBehavior.SetNull);
    }
}

public sealed class AlertConfiguration : IEntityTypeConfiguration<Alert>
{
    public void Configure(EntityTypeBuilder<Alert> builder)
    {
        builder.HasKey(a => a.AlertId);
        builder.Property(a => a.Message).HasMaxLength(500).IsRequired();
        builder.Property(a => a.ResolutionNote).HasMaxLength(1000);

        builder.Property(a => a.Metric).HasConversion<string>().HasMaxLength(40).IsRequired();
        builder.Property(a => a.Status).HasConversion<string>().HasMaxLength(20).IsRequired();

        // Numeric for the same reason as AlertRule.Severity: the alert list is ordered
        // worst-first, and that ordering has to happen in SQL because it drives which rows land
        // on which page. Sorting a string column would rank Medium above High.
        builder.Property(a => a.Severity).HasConversion<int>().IsRequired();

        builder.HasOne(a => a.Device).WithMany(d => d.Alerts)
            .HasForeignKey(a => a.DeviceId).OnDelete(DeleteBehavior.Cascade);

        // SetNull, not Cascade: deleting a rule must not erase the alerts it raised.
        builder.HasOne(a => a.AlertRule).WithMany(r => r.Alerts)
            .HasForeignKey(a => a.AlertRuleId).OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(a => a.AcknowledgedByUser).WithMany()
            .HasForeignKey(a => a.AcknowledgedByUserId).OnDelete(DeleteBehavior.NoAction);

        builder.HasOne(a => a.ResolvedByUser).WithMany()
            .HasForeignKey(a => a.ResolvedByUserId).OnDelete(DeleteBehavior.NoAction);

        builder.HasIndex(a => a.Status);
        builder.HasIndex(a => new { a.DeviceId, a.Status });

        // Backs the default alert-list ordering (worst first, then newest).
        builder.HasIndex(a => new { a.Status, a.Severity, a.CreatedAt })
            .IsDescending(false, true, true);

        // Backs the cooldown check: "did this rule already fire for this device recently?"
        builder.HasIndex(a => new { a.DeviceId, a.AlertRuleId, a.CreatedAt });
    }
}
