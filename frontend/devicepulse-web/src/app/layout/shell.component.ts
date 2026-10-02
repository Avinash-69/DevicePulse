import { CommonModule } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';

import { AuthService } from '../core/auth/auth.service';
import { Permissions } from '../core/auth/permissions';
import { NotificationService } from '../core/services/notification.service';
import { ThemeService } from '../core/services/theme.service';
import { ToastsComponent } from './toasts.component';

interface NavItem {
  label: string;
  path: string;
  icon: string;
  /** Any one of these permissions is enough to show the item. */
  permissions: string[];
}

/**
 * The authenticated application shell: sidebar, top bar and router outlet.
 *
 * Navigation is filtered by permission so a user is not shown pages that would only return
 * 403s. That is a convenience; the guards and the API are what actually prevent access (§29).
 */
@Component({
  selector: 'dp-shell',
  standalone: true,
  imports: [CommonModule, RouterOutlet, RouterLink, RouterLinkActive, ToastsComponent],
  template: `
    <div class="shell" [class.sidebar-open]="sidebarOpen()">
      <aside class="sidebar">
        <div class="brand">
          <span class="mark" aria-hidden="true">
            <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2">
              <path d="M2 12h4l2.5-7 3.5 14 3-9 2 4h5" stroke-linecap="round" stroke-linejoin="round" />
            </svg>
          </span>
          <span class="name">DevicePulse</span>
        </div>

        <nav aria-label="Main">
          @for (group of visibleGroups(); track group.title) {
            <div class="nav-group">
              <span class="nav-title">{{ group.title }}</span>

              @for (item of group.items; track item.path) {
                <a
                  [routerLink]="item.path"
                  routerLinkActive="active"
                  class="nav-item"
                  (click)="closeSidebarOnMobile()"
                >
                  <span class="icon" [innerHTML]="item.icon" aria-hidden="true"></span>
                  <span>{{ item.label }}</span>
                </a>
              }
            </div>
          }
        </nav>

        <div class="sidebar-foot subtle small">
          <span>{{ user()?.roles?.join(', ') }}</span>
        </div>
      </aside>

      <div class="main">
        <header class="topbar">
          <button
            type="button"
            class="btn btn-ghost btn-icon menu-toggle"
            aria-label="Toggle navigation"
            (click)="sidebarOpen.set(!sidebarOpen())"
          >
            <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
              <path d="M3 6h18M3 12h18M3 18h18" stroke-linecap="round" />
            </svg>
          </button>

          <span class="spacer"></span>

          <button
            type="button"
            class="btn btn-ghost btn-sm"
            [attr.aria-label]="'Switch to ' + (theme.isDark() ? 'light' : 'dark') + ' theme'"
            (click)="theme.toggle()"
          >
            {{ theme.isDark() ? 'Light' : 'Dark' }}
          </button>

          <div class="account">
            <button
              type="button"
              class="account-btn"
              [attr.aria-expanded]="menuOpen()"
              (click)="menuOpen.set(!menuOpen())"
            >
              <span class="avatar" aria-hidden="true">{{ initials() }}</span>
              <span class="who">
                <span class="who-name">{{ user()?.name }}</span>
                <span class="who-email subtle small">{{ user()?.email }}</span>
              </span>
            </button>

            @if (menuOpen()) {
              <div class="menu card" role="menu">
                <a routerLink="/account" class="menu-item" (click)="menuOpen.set(false)">
                  Change password
                </a>
                <hr class="divider" />
                <button type="button" class="menu-item" (click)="signOut()">Sign out</button>
              </div>
            }
          </div>
        </header>

        <main>
          <router-outlet />
        </main>
      </div>

      @if (sidebarOpen()) {
        <div class="scrim" (click)="sidebarOpen.set(false)" role="presentation"></div>
      }
    </div>

    <dp-toasts />
  `,
  styles: [
    `
      .shell {
        display: grid;
        grid-template-columns: var(--sidebar-width) 1fr;
        min-height: 100vh;
      }

      /* ---- sidebar ---- */

      .sidebar {
        display: flex;
        flex-direction: column;
        background: var(--surface);
        border-right: 1px solid var(--border);
        position: sticky;
        top: 0;
        height: 100vh;
        overflow-y: auto;
      }

      .brand {
        display: flex;
        gap: 0.55rem;
        align-items: center;
        height: var(--topbar-height);
        padding: 0 1rem;
        border-bottom: 1px solid var(--border);
        flex: 0 0 auto;
      }

      .mark {
        display: grid;
        place-items: center;
        width: 26px;
        height: 26px;
        color: var(--accent-text);
        background: var(--accent);
        border-radius: 6px;
      }

      .mark svg { width: 17px; height: 17px; }

      .name {
        font-weight: 600;
        letter-spacing: -0.02em;
      }

      nav {
        flex: 1 1 auto;
        padding: 0.75rem 0.6rem;
      }

      .nav-group { margin-bottom: 1rem; }

      .nav-title {
        display: block;
        padding: 0 0.6rem 0.35rem;
        font-size: 0.68rem;
        font-weight: 600;
        text-transform: uppercase;
        letter-spacing: 0.06em;
        color: var(--text-subtle);
      }

      .nav-item {
        display: flex;
        gap: 0.6rem;
        align-items: center;
        padding: 0.45rem 0.6rem;
        font-size: 0.875rem;
        color: var(--text-muted);
        border-radius: var(--radius);
        text-decoration: none;
      }

      .nav-item:hover {
        color: var(--text);
        background: var(--surface-3);
        text-decoration: none;
      }

      .nav-item.active {
        color: var(--accent);
        background: var(--accent-soft);
        font-weight: 500;
      }

      .icon {
        display: grid;
        place-items: center;
        width: 17px;
        height: 17px;
        flex: 0 0 auto;
      }

      .icon ::ng-deep svg { width: 16px; height: 16px; }

      .sidebar-foot {
        padding: 0.7rem 1rem;
        border-top: 1px solid var(--border);
        flex: 0 0 auto;
      }

      /* ---- main ---- */

      .main {
        display: flex;
        flex-direction: column;
        min-width: 0;
      }

      .topbar {
        display: flex;
        gap: 0.5rem;
        align-items: center;
        height: var(--topbar-height);
        padding: 0 1rem;
        background: var(--surface);
        border-bottom: 1px solid var(--border);
        position: sticky;
        top: 0;
        z-index: 20;
      }

      .menu-toggle { display: none; }

      main { flex: 1 1 auto; min-width: 0; }

      /* ---- account menu ---- */

      .account { position: relative; }

      .account-btn {
        display: flex;
        gap: 0.5rem;
        align-items: center;
        padding: 0.25rem 0.4rem;
        font: inherit;
        color: inherit;
        background: transparent;
        border: none;
        border-radius: var(--radius);
        cursor: pointer;
      }

      .account-btn:hover { background: var(--surface-3); }

      .avatar {
        display: grid;
        place-items: center;
        width: 28px;
        height: 28px;
        font-size: 0.72rem;
        font-weight: 600;
        color: var(--accent-text);
        background: var(--accent);
        border-radius: 50%;
        flex: 0 0 auto;
      }

      .who {
        display: grid;
        text-align: left;
        line-height: 1.25;
      }

      .who-name { font-size: 0.82rem; font-weight: 500; }
      .who-email { font-size: 0.72rem; }

      .menu {
        position: absolute;
        top: calc(100% + 6px);
        right: 0;
        min-width: 190px;
        padding: 0.3rem;
        box-shadow: var(--shadow-lg);
        z-index: 30;
      }

      .menu-item {
        display: block;
        width: 100%;
        padding: 0.45rem 0.6rem;
        font: inherit;
        font-size: 0.85rem;
        text-align: left;
        color: var(--text);
        background: transparent;
        border: none;
        border-radius: var(--radius-sm);
        cursor: pointer;
        text-decoration: none;
      }

      .menu-item:hover { background: var(--surface-3); text-decoration: none; }

      .scrim { display: none; }

      /* ---- narrow screens: the sidebar becomes a drawer ---- */

      @media (max-width: 900px) {
        .shell { grid-template-columns: 1fr; }

        .sidebar {
          position: fixed;
          top: 0;
          left: 0;
          width: var(--sidebar-width);
          z-index: 40;
          transform: translateX(-100%);
          transition: transform 0.18s ease-out;
        }

        .sidebar-open .sidebar { transform: translateX(0); }

        .menu-toggle { display: inline-flex; }

        .scrim {
          display: block;
          position: fixed;
          inset: 0;
          z-index: 35;
          background: rgb(8 12 16 / 45%);
        }

        .who { display: none; }
      }
    `,
  ],
})
export class ShellComponent {
  private readonly auth = inject(AuthService);
  private readonly notifications = inject(NotificationService);

  readonly theme = inject(ThemeService);

  readonly user = this.auth.user;
  readonly sidebarOpen = signal(false);
  readonly menuOpen = signal(false);

  readonly initials = computed(() => {
    const name = this.user()?.name ?? '';

    return name
      .split(/\s+/)
      .filter(Boolean)
      .slice(0, 2)
      .map((part) => part[0]?.toUpperCase() ?? '')
      .join('');
  });

  /**
   * Grouped so the sidebar separates day-to-day monitoring from administration — the
   * distinction that matters to whoever is using it.
   */
  private readonly groups: { title: string; items: NavItem[] }[] = [
    {
      title: 'Monitor',
      items: [
        { label: 'Dashboard', path: '/dashboard', icon: icons.dashboard, permissions: [Permissions.dashboardView] },
        { label: 'Devices', path: '/devices', icon: icons.devices, permissions: [Permissions.deviceView] },
        { label: 'Alerts', path: '/alerts', icon: icons.alerts, permissions: [Permissions.alertView] },
      ],
    },
    {
      title: 'Configure',
      items: [
        { label: 'Alert rules', path: '/alert-rules', icon: icons.rules, permissions: [Permissions.alertRuleView] },
        { label: 'Settings', path: '/settings', icon: icons.settings, permissions: [Permissions.settingsView] },
        {
          label: 'Reference data',
          path: '/reference-data',
          icon: icons.reference,
          permissions: [Permissions.referenceDataView],
        },
        { label: 'Simulator', path: '/simulator', icon: icons.simulator, permissions: [Permissions.simulatorView] },
      ],
    },
    {
      title: 'Administer',
      items: [
        { label: 'Users', path: '/admin/users', icon: icons.users, permissions: [Permissions.userView] },
        { label: 'Roles', path: '/admin/roles', icon: icons.roles, permissions: [Permissions.roleView] },
        { label: 'Audit log', path: '/audit', icon: icons.audit, permissions: [Permissions.auditView] },
      ],
    },
  ];

  /** Groups with at least one visible item, so an empty heading never appears. */
  readonly visibleGroups = computed(() =>
    this.groups
      .map((group) => ({
        title: group.title,
        items: group.items.filter((item) => this.auth.hasAny(...item.permissions)),
      }))
      .filter((group) => group.items.length > 0),
  );

  closeSidebarOnMobile(): void {
    if (window.matchMedia('(max-width: 900px)').matches) {
      this.sidebarOpen.set(false);
    }
  }

  signOut(): void {
    this.menuOpen.set(false);
    this.notifications.dismissAll();
    this.auth.logout();
  }
}

/**
 * Inline SVG icons.
 *
 * Inline rather than an icon font or a package: eleven icons do not justify a dependency, and
 * inline SVG inherits currentColor so they follow the theme without extra work.
 */
const icons = {
  dashboard:
    '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><rect x="3" y="3" width="7" height="9" rx="1"/><rect x="14" y="3" width="7" height="5" rx="1"/><rect x="14" y="12" width="7" height="9" rx="1"/><rect x="3" y="16" width="7" height="5" rx="1"/></svg>',
  devices:
    '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><rect x="4" y="3" width="16" height="12" rx="2"/><path d="M8 21h8M12 15v6"/></svg>',
  alerts:
    '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 3a6 6 0 0 0-6 6c0 4-2 5-2 5h16s-2-1-2-5a6 6 0 0 0-6-6z"/><path d="M10.5 20a1.8 1.8 0 0 0 3 0"/></svg>',
  rules:
    '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M4 7h10M18 7h2M4 17h4M12 17h8"/><circle cx="16" cy="7" r="2"/><circle cx="10" cy="17" r="2"/></svg>',
  settings:
    '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><circle cx="12" cy="12" r="3"/><path d="M12 2v3M12 19v3M2 12h3M19 12h3M4.9 4.9l2.1 2.1M17 17l2.1 2.1M19.1 4.9 17 7M7 17l-2.1 2.1"/></svg>',
  reference:
    '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M4 5a2 2 0 0 1 2-2h7l5 5v11a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2z"/><path d="M13 3v5h5M8 13h8M8 17h5"/></svg>',
  simulator:
    '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M2 12h3l2-5 3 10 3-8 2 3h7"/><circle cx="19" cy="19" r="2"/></svg>',
  users:
    '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="9" cy="8" r="3.2"/><path d="M3 20a6 6 0 0 1 12 0"/><path d="M16 5.5a3 3 0 0 1 0 5M18 20a5.5 5.5 0 0 0-2-4"/></svg>',
  roles:
    '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 3l8 3v6c0 5-4 8-8 9-4-1-8-4-8-9V6z"/><path d="M9.5 12l1.8 1.8L15 10"/></svg>',
  audit:
    '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="9"/><path d="M12 7v5l3.5 2"/></svg>',
};
