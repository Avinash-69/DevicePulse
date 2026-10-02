import { Routes } from '@angular/router';

import { anonymousOnlyGuard, authGuard, permissionGuard } from './core/auth/guards';
import { Permissions } from './core/auth/permissions';

/**
 * Application routes.
 *
 * Every feature is lazily loaded. That is not premature optimisation: the admin pages are
 * irrelevant to an Operator, and loading the role editor's code for someone who can never open
 * it is pure cost on first paint.
 *
 * Each route carries the permission its page needs, so the guard, the sidebar and the API all
 * agree on who may see what — the guard being a convenience and the API the authority (§29).
 */
export const routes: Routes = [
  {
    path: 'login',
    canActivate: [anonymousOnlyGuard],
    loadComponent: () => import('./features/auth/login.component').then((m) => m.LoginComponent),
    title: 'Sign in · DevicePulse',
  },

  {
    path: '',
    canActivate: [authGuard],
    loadComponent: () => import('./layout/shell.component').then((m) => m.ShellComponent),
    children: [
      { path: '', pathMatch: 'full', redirectTo: 'dashboard' },

      {
        path: 'dashboard',
        canActivate: [permissionGuard(Permissions.dashboardView)],
        loadComponent: () =>
          import('./features/dashboard/dashboard.component').then((m) => m.DashboardComponent),
        title: 'Dashboard · DevicePulse',
      },

      {
        path: 'devices',
        canActivate: [permissionGuard(Permissions.deviceView)],
        loadComponent: () =>
          import('./features/devices/device-list.component').then((m) => m.DeviceListComponent),
        title: 'Devices · DevicePulse',
      },
      {
        path: 'devices/:id',
        canActivate: [permissionGuard(Permissions.deviceView)],
        loadComponent: () =>
          import('./features/devices/device-detail.component').then((m) => m.DeviceDetailComponent),
        title: 'Device · DevicePulse',
      },

      {
        path: 'alerts',
        canActivate: [permissionGuard(Permissions.alertView)],
        loadComponent: () => import('./features/alerts/alert-list.component').then((m) => m.AlertListComponent),
        title: 'Alerts · DevicePulse',
      },

      {
        path: 'alert-rules',
        canActivate: [permissionGuard(Permissions.alertRuleView)],
        loadComponent: () =>
          import('./features/alert-rules/alert-rules.component').then((m) => m.AlertRulesComponent),
        title: 'Alert rules · DevicePulse',
      },

      {
        path: 'settings',
        canActivate: [permissionGuard(Permissions.settingsView)],
        loadComponent: () => import('./features/settings/settings.component').then((m) => m.SettingsComponent),
        title: 'Settings · DevicePulse',
      },

      {
        path: 'reference-data',
        canActivate: [permissionGuard(Permissions.referenceDataView)],
        loadComponent: () =>
          import('./features/settings/reference-data.component').then((m) => m.ReferenceDataComponent),
        title: 'Reference data · DevicePulse',
      },

      {
        path: 'simulator',
        canActivate: [permissionGuard(Permissions.simulatorView)],
        loadComponent: () =>
          import('./features/simulator/simulator.component').then((m) => m.SimulatorComponent),
        title: 'Simulator · DevicePulse',
      },

      {
        path: 'admin/users',
        canActivate: [permissionGuard(Permissions.userView)],
        loadComponent: () => import('./features/admin/user-list.component').then((m) => m.UserListComponent),
        title: 'Users · DevicePulse',
      },
      {
        path: 'admin/roles',
        canActivate: [permissionGuard(Permissions.roleView)],
        loadComponent: () => import('./features/admin/role-list.component').then((m) => m.RoleListComponent),
        title: 'Roles · DevicePulse',
      },

      {
        path: 'audit',
        canActivate: [permissionGuard(Permissions.auditView)],
        loadComponent: () => import('./features/audit/audit-log.component').then((m) => m.AuditLogComponent),
        title: 'Audit log · DevicePulse',
      },

      {
        // No permission guard: changing your own password is something every signed-in user
        // can do, by definition.
        path: 'account',
        loadComponent: () => import('./features/auth/account.component').then((m) => m.AccountComponent),
        title: 'Account · DevicePulse',
      },
    ],
  },

  { path: '**', redirectTo: '' },
];
