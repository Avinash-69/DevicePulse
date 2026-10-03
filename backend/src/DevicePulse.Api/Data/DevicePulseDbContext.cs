using DevicePulse.Api.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace DevicePulse.Api.Data;

public class DevicePulseDbContext : DbContext
{
    public DevicePulseDbContext(DbContextOptions<DevicePulseDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<Device> Devices => Set<Device>();
    public DbSet<DeviceType> DeviceTypes => Set<DeviceType>();
    public DbSet<Location> Locations => Set<Location>();
    public DbSet<Telemetry> Telemetry => Set<Telemetry>();

    public DbSet<AlertRule> AlertRules => Set<AlertRule>();
    public DbSet<Alert> Alerts => Set<Alert>();

    public DbSet<SystemSetting> SystemSettings => Set<SystemSetting>();
    public DbSet<SettingHistory> SettingHistory => Set<SettingHistory>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<ServiceLog> ServiceLogs => Set<ServiceLog>();

    /// <summary>
    /// Every DateTime in this model is UTC. SQL Server's datetime2 stores no offset, so a value
    /// read back arrives with Kind=Unspecified — which serialises without a trailing "Z" and
    /// leaves the Angular client to guess the zone. This converter re-stamps reads as UTC and
    /// normalises writes, so a timestamp means the same thing on both sides of the wire.
    /// </summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);

        configurationBuilder.Properties<DateTime>()
            .HaveConversion<UtcDateTimeConverter>();

        configurationBuilder.Properties<DateTime?>()
            .HaveConversion<NullableUtcDateTimeConverter>();
    }

    private sealed class UtcDateTimeConverter : ValueConverter<DateTime, DateTime>
    {
        public UtcDateTimeConverter() : base(
            write => write.Kind == DateTimeKind.Utc ? write : write.ToUniversalTime(),
            read => DateTime.SpecifyKind(read, DateTimeKind.Utc))
        { }
    }

    private sealed class NullableUtcDateTimeConverter : ValueConverter<DateTime?, DateTime?>
    {
        public NullableUtcDateTimeConverter() : base(
            write => write == null
                ? null
                : write.Value.Kind == DateTimeKind.Utc ? write : write.Value.ToUniversalTime(),
            read => read == null
                ? null
                : DateTime.SpecifyKind(read.Value, DateTimeKind.Utc))
        { }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Entity configuration lives in IEntityTypeConfiguration classes in Data/Configurations
        // rather than one thousand-line OnModelCreating — the same reasoning as splitting
        // controllers per resource.
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(DevicePulseDbContext).Assembly);
    }
}
