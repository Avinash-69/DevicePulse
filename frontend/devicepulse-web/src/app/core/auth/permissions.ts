/**
 * Mirror of the backend's permission catalog (DevicePulse.Api.Authorization.Permissions).
 *
 * Kept in sync by hand, which is acceptable precisely because this list has no authority: it
 * decides what the UI offers, and the API independently enforces the real thing. A key that
 * drifts out of sync here causes a menu item to appear or disappear, never an access decision
 * (§4.4, §29 of the master reference).
 */
export const Permissions = {
  deviceView: 'device.view',
  deviceCreate: 'device.create',
  deviceUpdate: 'device.update',
  deviceRetire: 'device.retire',
  deviceManageCredentials: 'device.credentials.manage',

  telemetryView: 'telemetry.view',
  telemetryIngest: 'telemetry.ingest',

  alertView: 'alerts.view',
  alertResolve: 'alerts.resolve',
  alertManage: 'alerts.manage',
  alertRuleView: 'rules.view',
  alertRuleManage: 'rules.manage',

  settingsView: 'settings.view',
  settingsManage: 'settings.manage',

  userView: 'user.view',
  userCreate: 'user.create',
  userUpdate: 'user.update',
  userDisable: 'user.disable',

  roleView: 'role.view',
  roleManage: 'role.manage',
  permissionView: 'permission.view',
  permissionManage: 'permission.manage',

  referenceDataView: 'referencedata.view',
  referenceDataManage: 'referencedata.manage',

  auditView: 'audit.view',
  dashboardView: 'dashboard.view',
  simulatorView: 'simulator.view',
  simulatorManage: 'simulator.manage',
} as const;

export type PermissionKey = (typeof Permissions)[keyof typeof Permissions];
