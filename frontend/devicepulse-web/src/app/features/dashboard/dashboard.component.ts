import { CommonModule } from '@angular/common';
import { Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';

import { ApiService } from '../../core/services/api.service';
import { DashboardSummary } from '../../core/models/api.models';
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
 */
@Component({
  selector: 'dp-dashboard',
  standalone: true,
  imports: [
    CommonModule,
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
    RelativeTimePipe,
  ],
  template: `
    <div class="page">
      <dp-page-header
        title="Dashboard"
        description="Fleet health, open alerts and recent telemetry."
      >
        <span class="muted small">
          @if (summary()) {
            Updated {{ summary()!.generatedAt | relativeTime }}
          }
        </span>

        <button type="button" class="btn btn-sm" (click)="load(true)" [disabled]="refreshing()">
          @if (refreshing()) {
            <span class="spinner"></span>
          }
          Refresh
        </button>
      </dp-page-header>

      @if (loading()) {
        <div class="grid grid-auto">
          @for (i of [1, 2, 3, 4, 5]; track i) {
            <div class="skeleton" style="height: 86px; border-radius: 12px"></div>
          }
        </div>
      } @else {
        @if (summary(); as data) {
        <div class="stack-lg">
          <!-- Counters -->
          <section class="grid grid-auto">
            <dp-stat label="Devices" [value]="data.devices.total" [hint]="data.devices.retired + ' retired'" />
            <dp-stat label="Online" [value]="data.devices.online" color="var(--ok)" [hint]="onlineShare()" />
            <dp-stat label="Offline" [value]="data.devices.offline" color="var(--danger)" hint="no recent telemetry" />
            <dp-stat label="Open alerts" [value]="data.alerts.open" color="var(--warn)" [hint]="data.alerts.acknowledged + ' acknowledged'" />
            <dp-stat
              label="Critical"
              [value]="data.alerts.critical"
              [color]="data.alerts.critical > 0 ? 'var(--sev-critical)' : ''"
              [accent]="data.alerts.critical > 0"
              [hint]="data.alerts.resolvedToday + ' resolved today'"
            />
          </section>

          <!-- Trend + severity -->
          <section class="two-up">
            <div class="card">
              <div class="card-header">
                <h2>Temperature, last {{ data.trendHours }}h</h2>
                <span class="spacer"></span>
                <span class="muted small">hourly average across the fleet</span>
              </div>
              <div class="card-body">
                <dp-trend-chart [points]="data.temperatureTrend" unit="°C" />
              </div>
            </div>

            <div class="card">
              <div class="card-header"><h2>Unresolved by severity</h2></div>
              <div class="card-body">
                <dp-donut
                  [slices]="severitySlices()"
                  centreLabel="unresolved"
                  emptyMessage="No unresolved alerts. Everything is quiet."
                />
              </div>
            </div>
          </section>

          <!-- Needs attention -->
          <section class="card">
            <div class="card-header">
              <h2>Needs attention</h2>
              <span class="spacer"></span>
              <a routerLink="/devices" class="small">All devices</a>
            </div>

            @if (data.devicesNeedingAttention.length === 0) {
              <dp-empty
                title="Nothing needs attention"
                message="Every device is reporting and no alerts are open."
              />
            } @else {
              <div class="table-wrap">
                <table class="data">
                  <thead>
                    <tr>
                      <th>Device</th>
                      <th>Location</th>
                      <th>Connectivity</th>
                      <th>Last seen</th>
                      <th>Temp</th>
                      <th>Battery</th>
                      <th>Alerts</th>
                      <th>Worst</th>
                    </tr>
                  </thead>
                  <tbody>
                    @for (row of data.devicesNeedingAttention; track row.deviceId) {
                      <tr>
                        <td>
                          <a [routerLink]="['/devices', row.deviceId]">{{ row.deviceName }}</a>
                          <div class="mono subtle small">{{ row.deviceCode }}</div>
                        </td>
                        <td class="muted">{{ row.locationName }}</td>
                        <td><dp-connectivity [status]="row.connectivityStatus" /></td>
                        <td class="muted small nowrap">
                          {{ row.lastSeenAt ? (row.lastSeenAt | relativeTime) : 'never' }}
                        </td>
                        <td class="mono nowrap">
                          {{ row.lastTemperature !== null ? row.lastTemperature + '°C' : '—' }}
                        </td>
                        <td><dp-battery [level]="row.lastBattery" /></td>
                        <td class="mono">{{ row.openAlertCount || '—' }}</td>
                        <td>
                          @if (row.highestOpenSeverity) {
                            <dp-severity [severity]="row.highestOpenSeverity" />
                          } @else {
                            <span class="subtle">—</span>
                          }
                        </td>
                      </tr>
                    }
                  </tbody>
                </table>
              </div>
            }
          </section>

          <!-- Distribution + recent alerts -->
          <section class="two-up">
            <div class="stack">
              <div class="card">
                <div class="card-header">
                  <h2>Devices by location</h2>
                  <span class="spacer"></span>
                  <a routerLink="/reference-data" class="small">Manage</a>
                </div>
                <div class="card-body">
                  <dp-bar-chart [data]="locationBars()" secondaryLabel="online" />
                </div>
              </div>

              <div class="card">
                <div class="card-header"><h2>Devices by type</h2></div>
                <div class="card-body">
                  <dp-bar-chart [data]="typeBars()" />
                </div>
              </div>
            </div>

            <div class="card">
              <div class="card-header">
                <h2>Recent alerts</h2>
                <span class="spacer"></span>
                <a routerLink="/alerts" class="small">All alerts</a>
              </div>

              @if (data.recentAlerts.length === 0) {
                <dp-empty title="No alerts yet" message="Alerts appear here as rules are triggered." />
              } @else {
                <ul class="alert-feed">
                  @for (alert of data.recentAlerts; track alert.alertId) {
                    <li>
                      <span class="bar" [style.background]="colorFor(alert.severity)"></span>
                      <div class="feed-body">
                        <p class="feed-message">{{ alert.message }}</p>
                        <p class="feed-meta subtle small">
                          <dp-severity [severity]="alert.severity" />
                          <span>{{ alert.status }}</span>
                          <span>&middot;</span>
                          <span>{{ alert.createdAt | relativeTime }}</span>
                        </p>
                      </div>
                    </li>
                  }
                </ul>
              }
            </div>
          </section>
        </div>
        } @else {
          <dp-empty
            title="Could not load the dashboard"
            message="The API did not respond. Check that it is running, then try again."
          >
            <button type="button" class="btn btn-primary" (click)="load(true)">Try again</button>
          </dp-empty>
        }
      }
    </div>
  `,
  styles: [
    `
      .two-up {
        display: grid;
        gap: 1rem;
        grid-template-columns: minmax(0, 1.6fr) minmax(0, 1fr);
      }

      @media (max-width: 1100px) {
        .two-up { grid-template-columns: 1fr; }
      }

      .alert-feed {
        margin: 0;
        padding: 0;
        list-style: none;
        max-height: 420px;
        overflow-y: auto;
      }

      .alert-feed li {
        display: flex;
        gap: 0.7rem;
        padding: 0.7rem 1.1rem;
        border-bottom: 1px solid var(--border);
      }

      .alert-feed li:last-child { border-bottom: none; }

      .bar {
        width: 3px;
        border-radius: 2px;
        flex: 0 0 auto;
      }

      .feed-body { min-width: 0; }

      .feed-message {
        margin: 0;
        font-size: 0.82rem;
        line-height: 1.4;
      }

      .feed-meta {
        display: flex;
        gap: 0.4rem;
        align-items: center;
        margin: 0.3rem 0 0;
        flex-wrap: wrap;
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
