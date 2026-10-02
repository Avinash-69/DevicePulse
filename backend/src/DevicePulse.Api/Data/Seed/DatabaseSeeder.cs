using DevicePulse.Api.Authorization;
using DevicePulse.Api.Entities;
using DevicePulse.Api.Entities.Enums;
using DevicePulse.Api.Options;
using DevicePulse.Api.Services.Configuration;
using DevicePulse.Api.Services.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DevicePulse.Api.Data.Seed;

/// <summary>
/// Brings a fresh database up to a usable state: the permission catalog, the four system roles,
/// one Super Admin, the runtime settings and a starter set of alert rules.
///
/// Written to be idempotent — it runs on every startup and only fills in what is missing. That
/// is what lets a newly-added permission or setting appear automatically on the next deploy
/// instead of needing a hand-written migration or a manual INSERT.
/// </summary>
public sealed class DatabaseSeeder
{
    private readonly DevicePulseDbContext _db;
    private readonly IPasswordHasher _passwordHasher;
    private readonly SeedOptions _options;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<DatabaseSeeder> _logger;

    public DatabaseSeeder(
        DevicePulseDbContext db,
        IPasswordHasher passwordHasher,
        IOptions<SeedOptions> options,
        IHostEnvironment environment,
        ILogger<DatabaseSeeder> logger)
    {
        _db = db;
        _passwordHasher = passwordHasher;
        _options = options.Value;
        _environment = environment;
        _logger = logger;
    }

    public async Task SeedAsync(CancellationToken ct = default)
    {
        await SeedPermissionsAsync(ct);
        await SeedRolesAsync(ct);
        await SeedSettingsAsync(ct);
        await SeedSuperAdminAsync(ct);
        await SeedAlertRulesAsync(ct);

        if (_options.SeedSampleData && _environment.IsDevelopment())
            await SeedSampleDataAsync(ct);
    }

    /// <summary>
    /// Reconciles the Permissions table against the compiled-in catalog. New keys are inserted
    /// and existing names/descriptions are refreshed; nothing is deleted, because a key that
    /// disappeared from code may still be referenced by a role row, and removing it silently
    /// would change what that role grants.
    /// </summary>
    private async Task SeedPermissionsAsync(CancellationToken ct)
    {
        var existing = await _db.Permissions.ToDictionaryAsync(p => p.Key, StringComparer.Ordinal, ct);
        var added = 0;

        foreach (var definition in Permissions.All)
        {
            if (existing.TryGetValue(definition.Key, out var permission))
            {
                permission.Name = definition.Name;
                permission.Description = definition.Description;
                permission.Category = definition.Category;
                continue;
            }

            _db.Permissions.Add(new Permission
            {
                Key = definition.Key,
                Name = definition.Name,
                Description = definition.Description,
                Category = definition.Category
            });

            added++;
        }

        if (await _db.SaveChangesAsync(ct) > 0 && added > 0)
            _logger.LogInformation("Seeded {Count} new permission(s) into the catalog.", added);
    }

    private async Task SeedRolesAsync(CancellationToken ct)
    {
        var permissionsByKey = await _db.Permissions.ToDictionaryAsync(p => p.Key, StringComparer.Ordinal, ct);

        // The Super Admin's permission set is the catalog itself, computed rather than listed,
        // so a permission added in code is never accidentally left ungranted.
        var allKeys = Permissions.All.Select(p => p.Key).ToArray();

        var adminKeys = allKeys
            .Except([
                // An Admin manages the platform but not the people who manage the platform.
                Permissions.RoleManage,
                Permissions.PermissionManage,
                Permissions.UserDisable
            ])
            .ToArray();

        string[] operatorKeys =
        [
            Permissions.DashboardView,
            Permissions.DeviceView,
            Permissions.TelemetryView,
            Permissions.AlertView,
            Permissions.AlertResolve,
            Permissions.AlertRuleView,
            Permissions.SettingsView,
            Permissions.ReferenceDataView,
            Permissions.SimulatorView
        ];

        string[] viewerKeys =
        [
            Permissions.DashboardView,
            Permissions.DeviceView,
            Permissions.TelemetryView,
            Permissions.AlertView
        ];

        await EnsureRoleAsync(SystemRoles.SuperAdmin,
            "Full administration of users, roles, permissions, configuration and devices.",
            allKeys, permissionsByKey, ct);

        await EnsureRoleAsync(SystemRoles.Admin,
            "Operational administration: devices, alert rules, settings and reference data.",
            adminKeys, permissionsByKey, ct);

        await EnsureRoleAsync(SystemRoles.Operator,
            "Monitors devices and telemetry, and acknowledges or resolves alerts.",
            operatorKeys, permissionsByKey, ct);

        await EnsureRoleAsync(SystemRoles.Viewer,
            "Read-only access to the dashboard, devices, telemetry and alerts.",
            viewerKeys, permissionsByKey, ct);

        await _db.SaveChangesAsync(ct);
    }

    private async Task EnsureRoleAsync(
        string name,
        string description,
        string[] permissionKeys,
        Dictionary<string, Permission> permissionsByKey,
        CancellationToken ct)
    {
        var role = await _db.Roles
            .Include(r => r.RolePermissions)
            .FirstOrDefaultAsync(r => r.Name == name, ct);

        if (role is null)
        {
            role = new Role
            {
                Name = name,
                Description = description,
                IsActive = true,
                IsSystemRole = true,
                CreatedAt = DateTime.UtcNow
            };

            _db.Roles.Add(role);
            _logger.LogInformation("Seeded system role {RoleName}.", name);
        }

        var currentIds = role.RolePermissions.Select(rp => rp.PermissionId).ToHashSet();

        // Only missing grants are added. An administrator who has deliberately removed a
        // permission from Admin, Operator or Viewer at runtime keeps that decision across
        // restarts — re-asserting the full set on every boot would silently undo their work.
        //
        // SuperAdmin is the exception: RoleService refuses to let it lose a permission, so
        // topping it up here simply keeps it consistent with a catalog that has grown.
        foreach (var key in permissionKeys)
        {
            if (!permissionsByKey.TryGetValue(key, out var permission))
                continue;

            if (currentIds.Contains(permission.PermissionId))
                continue;

            role.RolePermissions.Add(new RolePermission { PermissionId = permission.PermissionId });
        }
    }

    private async Task SeedSettingsAsync(CancellationToken ct)
    {
        var existing = await _db.SystemSettings.ToDictionaryAsync(s => s.Key, StringComparer.OrdinalIgnoreCase, ct);
        var added = 0;

        foreach (var definition in SettingKeys.All)
        {
            if (existing.TryGetValue(definition.Key, out var setting))
            {
                // The metadata (type, bounds, description, unit) is refreshed from code because
                // that is where it is authored. The *value* is never touched — overwriting an
                // operator's tuned threshold on every deploy would defeat the entire point of
                // runtime configuration.
                setting.ValueType = definition.ValueType;
                setting.Category = definition.Category;
                setting.Description = definition.Description;
                setting.Unit = definition.Unit;
                setting.MinValue = definition.MinValue;
                setting.MaxValue = definition.MaxValue;
                setting.AllowedValues = definition.AllowedValues;
                setting.IsEditable = definition.IsEditable;
                continue;
            }

            _db.SystemSettings.Add(new SystemSetting
            {
                Key = definition.Key,
                Value = definition.DefaultValue,
                ValueType = definition.ValueType,
                Category = definition.Category,
                Description = definition.Description,
                Unit = definition.Unit,
                MinValue = definition.MinValue,
                MaxValue = definition.MaxValue,
                AllowedValues = definition.AllowedValues,
                IsEditable = definition.IsEditable,
                Version = 1,
                CreatedAt = DateTime.UtcNow
            });

            added++;
        }

        await _db.SaveChangesAsync(ct);

        if (added > 0)
            _logger.LogInformation("Seeded {Count} new runtime setting(s).", added);
    }

    private async Task SeedSuperAdminAsync(CancellationToken ct)
    {
        var superAdminRole = await _db.Roles.FirstAsync(r => r.Name == SystemRoles.SuperAdmin, ct);

        var anySuperAdmin = await _db.UserRoles.AnyAsync(ur => ur.RoleId == superAdminRole.RoleId, ct);

        if (anySuperAdmin)
            return;

        var password = _options.SuperAdminPassword;

        if (string.IsNullOrWhiteSpace(password))
        {
            // Refusing to invent a password is the point. A hard-coded fallback would ship a
            // known administrator credential, and a generated one nobody records would leave
            // the deployment with an unreachable admin account.
            _logger.LogError(
                "No Super Admin exists and Seed:SuperAdminPassword is not configured. " +
                "Set it via user-secrets (development) or an environment variable, then restart.");
            return;
        }

        if (!_environment.IsDevelopment() && IsWeak(password))
        {
            _logger.LogError(
                "Seed:SuperAdminPassword is too weak for a {Environment} environment. The Super Admin was not created.",
                _environment.EnvironmentName);
            return;
        }

        var email = _options.SuperAdminEmail.Trim().ToLowerInvariant();
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);

        if (user is null)
        {
            user = new User
            {
                Name = _options.SuperAdminName,
                Email = email,
                PasswordHash = _passwordHasher.Hash(password),
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            };

            _db.Users.Add(user);
        }

        user.UserRoles.Add(new UserRole { RoleId = superAdminRole.RoleId });

        await _db.SaveChangesAsync(ct);

        _logger.LogWarning(
            "Seeded the initial Super Admin account {Email}. Change this password immediately after first sign-in.",
            email);
    }

    private static bool IsWeak(string password) =>
        password.Length < 12
        || !password.Any(char.IsUpper)
        || !password.Any(char.IsLower)
        || !password.Any(char.IsDigit);

    /// <summary>
    /// The three rules from §16 of the master reference, so a fresh install demonstrates the
    /// alerting pipeline immediately. Seeded once; an administrator is then free to edit,
    /// disable or delete them, and the seeder will not put them back.
    /// </summary>
    private async Task SeedAlertRulesAsync(CancellationToken ct)
    {
        if (await _db.AlertRules.AnyAsync(ct))
            return;

        _db.AlertRules.AddRange(
            new AlertRule
            {
                Name = "High temperature",
                Description = "Raises a High alert when a device reports above 40 °C.",
                Metric = AlertMetric.Temperature,
                Operator = AlertOperator.GreaterThan,
                Threshold = 40,
                Severity = AlertSeverity.High,
                IsEnabled = true,
                CooldownSeconds = 300,
                CreatedAt = DateTime.UtcNow
            },
            new AlertRule
            {
                Name = "Critical temperature",
                Description = "Raises a Critical alert when a device reports above 55 °C.",
                Metric = AlertMetric.Temperature,
                Operator = AlertOperator.GreaterThan,
                Threshold = 55,
                Severity = AlertSeverity.Critical,

                // A shorter cooldown than the High rule: a critical condition should keep
                // reasserting itself more often than a merely elevated one.
                IsEnabled = true,
                CooldownSeconds = 120,
                CreatedAt = DateTime.UtcNow
            },
            new AlertRule
            {
                Name = "Low battery",
                Description = "Raises a Medium alert when a device reports below 15% battery.",
                Metric = AlertMetric.Battery,
                Operator = AlertOperator.LessThan,
                Threshold = 15,
                Severity = AlertSeverity.Medium,
                IsEnabled = true,
                CooldownSeconds = 3600,
                CreatedAt = DateTime.UtcNow
            },
            new AlertRule
            {
                Name = "Weak signal",
                Description = "Raises a Low alert when signal strength drops below -100 dBm.",
                Metric = AlertMetric.SignalStrength,
                Operator = AlertOperator.LessThan,
                Threshold = -100,
                Severity = AlertSeverity.Low,
                IsEnabled = true,
                CooldownSeconds = 1800,
                CreatedAt = DateTime.UtcNow
            },
            new AlertRule
            {
                Name = "Device offline",
                Description = "Raises a High alert when a device has not reported for five minutes.",
                Metric = AlertMetric.LastSeenAgeSeconds,
                Operator = AlertOperator.GreaterThan,
                Threshold = 300,
                Severity = AlertSeverity.High,
                IsEnabled = true,
                CooldownSeconds = 900,
                CreatedAt = DateTime.UtcNow
            });

        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Seeded the default alert rules.");
    }

    /// <summary>
    /// Development-only sample fleet, so the dashboard and device list have something to show
    /// before the simulator is started. Gated on both the setting and the environment, because
    /// fabricated devices in a real deployment would be worse than an empty screen.
    /// </summary>
    private async Task SeedSampleDataAsync(CancellationToken ct)
    {
        if (await _db.Devices.AnyAsync(ct))
            return;

        var types = new[]
        {
            new DeviceType { Name = "Temperature Sensor", Description = "Ambient temperature and humidity probe.", IsActive = true },
            new DeviceType { Name = "Cold Chain Monitor", Description = "Refrigeration unit monitor.", IsActive = true },
            new DeviceType { Name = "Industrial Gateway", Description = "Edge gateway aggregating field sensors.", IsActive = true },
            new DeviceType { Name = "Asset Tracker", Description = "Battery-powered GPS asset tag.", IsActive = true }
        };

        var locations = new[]
        {
            new Location { Name = "Hyderabad — Plant 1", IsActive = true },
            new Location { Name = "Hyderabad — Warehouse", IsActive = true },
            new Location { Name = "Bengaluru — Data Centre", IsActive = true },
            new Location { Name = "Pune — Cold Storage", IsActive = true }
        };

        _db.DeviceTypes.AddRange(types);
        _db.Locations.AddRange(locations);
        await _db.SaveChangesAsync(ct);

        // Fixed seed so a reset database produces the same fleet — reproducibility matters when
        // comparing load-test runs (§19 mentions a randomisation seed for exactly this reason).
        var random = new Random(20261002);

        for (var i = 1; i <= 24; i++)
        {
            var type = types[random.Next(types.Length)];
            var location = locations[random.Next(locations.Length)];

            _db.Devices.Add(new Device
            {
                DeviceCode = $"DP-{i:D4}",
                DeviceName = $"{type.Name} {i:D2}",
                DeviceTypeId = type.DeviceTypeId,
                LocationId = location.LocationId,
                LifecycleStatus = LifecycleStatus.Active,

                // Unknown, not Online: no reading has arrived yet, and claiming Online would
                // make the dashboard lie until the first sweep corrected it.
                ConnectivityStatus = ConnectivityStatus.Unknown,
                CreatedAt = DateTime.UtcNow.AddDays(-random.Next(1, 90))
            });
        }

        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Seeded sample device types, locations and 24 devices for development.");
    }
}
