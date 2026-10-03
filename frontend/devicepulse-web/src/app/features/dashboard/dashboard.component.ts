import { Component, OnDestroy, OnInit, computed, inject, signal, ChangeDetectionStrategy } from '@angular/core';
import { RouterLink } from '@angular/router';

import { ApiService } from '../../core/services/api.service';
import { DashboardSummary, DeviceHealthRow } from '../../core/models/api.models';
import { environment } from '../../../environments/environment';
import {
  BarChartComponent,
  BarDatum,
  BatteryMeterComponent,
  DonutChartComponent,
  DonutSlice,
  TrendChartComponent,
} from '../../shared/components/charts.component';
import {
  ConnectivityBadgeComponent,
  EmptyStateComponent,
  PageHeaderComponent,
  SeverityBadgeComponent,
  StatComponent,
  severityColor,
} from '../../shared/components/ui.components';
import { RelativeTimePipe } from '../../shared/utils/relative-time.pipe';

/**
 * The monitoring dashboard (§30).
 *
 * One request fills the whole page — the API aggregates everything server-side rather than
 * having the SPA fire six calls and count rows itself.
 *
 * It polls rather than pushing. That is the ordering the master reference asks for (§31): get
 * the data right over plain HTTP first, and introduce SignalR when there is a reason beyond
 * novelty. The interval is a few seconds longer than a device's reporting interval, so the
 * numbers move without hammering the API.
 *
 * The layout is ordered by what an operator does, not by what is easiest to arrange. Opening
 * this page asks one question — is anything wrong, and what do I do about it — so the screen
 * answers in that order:
 *
 *   1. A verdict. One line that says whether the fleet is healthy, and a single display-size
 *      figure for the number that would make it not healthy.
 *   2. The work. The devices needing attention, full width, directly under the verdict, with
 *      failing rows tinted so they can be found without reading every cell.
 *   3. The context. Trends and distributions, below and visually quieter, for when the first
 *      two have raised a question worth investigating.
 *
 * What it deliberately is not: a row of five equal-weight metric cards above two charts above a
 * recent-activity feed. In that arrangement "Devices: 24" shouts exactly as loudly as
 * "Critical: 3", which leaves the operator to do the triage the screen should have done.
 */
@Component({
  selector: 'dp-dashboard',
  standalone: true,
  imports: [
    RouterLink,
    PageHeaderComponent,
    StatComponent,
    TrendChartComponent,
    BarChartComponent,
    DonutChartComponent,
    BatteryMeterComponent,
    ConnectivityBadgeComponent,
    SeverityBadgeComponent,
    EmptyStateComponent,
    RelativeTimePipe
],
  template: `
    <div class="page">
      <dp-page-header title="Fleet" [context]="updatedLabel()">
        <button type="button" class="btn btn-sm" (click)="load(true)" [disabled]="refreshing()">
          @if (refreshing()) {
            <span class="spinner"></span>
          }
          Refresh
        </button>
      </dp-page-header>

      @if (loading()) {
        <div class="stack-lg">
          <div class="skeleton" style="height: 64px"></div>
          <div class="skeleton" style="height: 220px"></div>
        </div>
      } @else if (summary(); as data) {
        <div class="stack-lg">
          <!-- 1. The verdict ------------------------------------------------------------- -->
          <section class="verdict" [class]="verdictTone()">
            <div class="verdict-line">
              <span class="dot"></span>
              <p class="verdict-text">{{ verdict() }}</p>
            </div>

            <div class="readouts">
              <dp-stat
                label="Open alerts"
                [value]="data.alerts.open"
                [lead]="true"
                [color]="data.alerts.open > 0 ? 'var(--danger)' : ''"
                [hint]="data.alerts.acknowledged + ' acknowledged'"
              />
              <dp-stat
                label="Critical"
                [value]="data.alerts.critical"
                [color]="data.alerts.critical > 0 ? 'var(--sev-critical)' : ''"
                [hint]="data.alerts.resolvedToday + ' resolved today'"
              />
              <dp-stat
                label="Offline"
                [value]="data.devices.offline"
                [color]="data.devices.offline > 0 ? 'var(--danger)' : ''"
                hint="no recent telemetry"
              />
              <dp-stat label="Reporting" [value]="data.devices.online" [hint]="onlineShare()" />
              <dp-stat
                label="Devices"
                [value]="data.devices.total"
                [hint]="data.devices.retired + ' retired'"
              />
            </div>
          </section>

          <!-- 2. The work --------------------------------------------------------------- -->
          <section class="section">
            <div class="section-head">
              <h2>Needs attention</h2>
              <span class="label">{{ data.devicesNeedingAttention.length }}</span>
              <span class="spacer"></span>
              <a routerLink="/devices" class="small">All devices</a>
            </div>

            @if (data.devicesNeedingAttention.length === 0) {
              <div class="panel">
                <dp-empty
                  title="Nothing needs attention"
                  message="Every device is reporting and no alerts are open."
                />
              </div>
            } @else {
              <div class="panel">
                <div class="table-wrap">
                  <table class="data">
                    <thead>
                      <tr>
                        <th>Device</th>
                        <th>Location</th>
                        <th>Connectivity</th>
                        <th>Last seen</th>
                        <th class="right">Temp</th>
                        <th>Battery</th>
                        <th class="right">Alerts</th>
                        <th>Worst</th>
                      </tr>
                    </thead>
                    <tbody>
                      @for (row of data.devicesNeedingAttention; track row.deviceId) {
                        <tr [class]="rowTone(row)">
                          <td class="primary">
                            <a [routerLink]="['/devices', row.deviceId]">{{ row.deviceName }}</a>
                            <div class="mono text-3 small">{{ row.deviceCode }}</div>
                          </td>
                          <td class="text-2">{{ row.locationName }}</td>
                          <td><dp-connectivity [status]="row.connectivityStatus" /></td>
                          <td class="text-2 small nowrap">
                            {{ row.lastSeenAt ? (row.lastSeenAt | relativeTime) : 'never' }}
                          </td>
                          <td class="num right nowrap">
                            {{ row.lastTemperature !== null ? row.lastTemperature + '°C' : '—' }}
                          </td>
                          <td><dp-battery [level]="row.lastBattery" /></td>
                          <td class="num right">{{ row.openAlertCount || '—' }}</td>
                          <td>
                            @if (row.highestOpenSeverity) {
                              <dp-severity [severity]="row.highestOpenSeverity" />
                            } @else {
                              <span class="text-3">—</span>
                            }
                          </td>
                        </tr>
                      }
                    </tbody>
                  </table>
                </div>
              </div>
            }
          </section>

          <!-- 3. The context ------------------------------------------------------------ -->
          <section class="section context">
            <div class="section-head">
              <h2>Context</h2>
              <span class="label">last {{ data.trendHours }} hours</span>
            </div>

            <div class="split">
              <div class="panel">
                <div class="panel-head">
                  <h3>Fleet temperature</h3>
                  <span class="spacer"></span>
                  <span class="label">hourly average</span>
                </div>
                <div class="panel-body">
                  <dp-trend-chart [points]="data.temperatureTrend" unit="°C" />
                </div>
              </div>

              <div class="panel">
                <div class="panel-head">
                  <h3>Unresolved by severity</h3>
                </div>
                <div class="panel-body">
                  <dp-donut
                    [slices]="severitySlices()"
                    centreLabel="unresolved"
                    emptyMessage="No unresolved alerts."
                  />
                </div>
              </div>
            </div>

            <div class="split">
              <div class="panel">
                <div class="panel-head">
                  <h3>Recent alerts</h3>
                  <span class="spacer"></span>
                  <a routerLink="/alerts" class="small">All alerts</a>
                </div>

                @if (data.recentAlerts.length === 0) {
                  <dp-empty title="No alerts yet" message="Alerts appear here as rules are triggered." />
                } @else {
                  <ul class="feed">
                    @for (alert of data.recentAlerts; track alert.alertId) {
                      <li>
                        <span class="rail" [style.background]="colorFor(alert.severity)"></span>
                        <div class="feed-body">
                          <p class="feed-message">{{ alert.message }}</p>
                          <p class="feed-meta">
                            <dp-severity [severity]="alert.severity" />
                            <span class="text-3">{{ alert.status }}</span>
                            <span class="text-3">&middot;</span>
                            <span class="text-3">{{ alert.createdAt | relativeTime }}</span>
                          </p>
                        </div>
                      </li>
                    }
                  </ul>
                }
              </div>

              <div class="stack">
                <div class="panel">
                  <div class="panel-head">
                    <h3>By location</h3>
                    <span class="spacer"></span>
                    <a routerLink="/reference-data" class="small">Manage</a>
                  </div>
                  <div class="panel-body">
                    <dp-bar-chart [data]="locationBars()" secondaryLabel="online" />
                  </div>
                </div>

                <div class="panel">
                  <div class="panel-head"><h3>By type</h3></div>
                  <div class="panel-body">
                    <dp-bar-chart [data]="typeBars()" />
                  </div>
                </div>
              </div>
            </div>
          </section>
        </div>
      } @else {
        <div class="panel">
          <dp-empty
            title="Could not load the dashboard"
            message="The API did not respond. Check that it is running, then try again."
          >
            <button type="button" class="btn btn-primary" (click)="load(true)">Try again</button>
          </dp-empty>
        </div>
      }
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.Eager,
  styles: [
    `
      /*
       * The verdict band. A left rule carries the status colour rather than a filled panel: the
       * colour has to be findable from across a room without turning the top of the screen into
       * a block of red.
       */
      .verdict {
        display: grid;
        gap: var(--sp-5);
        padding: var(--sp-4) var(--sp-5);
        border-left: 3px solid var(--line-strong);
        background: var(--panel);
        border-radius: 0 var(--r-md) var(--r-md) 0;
      }

      .verdict-line {
        display: flex;
        gap: var(--sp-3);
        align-items: baseline;
      }

      .verdict-line .dot {
        width: 7px;
        height: 7px;
        margin-top: 6px;
        background: var(--line-strong);
      }

      .verdict-text {
        margin: 0;
        font-size: var(--fs-lg);
        font-weight: var(--fw-medium);
        letter-spacing: var(--tr-snug);
      }

      .verdict-ok { border-left-color: var(--ok); }
      .verdict-ok .dot { background: var(--ok); }

      .verdict-warn { border-left-color: var(--warn); }
      .verdict-warn .dot { background: var(--warn); }

      .verdict-danger { border-left-color: var(--danger); }
      .verdict-danger .dot { background: var(--danger); }

      /* Supporting context recedes: smaller headings, quieter chart labels. */
      .context .panel-head h3 { color: var(--text-2); }

      /* ---- recent alerts feed ---- */

      .feed {
        margin: 0;
        padding: 0;
        list-style: none;
        max-height: 340px;
        overflow-y: auto;
      }

      .feed li {
        display: flex;
        gap: var(--sp-3);
        padding: var(--sp-3) var(--sp-4);
        border-bottom: 1px solid var(--line);
      }

      .feed li:last-child { border-bottom: none; }

      .rail {
        width: 2px;
        border-radius: 1px;
        flex: 0 0 auto;
      }

      .feed-body { min-width: 0; }

      .feed-message {
        margin: 0;
        font-size: var(--fs-sm);
        line-height: var(--lh-snug);
      }

      .feed-meta {
        display: flex;
        gap: var(--sp-2);
        align-items: center;
        margin: var(--sp-1) 0 0;
        flex-wrap: wrap;
        font-size: var(--fs-meta);
      }

      @media (max-width: 600px) {
        .verdict { padding: var(--sp-3) var(--sp-4); }
        .verdict-text { font-size: var(--fs-body); }
      }
    `,
  ],
})
export class DashboardComponent implements OnInit, OnDestroy {
  private readonly api = inject(ApiService);

  readonly summary = signal<DashboardSummary | null>(null);
  readonly loading = signal(true);
  readonly refreshing = signal(false);

  private timer: ReturnType<typeof setInterval> | null = null;

  ngOnInit(): void {
    this.load();

    this.timer = setInterval(() => {
      // Skipped while the tab is hidden: polling a dashboard nobody is looking at is pure load
      // on the API and the database for no benefit.
      if (document.visibilityState === 'visible') {
        this.load();
      }
    }, environment.dashboardRefreshMs);
  }

  ngOnDestroy(): void {
    if (this.timer) {
      clearInterval(this.timer);
    }
  }

  load(showSpinner = false): void {
    if (showSpinner) {
      this.refreshing.set(true);
    }

    this.api.getDashboard().subscribe({
      next: (data) => {
        this.summary.set(data);
        this.loading.set(false);
        this.refreshing.set(false);
      },
      error: () => {
        // The error interceptor has already told the user. The previous snapshot is kept on
        // screen rather than blanked: stale numbers with a visible timestamp are more useful
        // than an empty page, and a single failed poll should not wipe the dashboard.
        this.loading.set(false);
        this.refreshing.set(false);
      },
    });
  }

  /**
   * The sentence at the top of the screen.
   *
   * Written to be read rather than decoded, and specific about the thing that is wrong: an
   * operator should be able to act on this line alone without looking at the figures beneath it.
   */
  readonly verdict = computed(() => {
    const data = this.summary();
    if (!data) return '';

    const parts: string[] = [];

    if (data.alerts.critical > 0) {
      parts.push(`${data.alerts.critical} critical ${data.alerts.critical === 1 ? 'alert' : 'alerts'}`);
    }

    const otherOpen = data.alerts.open - data.alerts.critical;
    if (otherOpen > 0) {
      parts.push(`${otherOpen} other open`);
    }

    if (data.devices.offline > 0) {
      parts.push(`${data.devices.offline} ${data.devices.offline === 1 ? 'device' : 'devices'} offline`);
    }

    if (parts.length === 0) {
      return data.devices.total === 0
        ? 'No devices registered yet.'
        : `All ${data.devices.total} devices reporting, no open alerts.`;
    }

    return `${parts.join(' · ')}.`;
  });

  readonly verdictTone = computed(() => {
    const data = this.summary();
    if (!data) return '';

    if (data.alerts.critical > 0 || data.devices.offline > 0) return 'verdict-danger';
    if (data.alerts.open > 0) return 'verdict-warn';

    return 'verdict-ok';
  });

  readonly updatedLabel = computed(() => {
    const data = this.summary();
    if (!data) return '';

    const seconds = Math.max(0, Math.round((Date.now() - new Date(data.generatedAt).getTime()) / 1000));

    return seconds < 10 ? 'updated just now' : `updated ${seconds}s ago`;
  });

  /** Tints a row so the failing devices can be found without reading every cell. */
  rowTone(row: DeviceHealthRow): string {
    if (row.connectivityStatus === 'Offline') return 'row-danger';
    if (row.highestOpenSeverity === 'Critical' || row.highestOpenSeverity === 'High') return 'row-danger';
    if (row.highestOpenSeverity === 'Medium' || row.highestOpenSeverity === 'Low') return 'row-warn';

    return '';
  }

  readonly onlineShare = computed(() => {
    const data = this.summary();

    if (!data || data.devices.total === 0) {
      return '';
    }

    return `${Math.round((data.devices.online / data.devices.total) * 100)}% of fleet`;
  });

  readonly severitySlices = computed<DonutSlice[]>(() => {
    const data = this.summary();

    if (!data) {
      return [];
    }

    // Ordered worst first so the donut reads as an ordering rather than an arbitrary arrangement.
    const order = ['Critical', 'High', 'Medium', 'Low', 'Info'] as const;

    return order
      .map((severity) => ({
        label: severity,
        value: data.alertsBySeverity.find((b) => b.severity === severity)?.count ?? 0,
        color: severityColor(severity),
      }))
      .filter((slice) => slice.value > 0);
  });

  readonly locationBars = computed<BarDatum[]>(() =>
    (this.summary()?.devicesByLocation ?? []).map((bucket) => ({
      label: bucket.location,
      value: bucket.total,
      secondary: bucket.online,
    })),
  );

  readonly typeBars = computed<BarDatum[]>(() =>
    (this.summary()?.devicesByType ?? []).map((bucket) => ({
      label: bucket.deviceType,
      value: bucket.count,
    })),
  );

  colorFor = severityColor;
}
