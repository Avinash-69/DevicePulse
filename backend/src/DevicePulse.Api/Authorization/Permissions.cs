namespace DevicePulse.Api.Authorization;

/// <summary>
/// The permission catalog. This is developer-controlled by design (Appendix C item 4): new
/// permission *keys* appear here as features are built and are seeded into the Permissions
/// table at startup. What a Super Admin manages at runtime is the *assignment* of these keys
/// to roles — the UI never invents new keys, because a key with no enforcement behind it is a lie.
/// </summary>
public static class Permissions
{
    public const string DeviceView = "device.view";
    public const string DeviceCreate = "device.create";
    public const string DeviceUpdate = "device.update";
    public const string DeviceRetire = "device.retire";
    public const string DeviceManageCredentials = "device.credentials.manage";

    public const string TelemetryView = "telemetry.view";
    public const string TelemetryIngest = "telemetry.ingest";

    public const string AlertView = "alerts.view";
    public const string AlertResolve = "alerts.resolve";
    public const string AlertManage = "alerts.manage";
    public const string AlertRuleView = "rules.view";
    public const string AlertRuleManage = "rules.manage";

    public const string SettingsView = "settings.view";
    public const string SettingsManage = "settings.manage";

    public const string UserView = "user.view";
    public const string UserCreate = "user.create";
    public const string UserUpdate = "user.update";
    public const string UserDisable = "user.disable";

    public const string RoleView = "role.view";
    public const string RoleManage = "role.manage";
    public const string PermissionView = "permission.view";
    public const string PermissionManage = "permission.manage";

    public const string ReferenceDataView = "referencedata.view";
    public const string ReferenceDataManage = "referencedata.manage";

    public const string AuditView = "audit.view";
    public const string DashboardView = "dashboard.view";
    public const string SimulatorView = "simulator.view";
    public const string SimulatorManage = "simulator.manage";

    public sealed record Definition(string Key, string Name, string Category, string Description);

    /// <summary>Single source of truth for both policy registration and database seeding.</summary>
    public static readonly IReadOnlyList<Definition> All =
    [
        new(DeviceView, "View devices", "Devices", "See the device list and device details."),
        new(DeviceCreate, "Register devices", "Devices", "Register a new device."),
        new(DeviceUpdate, "Edit devices", "Devices", "Change a device's name, type, location or lifecycle status."),
        new(DeviceRetire, "Retire devices", "Devices", "Decommission a device without destroying its history."),
        new(DeviceManageCredentials, "Manage device credentials", "Devices", "Issue or revoke a device's ingestion API key."),

        new(TelemetryView, "View telemetry", "Telemetry", "Query current and historical device readings."),
        new(TelemetryIngest, "Ingest telemetry", "Telemetry", "Submit readings on behalf of a device."),

        new(AlertView, "View alerts", "Alerts", "See open and historical alerts."),
        new(AlertResolve, "Resolve alerts", "Alerts", "Acknowledge and resolve alerts."),
        new(AlertManage, "Manage alerts", "Alerts", "Full alert administration."),
        new(AlertRuleView, "View alert rules", "Alerts", "See the configured alert rules."),
        new(AlertRuleManage, "Manage alert rules", "Alerts", "Create, edit, enable and disable alert rules."),

        new(SettingsView, "View settings", "Configuration", "Read runtime business settings."),
        new(SettingsManage, "Manage settings", "Configuration", "Change runtime business settings and view their history."),

        new(UserView, "View users", "Administration", "See the user list."),
        new(UserCreate, "Create users", "Administration", "Create new user accounts."),
        new(UserUpdate, "Edit users", "Administration", "Change user details and role assignments."),
        new(UserDisable, "Enable/disable users", "Administration", "Activate or deactivate a user account."),

        new(RoleView, "View roles", "Administration", "See the configured roles."),
        new(RoleManage, "Manage roles", "Administration", "Create and edit roles."),
        new(PermissionView, "View permissions", "Administration", "See the permission catalog."),
        new(PermissionManage, "Assign permissions", "Administration", "Attach and detach permissions on a role."),

        new(ReferenceDataView, "View reference data", "Configuration", "See device types and locations."),
        new(ReferenceDataManage, "Manage reference data", "Configuration", "Create and edit device types and locations."),

        new(AuditView, "View audit log", "Administration", "Read the administrative audit trail."),
        new(DashboardView, "View dashboard", "Dashboard", "See the monitoring dashboard."),
        new(SimulatorView, "View simulator", "Simulator", "See the simulator configuration and status."),
        new(SimulatorManage, "Control simulator", "Simulator", "Start, stop and reconfigure the IoT simulator.")
    ];
}

/// <summary>
/// Seeded role names. These exist as constants only so seeding and tests can refer to them
/// without magic strings — authorization itself never checks a role name, only permissions.
/// </summary>
public static class SystemRoles
{
    public const string SuperAdmin = "SuperAdmin";
    public const string Admin = "Admin";
    public const string Operator = "Operator";
    public const string Viewer = "Viewer";
}
