/**
 * TypeScript mirrors of the API's DTOs.
 *
 * Hand-written rather than generated from the OpenAPI document. For a project this size the
 * generator's output (and the build step it needs) costs more than it saves, and writing them
 * by hand keeps the contract something a reader can take in at a glance. If the API surface
 * grows much further, generating these from /swagger/v1/swagger.json becomes the better trade.
 *
 * Every enum is a string union, because the API serialises enums as their names.
 */

// ---------------------------------------------------------------- shared

/** The one pagination shape every list endpoint returns (Appendix D.1 of the master reference). */
export interface PagedResult<T> {
  items: T[];
  page: number;
  pageSize: number;
  totalCount: number;
  totalPages: number;
  hasPrevious: boolean;
  hasNext: boolean;
}

/** RFC 7807 ProblemDetails — the one error shape the API returns. */
export interface ProblemDetails {
  type?: string;
  title?: string;
  status?: number;
  detail?: string;
  instance?: string;
  traceId?: string;
  /** Present on validation failures: field name to messages. */
  errors?: Record<string, string[]>;
}

// ---------------------------------------------------------------- auth

export interface LoginRequest {
  email: string;
  password: string;
}

export interface AuthResponse {
  accessToken: string;
  refreshToken: string;
  accessTokenExpiresAt: string;
  user: CurrentUser;
}

export interface CurrentUser {
  userId: number;
  name: string;
  email: string;
  roles: string[];
  /**
   * Used only to decide what to show. The API re-checks every one of these on every request,
   * so a tampered list here changes the menu and nothing else (§29).
   */
  permissions: string[];
}

export interface ChangePasswordRequest {
  currentPassword: string;
  newPassword: string;
}

// ---------------------------------------------------------------- devices

export type LifecycleStatus = 'Registered' | 'Active' | 'Inactive' | 'Retired';
export type ConnectivityStatus = 'Unknown' | 'Online' | 'Offline';

export interface Device {
  deviceId: number;
  deviceCode: string;
  deviceName: string;
  deviceTypeId: number;
  deviceTypeName: string;
  locationId: number;
  locationName: string;
  /** Where the device sits administratively — a human decision. */
  lifecycleStatus: LifecycleStatus;
  /** Whether it is currently reachable — derived from telemetry. Separate on purpose. */
  connectivityStatus: ConnectivityStatus;
  lastSeenAt: string | null;
  hasApiKey: boolean;
  openAlertCount: number;
  createdAt: string;
  updatedAt: string | null;
  /** Round-tripped on update so the API can reject an edit made from a stale read. */
  rowVersion: string | null;
}

export interface CreateDeviceRequest {
  deviceCode: string;
  deviceName: string;
  deviceTypeId: number;
  locationId: number;
}

export interface UpdateDeviceRequest {
  deviceName: string;
  deviceTypeId: number;
  locationId: number;
  lifecycleStatus: LifecycleStatus;
  rowVersion: string | null;
}

export interface DeviceQuery {
  search?: string;
  deviceTypeId?: number;
  locationId?: number;
  lifecycleStatus?: LifecycleStatus;
  connectivityStatus?: ConnectivityStatus;
  sortBy?: string;
  sortDescending?: boolean;
  page?: number;
  pageSize?: number;
}

export interface DeviceApiKey {
  deviceId: number;
  deviceCode: string;
  /** Returned exactly once. It is not stored and cannot be retrieved again. */
  apiKey: string;
  issuedAt: string;
}

export interface DeviceType {
  deviceTypeId: number;
  name: string;
  description: string | null;
  isActive: boolean;
  deviceCount: number;
}

export interface Location {
  locationId: number;
  name: string;
  description: string | null;
  isActive: boolean;
  deviceCount: number;
}

// ---------------------------------------------------------------- telemetry

export interface TelemetryReading {
  telemetryId: number;
  deviceId: number;
  temperature: number;
  battery: number;
  signalStrength: number;
  recordedAt: string;
  receivedAt: string;
}

export interface TelemetryIngestRequest {
  deviceId: number;
  temperature: number;
  battery: number;
  signalStrength: number;
  recordedAt?: string;
  messageId?: string;
}

export interface TelemetryIngestResult {
  telemetryId: number;
  deviceId: number;
  recordedAt: string;
  /** True when the API recognised the reading as a retry and discarded it. */
  duplicate: boolean;
  alertsRaised: number;
}

export interface TelemetryTrendPoint {
  bucketStart: string;
  avgTemperature: number;
  minTemperature: number;
  maxTemperature: number;
  avgBattery: number;
  readingCount: number;
}

// ---------------------------------------------------------------- alerts

export type AlertMetric = 'Temperature' | 'Battery' | 'SignalStrength' | 'LastSeenAgeSeconds';
export type AlertOperator =
  | 'GreaterThan'
  | 'GreaterThanOrEqual'
  | 'LessThan'
  | 'LessThanOrEqual'
  | 'EqualTo'
  | 'NotEqualTo';
export type AlertSeverity = 'Info' | 'Low' | 'Medium' | 'High' | 'Critical';
export type AlertStatus = 'Open' | 'Acknowledged' | 'Resolved';

export interface Alert {
  alertId: number;
  deviceId: number;
  deviceName: string;
  deviceCode: string;
  alertRuleId: number | null;
  alertRuleName: string | null;
  metric: AlertMetric;
  message: string;
  severity: AlertSeverity;
  status: AlertStatus;
  triggeredValue: number;
  threshold: number;
  createdAt: string;
  acknowledgedAt: string | null;
  acknowledgedBy: string | null;
  resolvedAt: string | null;
  resolvedBy: string | null;
  resolutionNote: string | null;
}

export interface AlertQuery {
  status?: AlertStatus;
  severity?: AlertSeverity;
  deviceId?: number;
  from?: string;
  to?: string;
  page?: number;
  pageSize?: number;
}

export interface AlertRule {
  alertRuleId: number;
  name: string;
  description: string | null;
  metric: AlertMetric;
  operator: AlertOperator;
  threshold: number;
  severity: AlertSeverity;
  isEnabled: boolean;
  cooldownSeconds: number;
  deviceTypeId: number | null;
  deviceTypeName: string | null;
  /** Rendered condition, e.g. "Temperature > 40°C". Built by the API so the UI need not re-derive it. */
  conditionSummary: string;
  triggeredCount: number;
  createdAt: string;
  updatedAt: string | null;
  rowVersion: string | null;
}

export interface CreateAlertRuleRequest {
  name: string;
  description: string | null;
  metric: AlertMetric;
  operator: AlertOperator;
  threshold: number;
  severity: AlertSeverity;
  isEnabled: boolean;
  cooldownSeconds: number;
  deviceTypeId: number | null;
}

export interface UpdateAlertRuleRequest {
  name: string;
  description: string | null;
  metric: AlertMetric;
  operator: AlertOperator;
  threshold: number;
  severity: AlertSeverity;
  cooldownSeconds: number;
  deviceTypeId: number | null;
  rowVersion: string | null;
}

/**
 * The closed set of metrics, operators and severities a rule may use, served by the API from
 * its own enums. The rule editor renders dropdowns from this rather than offering free text,
 * so it cannot offer something the backend would reject (Appendix C item 5).
 */
export interface AlertRuleVocabulary {
  metrics: VocabularyItem[];
  operators: VocabularyItem[];
  severities: VocabularyItem[];
}

export interface VocabularyItem {
  value: string;
  label: string;
  unit?: string | null;
}

// ---------------------------------------------------------------- settings

export type SettingValueType = 'String' | 'Integer' | 'Decimal' | 'Boolean' | 'Enum';

export interface Setting {
  settingId: number;
  key: string;
  value: string;
  /** Drives which control the admin form renders — a number input, a toggle or a dropdown. */
  valueType: SettingValueType;
  category: string;
  description: string | null;
  unit: string | null;
  isEditable: boolean;
  minValue: number | null;
  maxValue: number | null;
  allowedValues: string[] | null;
  version: number;
  updatedBy: string | null;
  updatedAt: string | null;
  rowVersion: string | null;
}

export interface UpdateSettingRequest {
  value: string;
  changeReason: string | null;
  rowVersion: string | null;
}

export interface SettingHistoryEntry {
  settingHistoryId: number;
  key: string;
  version: number;
  oldValue: string | null;
  newValue: string;
  changedBy: string | null;
  changedAt: string;
  changeReason: string | null;
}

// ---------------------------------------------------------------- administration

export interface User {
  userId: number;
  name: string;
  email: string;
  isActive: boolean;
  isLockedOut: boolean;
  lockedOutUntil: string | null;
  lastLoginAt: string | null;
  roles: RoleSummary[];
  createdAt: string;
  updatedAt: string | null;
}

export interface RoleSummary {
  roleId: number;
  name: string;
}

export interface CreateUserRequest {
  name: string;
  email: string;
  password: string;
  roleIds: number[];
}

export interface UpdateUserRequest {
  name: string;
  email: string;
  roleIds: number[];
}

export interface UserQuery {
  search?: string;
  isActive?: boolean;
  roleId?: number;
  page?: number;
  pageSize?: number;
}

export interface Role {
  roleId: number;
  name: string;
  description: string | null;
  isActive: boolean;
  /** Built-in roles cannot be renamed or disabled; their permission sets still can be edited. */
  isSystemRole: boolean;
  userCount: number;
  permissions: string[];
  createdAt: string;
  updatedAt: string | null;
}

export interface CreateRoleRequest {
  name: string;
  description: string | null;
  permissions: string[];
}

export interface UpdateRoleRequest {
  name: string;
  description: string | null;
  isActive: boolean;
}

export interface Permission {
  permissionId: number;
  key: string;
  name: string;
  description: string | null;
  category: string;
}

// ---------------------------------------------------------------- audit

export interface AuditEntry {
  auditId: number;
  userId: number | null;
  userEmail: string | null;
  action: string;
  entityType: string;
  entityId: string | null;
  /** Serialised JSON of just the changed fields — never a whole entity, so no secrets. */
  oldValue: string | null;
  newValue: string | null;
  timestamp: string;
  ipAddress: string | null;
  correlationId: string | null;
}

export interface AuditQuery {
  entityType?: string;
  action?: string;
  userId?: number;
  search?: string;
  from?: string;
  to?: string;
  page?: number;
  pageSize?: number;
}

// ---------------------------------------------------------------- dashboard

export interface DashboardSummary {
  devices: DeviceCounts;
  alerts: AlertCounts;
  alertsBySeverity: SeverityBucket[];
  devicesByLocation: LocationBucket[];
  devicesByType: DeviceTypeBucket[];
  temperatureTrend: TelemetryTrendPoint[];
  recentAlerts: Alert[];
  devicesNeedingAttention: DeviceHealthRow[];
  trendHours: number;
  generatedAt: string;
}

export interface DeviceCounts {
  total: number;
  online: number;
  offline: number;
  unknown: number;
  retired: number;
  active: number;
}

export interface AlertCounts {
  open: number;
  acknowledged: number;
  critical: number;
  resolvedToday: number;
}

export interface SeverityBucket {
  severity: AlertSeverity;
  count: number;
}

export interface LocationBucket {
  location: string;
  total: number;
  online: number;
  offline: number;
}

export interface DeviceTypeBucket {
  deviceType: string;
  count: number;
}

export interface DeviceHealthRow {
  deviceId: number;
  deviceCode: string;
  deviceName: string;
  locationName: string;
  connectivityStatus: ConnectivityStatus;
  lastSeenAt: string | null;
  lastBattery: number | null;
  lastTemperature: number | null;
  openAlertCount: number;
  highestOpenSeverity: AlertSeverity | null;
}

// ---------------------------------------------------------------- live push

export type AlertChange = 'Raised' | 'Acknowledged' | 'Resolved';

/** Pushed when an alert is raised, acknowledged or resolved. */
export interface AlertChangedEvent {
  alertId: number;
  deviceId: number;
  change: AlertChange;
  severity: AlertSeverity;
  status: AlertStatus;
  message: string;
  occurredAt: string;
}

/** Pushed when a device's connectivity or lifecycle status changes, or a device is registered. */
export interface DeviceStatusChangedEvent {
  deviceId: number;
  deviceCode: string;
  deviceName: string;
  connectivityStatus: ConnectivityStatus;
  lifecycleStatus: LifecycleStatus;
  lastSeenAt: string | null;
  occurredAt: string;
}
