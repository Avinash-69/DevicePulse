import { Component, DestroyRef, OnDestroy, OnInit, computed, inject, signal, ChangeDetectionStrategy } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';

import { AuthService } from '../core/auth/auth.service';
import { Permissions } from '../core/auth/permissions';
import { ApiService } from '../core/services/api.service';
import { NotificationService } from '../core/services/notification.service';
import { RealtimeService } from '../core/services/realtime.service';
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
 *
 * The top bar does real work rather than holding a theme toggle and an avatar. It carries the
 * name of the current screen, a search box that jumps straight to a device, and a live count of
 * open alerts. For a triage tool that last one matters most: the number an operator wants to
 * know is "is anything on fire", and it should be visible from every screen rather than only
 * from the dashboard. It costs one cheap request a minute -- a page of one row, read for its
 * total -- and is skipped entirely for users who cannot see alerts.
 */
@Component({
  selector: 'dp-shell',
  standalone: true,
  imports: [RouterOutlet, RouterLink, RouterLinkActive, ToastsComponent],
  template: `
    <div class="shell" [class.sidebar-open]="sidebarOpen()">
      <aside class="sidebar">
        <a class="brand" routerLink="/dashboard">
          <span class="mark" aria-hidden="true">
            <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2">
              <path d="M2 12h4l2.5-7 3.5 14 3-9 2 4h5" stroke-linecap="round" stroke-linejoin="round" />
            </svg>
          </span>
          <span class="name">DevicePulse</span>
        </a>

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
                  <span class="nav-label">{{ item.label }}</span>

                  @if (item.path === '/alerts' && openAlerts() > 0) {
                    <span class="count" [class.count-danger]="openAlerts() > 0">{{ openAlerts() }}</span>
                  }
                </a>
              }
            </div>
          }
        </nav>

        <div class="sidebar-foot">
          <span class="label">{{ user()?.roles?.join(', ') }}</span>
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
            <svg width="17" height="17" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
              <path d="M3 6h18M3 12h18M3 18h18" stroke-linecap="round" />
            </svg>
          </button>

          <h2 class="where">{{ pageName() }}</h2>

          <span class="spacer"></span>

          <form class="find" (submit)="search($event)" role="search">
            <svg
              class="find-icon"
              width="14"
              height="14"
              viewBox="0 0 24 24"
              fill="none"
              stroke="currentColor"
              stroke-width="2"
              aria-hidden="true"
            >
              <circle cx="11" cy="11" r="7" />
              <path d="m20 20-3.5-3.5" stroke-linecap="round" />
            </svg>
            <input
              type="search"
              name="q"
              placeholder="Find a device"
              aria-label="Find a device"
              autocomplete="off"
            />
          </form>

          @if (canGoLive()) {
            <span class="live-state" [class.on]="live.isLive()" [attr.title]="liveTitle()" role="status">
              <span class="dot"></span>
              <span class="live-word">{{ live.isLive() ? 'Live' : 'Reconnecting' }}</span>
            </span>
          }

          @if (canSeeAlerts()) {
            <a
              routerLink="/alerts"
              class="alert-pulse"
              [class.live]="openAlerts() > 0"
              [attr.aria-label]="openAlerts() + ' open alerts'"
            >
              <span class="dot"></span>
              <span class="num">{{ openAlerts() }}</span>
              <span class="alert-word">open</span>
            </a>
          }

          <button
            type="button"
            class="btn btn-ghost btn-icon"
            [attr.aria-label]="'Switch to ' + (theme.isDark() ? 'light' : 'dark') + ' theme'"
            (click)="theme.toggle()"
          >
            @if (theme.isDark()) {
              <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                <circle cx="12" cy="12" r="4" />
                <path d="M12 2v2M12 20v2M2 12h2M20 12h2M5 5l1.5 1.5M17.5 17.5 19 19M19 5l-1.5 1.5M6.5 17.5 5 19" stroke-linecap="round" />
              </svg>
            } @else {
              <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                <path d="M20 14.5A8.5 8.5 0 0 1 9.5 4a7 7 0 1 0 10.5 10.5z" stroke-linecap="round" stroke-linejoin="round" />
              </svg>
            }
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
                <span class="who-email">{{ user()?.email }}</span>
              </span>
            </button>

            @if (menuOpen()) {
              <div class="menu account-menu" role="menu">
                <a routerLink="/account" class="menu-item" (click)="menuOpen.set(false)">
                  Your account
                </a>
                <hr class="divider" />
                <button type="button" class="menu-item menu-item-danger" (click)="signOut()">Sign out</button>
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
  changeDetection: ChangeDetectionStrategy.Eager,
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
        background: var(--panel);
        border-right: 1px solid var(--line);
        position: sticky;
        top: 0;
        height: 100vh;
        overflow-y: auto;
        z-index: 30;
      }

      .brand {
        display: flex;
        gap: var(--sp-2);
        align-items: center;
        height: var(--topbar-height);
        padding: 0 var(--sp-3);
        flex: 0 0 auto;
        color: var(--text);
        border-bottom: 1px solid var(--line);
      }

      .brand:hover { text-decoration: none; }

      .mark {
        display: grid;
        place-items: center;
        width: 24px;
        height: 24px;
        color: var(--accent);
        flex: 0 0 auto;
      }

      .mark svg { width: 20px; height: 20px; }

      .name {
        font-size: var(--fs-body);
        font-weight: var(--fw-semibold);
        letter-spacing: var(--tr-snug);
      }

      nav {
        flex: 1 1 auto;
        padding: var(--sp-3) var(--sp-2);
        display: grid;
        gap: var(--sp-5);
        align-content: start;
      }

      .nav-group { display: grid; gap: 1px; }

      .nav-title {
        font-size: var(--fs-micro);
        font-weight: var(--fw-semibold);
        letter-spacing: var(--tr-wide);
        text-transform: uppercase;
        color: var(--text-3);
        padding: 0 var(--sp-2) var(--sp-2);
      }

      /*
       * The active item is marked by a left rule in the accent colour plus a background shift.
       * A filled pill would make the nav the most saturated thing on the screen, which is
       * exactly backwards for a tool where the data should dominate.
       */
      .nav-item {
        display: flex;
        gap: var(--sp-2);
        align-items: center;
        height: 29px;
        padding: 0 var(--sp-2);
        font-size: var(--fs-sm);
        color: var(--text-2);
        border-radius: var(--r-sm);
        border-left: 2px solid transparent;
        white-space: nowrap;
        min-width: 0;
      }

      .nav-item:hover {
        color: var(--text);
        background: var(--panel-2);
        text-decoration: none;
      }

      .nav-item.active {
        color: var(--text);
        font-weight: var(--fw-medium);
        background: var(--accent-wash);
        border-left-color: var(--accent);
        border-radius: 0 var(--r-sm) var(--r-sm) 0;
      }

      .nav-label { overflow: hidden; text-overflow: ellipsis; }

      .nav-item .icon {
        display: grid;
        place-items: center;
        width: 16px;
        height: 16px;
        flex: 0 0 auto;
        color: var(--text-3);
      }

      .nav-item.active .icon { color: var(--accent); }
      .nav-item:hover .icon { color: var(--text-2); }
      .nav-item .icon svg { width: 15px; height: 15px; }
      .nav-item .count { margin-left: auto; }

      .sidebar-foot {
        flex: 0 0 auto;
        padding: var(--sp-3);
        border-top: 1px solid var(--line);
        overflow: hidden;
        text-overflow: ellipsis;
        white-space: nowrap;
      }

      /* ---- main ---- */

      .main { display: flex; flex-direction: column; min-width: 0; }

      .topbar {
        display: flex;
        gap: var(--sp-2);
        align-items: center;
        height: var(--topbar-height);
        padding: 0 var(--sp-4) 0 var(--sp-3);
        background: var(--panel);
        border-bottom: 1px solid var(--line);
        position: sticky;
        top: 0;
        z-index: 20;
      }

      .where {
        font-size: var(--fs-body);
        font-weight: var(--fw-semibold);
        letter-spacing: var(--tr-snug);
        white-space: nowrap;
      }

      /* ---- find ---- */

      .find {
        position: relative;
        display: flex;
        align-items: center;
        width: 220px;
      }

      .find-icon {
        position: absolute;
        left: var(--sp-2);
        color: var(--text-3);
        pointer-events: none;
      }

      .find input {
        height: 28px;
        padding-left: var(--sp-6);
        font-size: var(--fs-sm);
        background: var(--panel-2);
        border-color: transparent;
      }

      .find input:focus {
        background: var(--panel);
        border-color: var(--accent);
      }

      /* ---- alert pulse ---- */

      /*
       * Always-visible count of open alerts. Quiet when there is nothing to do, and coloured
       * only when there is -- a permanently red badge trains people to ignore it.
       */
      /* Quiet when live, since that is the normal state; it only draws the eye when it is not. */
      .live-state {
        display: inline-flex;
        gap: var(--sp-2);
        align-items: center;
        font-size: var(--fs-meta);
        color: var(--warn);
        white-space: nowrap;
      }

      .live-state .dot { background: var(--warn); }
      .live-state.on { color: var(--text-3); }
      .live-state.on .dot { background: var(--ok); }

      .alert-pulse {
        display: inline-flex;
        gap: var(--sp-2);
        align-items: center;
        height: 28px;
        padding: 0 var(--sp-3);
        font-size: var(--fs-sm);
        font-weight: var(--fw-medium);
        color: var(--text-3);
        border: 1px solid var(--line);
        border-radius: var(--r-md);
        white-space: nowrap;
      }

      .alert-pulse:hover {
        color: var(--text);
        background: var(--panel-2);
        text-decoration: none;
      }

      .alert-pulse .dot { background: var(--line-strong); }
      .alert-pulse .num { font-variant-numeric: tabular-nums; }

      .alert-pulse.live {
        color: var(--danger);
        border-color: var(--danger-line);
        background: var(--danger-wash);
      }

      .alert-pulse.live .dot { background: var(--danger); }
      .alert-pulse.live:hover { color: var(--danger); background: var(--danger-wash); }

      /* ---- account ---- */

      .account { position: relative; flex: 0 0 auto; }

      .account-btn {
        display: flex;
        gap: var(--sp-2);
        align-items: center;
        padding: var(--sp-1) var(--sp-1);
        font: inherit;
        color: inherit;
        background: transparent;
        border: none;
        border-radius: var(--r-md);
        cursor: pointer;
        max-width: 190px;
      }

      .account-btn:hover { background: var(--panel-2); }

      .avatar {
        display: grid;
        place-items: center;
        width: 26px;
        height: 26px;
        font-size: var(--fs-micro);
        font-weight: var(--fw-semibold);
        color: var(--accent);
        background: var(--accent-wash);
        border: 1px solid var(--accent-line);
        border-radius: var(--r-sm);
        flex: 0 0 auto;
      }

      .who {
        display: grid;
        text-align: left;
        min-width: 0;
      }

      .who-name {
        font-size: var(--fs-sm);
        font-weight: var(--fw-medium);
        line-height: 1.25;
        overflow: hidden;
        text-overflow: ellipsis;
        white-space: nowrap;
      }

      .who-email {
        font-size: var(--fs-micro);
        color: var(--text-3);
        line-height: 1.25;
        overflow: hidden;
        text-overflow: ellipsis;
        white-space: nowrap;
      }

      .account-menu {
        position: absolute;
        top: calc(100% + 4px);
        right: 0;
        z-index: 40;
      }

      main { flex: 1 1 auto; min-width: 0; }

      .scrim {
        position: fixed;
        inset: 0;
        z-index: 25;
        background: rgb(12 15 19 / 45%);
      }

      /* ---- responsive ---- */

      /*
       * Below 900px the sidebar becomes an overlay drawer rather than a column, and the search
       * box collapses to leave room for the alert count -- on a phone, knowing something is
       * wrong matters more than being able to search from the chrome.
       */
      @media (max-width: 900px) {
        .shell { grid-template-columns: 1fr; }

        .sidebar {
          position: fixed;
          top: 0;
          left: 0;
          width: 248px;
          transform: translateX(-100%);
          transition: transform 0.16s ease;
        }

        .sidebar-open .sidebar { transform: translateX(0); }
        .menu-toggle { display: inline-flex; }
        .find { display: none; }
      }

      @media (min-width: 901px) {
        .menu-toggle { display: none; }
        .scrim { display: none; }
      }

      @media (max-width: 600px) {
        .who { display: none; }
        .alert-word { display: none; }
        .live-word { display: none; }
        .topbar { padding: 0 var(--sp-2); }
        .account-btn { max-width: none; }
      }
    `,
  ],
})
export class ShellComponent implements OnInit, OnDestroy {
  private readonly auth = inject(AuthService);
  private readonly api = inject(ApiService);
  private readonly router = inject(Router);
  private readonly notifications = inject(NotificationService);
  private readonly destroyRef = inject(DestroyRef);

  readonly theme = inject(ThemeService);
  readonly live = inject(RealtimeService);

  readonly user = this.auth.user;
  readonly sidebarOpen = signal(false);
  readonly menuOpen = signal(false);
  readonly openAlerts = signal(0);

  private timer?: ReturnType<typeof setInterval>;

  readonly canSeeAlerts = computed(() => this.auth.has(Permissions.alertView));

  /** Only users who can see something that is pushed get a connection; the API agrees (LiveHub). */
  readonly canGoLive = computed(() =>
    this.auth.hasAny(Permissions.alertView, Permissions.deviceView, Permissions.dashboardView),
  );

  readonly liveTitle = computed(() =>
    this.live.isLive()
      ? 'Receiving live updates'
      : 'Live updates are reconnecting; screens refresh on a timer meanwhile',
  );

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

  private readonly url = signal('');

  /**
   * The name of the current screen, for the top bar.
   *
   * Derived from the nav table rather than from a second list, so a renamed nav item cannot
   * disagree with the heading. The few screens that are not in the nav are named here.
   */
  readonly pageName = computed(() => {
    const path = this.url().split('?')[0];

    const extras: Record<string, string> = {
      '/account': 'Your account',
    };

    if (extras[path]) return extras[path];

    const items = this.groups.flatMap((group) => group.items);

    // Longest match first, so /devices/12 resolves to Devices rather than failing.
    const match = items
      .filter((item) => path === item.path || path.startsWith(item.path + '/'))
      .sort((a, b) => b.path.length - a.path.length)[0];

    if (match && path !== match.path) {
      // A detail route under a list: name the section, since the record's own name is the
      // page heading immediately below.
      return match.label;
    }

    return match?.label ?? 'DevicePulse';
  });

  ngOnInit(): void {
    this.url.set(this.router.url);

    this.router.events.subscribe(() => {
      const next = this.router.url;
      if (next !== this.url()) this.url.set(next);
    });

    if (this.canGoLive()) {
      this.live.connect();
    }

    if (this.canSeeAlerts()) {
      this.refreshAlertCount();

      this.live
        .refreshes({ alerts: true })
        .pipe(takeUntilDestroyed(this.destroyRef))
        .subscribe(() => this.refreshAlertCount());

      // The fallback while the live connection is down. A minute: slow enough to be free, fast
      // enough that the chrome is not lying for long.
      this.timer = setInterval(() => {
        if (!this.live.isLive()) this.refreshAlertCount();
      }, 60_000);
    }
  }

  ngOnDestroy(): void {
    if (this.timer) clearInterval(this.timer);
    this.live.disconnect();
  }

  /** One row, read only for its total: the cheapest way to ask "how many are open". */
  private refreshAlertCount(): void {
    this.api.getAlerts({ status: 'Open', page: 1, pageSize: 1 }).subscribe({
      next: (page) => this.openAlerts.set(page.totalCount),
      // Silent: the error interceptor has already toasted anything worth saying, and a failed
      // count must not produce a toast every minute.
      error: () => undefined,
    });
  }

  search(event: Event): void {
    event.preventDefault();

    const input = (event.target as HTMLFormElement).elements.namedItem('q') as HTMLInputElement | null;
    const term = input?.value.trim() ?? '';

    if (!term) return;

    // Goes through the URL rather than a shared service, so the resulting view is linkable and
    // the back button behaves.
    this.router.navigate(['/devices'], { queryParams: { search: term } });
    if (input) input.value = '';
    this.closeSidebarOnMobile();
  }

  closeSidebarOnMobile(): void {
    if (window.matchMedia('(max-width: 900px)').matches) {
      this.sidebarOpen.set(false);
    }
  }

  signOut(): void {
    this.menuOpen.set(false);
    this.notifications.dismissAll();
    this.live.disconnect();
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
