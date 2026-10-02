import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import { environment } from '../../../environments/environment';
import {
  Alert,
  AlertQuery,
  AlertRule,
  AlertRuleVocabulary,
  AuditEntry,
  AuditQuery,
  CreateAlertRuleRequest,
  CreateDeviceRequest,
  CreateRoleRequest,
  CreateUserRequest,
  DashboardSummary,
  Device,
  DeviceApiKey,
  DeviceQuery,
  DeviceType,
  Location,
  PagedResult,
  Permission,
  Role,
  Setting,
  SettingHistoryEntry,
  TelemetryIngestRequest,
  TelemetryIngestResult,
  TelemetryReading,
  TelemetryTrendPoint,
  UpdateAlertRuleRequest,
  UpdateDeviceRequest,
  UpdateRoleRequest,
  UpdateSettingRequest,
  UpdateUserRequest,
  User,
  UserQuery,
} from '../models/api.models';

/**
 * One typed client for the whole API.
 *
 * Deliberately a single service rather than eight. Every method is a thin, uniform wrapper
 * around one endpoint, so splitting it would add files and imports without adding structure.
 * If a method ever grows real logic beyond shaping parameters, that is the signal to split out
 * a feature-level service that depends on this one.
 */
@Injectable({ providedIn: 'root' })
export class ApiService {
  private readonly http = inject(HttpClient);
  private readonly base = environment.apiBaseUrl;

  // ---------------------------------------------------------------- dashboard

  getDashboard(): Observable<DashboardSummary> {
    return this.http.get<DashboardSummary>(`${this.base}/dashboard/summary`);
  }

  // ---------------------------------------------------------------- devices

  getDevices(query: DeviceQuery = {}): Observable<PagedResult<Device>> {
    return this.http.get<PagedResult<Device>>(`${this.base}/devices`, {
      params: toParams(query),
    });
  }

  getDevice(id: number): Observable<Device> {
    return this.http.get<Device>(`${this.base}/devices/${id}`);
  }

  createDevice(request: CreateDeviceRequest): Observable<Device> {
    return this.http.post<Device>(`${this.base}/devices`, request);
  }

  updateDevice(id: number, request: UpdateDeviceRequest): Observable<Device> {
    return this.http.put<Device>(`${this.base}/devices/${id}`, request);
  }

  /** Retires the device. The API keeps its telemetry and alert history (Appendix C item 2). */
  retireDevice(id: number, reason: string | null): Observable<void> {
    return this.http.request<void>('delete', `${this.base}/devices/${id}`, {
      body: { reason },
    });
  }

  issueDeviceApiKey(id: number): Observable<DeviceApiKey> {
    return this.http.post<DeviceApiKey>(`${this.base}/devices/${id}/api-key`, {});
  }

  revokeDeviceApiKey(id: number): Observable<void> {
    return this.http.delete<void>(`${this.base}/devices/${id}/api-key`);
  }

  // ---------------------------------------------------------------- telemetry

  getDeviceTelemetry(
    deviceId: number,
    page = 1,
    pageSize = 25,
  ): Observable<PagedResult<TelemetryReading>> {
    return this.http.get<PagedResult<TelemetryReading>>(
      `${this.base}/devices/${deviceId}/telemetry`,
      { params: toParams({ page, pageSize }) },
    );
  }

  /**
   * The device's most recent reading. The API answers 204 when it has never reported, which
   * Angular surfaces as null — a newly-registered device having no readings is normal.
   */
  getLatestTelemetry(deviceId: number): Observable<TelemetryReading | null> {
    return this.http.get<TelemetryReading | null>(
      `${this.base}/devices/${deviceId}/telemetry/latest`,
    );
  }

  getDeviceTrend(deviceId: number, hours: number): Observable<TelemetryTrendPoint[]> {
    return this.http.get<TelemetryTrendPoint[]>(
      `${this.base}/devices/${deviceId}/telemetry/trend`,
      { params: toParams({ hours }) },
    );
  }

  ingestTelemetry(request: TelemetryIngestRequest): Observable<TelemetryIngestResult> {
    return this.http.post<TelemetryIngestResult>(`${this.base}/telemetry/ingest`, request);
  }

  // ---------------------------------------------------------------- alerts

  getAlerts(query: AlertQuery = {}): Observable<PagedResult<Alert>> {
    return this.http.get<PagedResult<Alert>>(`${this.base}/alerts`, { params: toParams(query) });
  }

  getAlert(id: number): Observable<Alert> {
    return this.http.get<Alert>(`${this.base}/alerts/${id}`);
  }

  acknowledgeAlert(id: number): Observable<Alert> {
    return this.http.post<Alert>(`${this.base}/alerts/${id}/acknowledge`, {});
  }

  resolveAlert(id: number, resolutionNote: string | null): Observable<Alert> {
    return this.http.post<Alert>(`${this.base}/alerts/${id}/resolve`, { resolutionNote });
  }

  // ---------------------------------------------------------------- alert rules

  getAlertRules(): Observable<AlertRule[]> {
    return this.http.get<AlertRule[]>(`${this.base}/alert-rules`);
  }

  getAlertRule(id: number): Observable<AlertRule> {
    return this.http.get<AlertRule>(`${this.base}/alert-rules/${id}`);
  }

  getAlertRuleVocabulary(): Observable<AlertRuleVocabulary> {
    return this.http.get<AlertRuleVocabulary>(`${this.base}/alert-rules/vocabulary`);
  }

  createAlertRule(request: CreateAlertRuleRequest): Observable<AlertRule> {
    return this.http.post<AlertRule>(`${this.base}/alert-rules`, request);
  }

  updateAlertRule(id: number, request: UpdateAlertRuleRequest): Observable<AlertRule> {
    return this.http.put<AlertRule>(`${this.base}/alert-rules/${id}`, request);
  }

  setAlertRuleStatus(id: number, isEnabled: boolean): Observable<AlertRule> {
    return this.http.patch<AlertRule>(`${this.base}/alert-rules/${id}/status`, { isEnabled });
  }

  deleteAlertRule(id: number): Observable<void> {
    return this.http.delete<void>(`${this.base}/alert-rules/${id}`);
  }

  // ---------------------------------------------------------------- settings

  getSettings(category?: string): Observable<Setting[]> {
    return this.http.get<Setting[]>(`${this.base}/settings`, {
      params: toParams({ category }),
    });
  }

  getSettingCategories(): Observable<string[]> {
    return this.http.get<string[]>(`${this.base}/settings/categories`);
  }

  updateSetting(key: string, request: UpdateSettingRequest): Observable<Setting> {
    // The key is encoded because setting keys are dotted paths; without this a key containing
    // a reserved character would break the URL.
    return this.http.put<Setting>(`${this.base}/settings/${encodeURIComponent(key)}`, request);
  }

  getSettingHistory(key: string): Observable<SettingHistoryEntry[]> {
    return this.http.get<SettingHistoryEntry[]>(
      `${this.base}/settings/${encodeURIComponent(key)}/history`,
    );
  }

  // ---------------------------------------------------------------- reference data

  getDeviceTypes(includeInactive = false): Observable<DeviceType[]> {
    return this.http.get<DeviceType[]>(`${this.base}/reference/device-types`, {
      params: toParams({ includeInactive }),
    });
  }

  createDeviceType(name: string, description: string | null): Observable<DeviceType> {
    return this.http.post<DeviceType>(`${this.base}/reference/device-types`, { name, description });
  }

  updateDeviceType(
    id: number,
    name: string,
    description: string | null,
    isActive: boolean,
  ): Observable<DeviceType> {
    return this.http.put<DeviceType>(`${this.base}/reference/device-types/${id}`, {
      name,
      description,
      isActive,
    });
  }

  getLocations(includeInactive = false): Observable<Location[]> {
    return this.http.get<Location[]>(`${this.base}/reference/locations`, {
      params: toParams({ includeInactive }),
    });
  }

  createLocation(name: string, description: string | null): Observable<Location> {
    return this.http.post<Location>(`${this.base}/reference/locations`, { name, description });
  }

  updateLocation(
    id: number,
    name: string,
    description: string | null,
    isActive: boolean,
  ): Observable<Location> {
    return this.http.put<Location>(`${this.base}/reference/locations/${id}`, {
      name,
      description,
      isActive,
    });
  }

  // ---------------------------------------------------------------- users

  getUsers(query: UserQuery = {}): Observable<PagedResult<User>> {
    return this.http.get<PagedResult<User>>(`${this.base}/admin/users`, {
      params: toParams(query),
    });
  }

  getUser(id: number): Observable<User> {
    return this.http.get<User>(`${this.base}/admin/users/${id}`);
  }

  createUser(request: CreateUserRequest): Observable<User> {
    return this.http.post<User>(`${this.base}/admin/users`, request);
  }

  updateUser(id: number, request: UpdateUserRequest): Observable<User> {
    return this.http.put<User>(`${this.base}/admin/users/${id}`, request);
  }

  setUserStatus(id: number, isActive: boolean): Observable<User> {
    return this.http.patch<User>(`${this.base}/admin/users/${id}/status`, { isActive });
  }

  resetUserPassword(id: number, newPassword: string): Observable<void> {
    return this.http.post<void>(`${this.base}/admin/users/${id}/reset-password`, { newPassword });
  }

  // ---------------------------------------------------------------- roles & permissions

  getRoles(): Observable<Role[]> {
    return this.http.get<Role[]>(`${this.base}/admin/roles`);
  }

  getRole(id: number): Observable<Role> {
    return this.http.get<Role>(`${this.base}/admin/roles/${id}`);
  }

  createRole(request: CreateRoleRequest): Observable<Role> {
    return this.http.post<Role>(`${this.base}/admin/roles`, request);
  }

  updateRole(id: number, request: UpdateRoleRequest): Observable<Role> {
    return this.http.put<Role>(`${this.base}/admin/roles/${id}`, request);
  }

  /** Replaces the role's whole permission set — the API takes the intended state, not a delta. */
  setRolePermissions(id: number, permissions: string[]): Observable<Role> {
    return this.http.put<Role>(`${this.base}/admin/roles/${id}/permissions`, { permissions });
  }

  getPermissionCatalog(): Observable<Permission[]> {
    return this.http.get<Permission[]>(`${this.base}/admin/permissions`);
  }

  // ---------------------------------------------------------------- audit

  getAuditLog(query: AuditQuery = {}): Observable<PagedResult<AuditEntry>> {
    return this.http.get<PagedResult<AuditEntry>>(`${this.base}/audit`, {
      params: toParams(query),
    });
  }
}

/**
 * Builds query parameters, dropping anything null, undefined or blank.
 *
 * The dropping matters: sending `search=` would make the API filter on an empty string, and
 * sending `lifecycleStatus=` would fail to bind to a nullable enum.
 */
function toParams(source: object): HttpParams {
  let params = new HttpParams();

  for (const [key, value] of Object.entries(source)) {
    if (value === null || value === undefined || value === '') {
      continue;
    }

    params = params.set(key, String(value));
  }

  return params;
}
