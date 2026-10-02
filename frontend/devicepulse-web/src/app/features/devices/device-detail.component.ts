import { CommonModule } from '@angular/common';
import { Component, OnInit, computed, inject, input, numberAttribute, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';

import { ApiService } from '../../core/services/api.service';
import { AuthService } from '../../core/auth/auth.service';
import { Permissions } from '../../core/auth/permissions';
import { NotificationService } from '../../core/services/notification.service';
import { fieldErrorsFrom } from '../../core/interceptors/error.interceptor';
import {
  Alert,
  Device,
  DeviceApiKey,
  DeviceType,
  LifecycleStatus,
  Location,
  TelemetryReading,
  TelemetryTrendPoint,
} from '../../core/models/api.models';
import {
  BatteryMeterComponent,
  TrendChartComponent,
} from '../../shared/components/charts.component';
import {
  AlertStatusBadgeComponent,
  ConnectivityBadgeComponent,
  CopyButtonComponent,
  EmptyStateComponent,
  IfPermittedComponent,
  LifecycleBadgeComponent,
  ModalComponent,
  PageHeaderComponent,
  SeverityBadgeComponent,
} from '../../shared/components/ui.components';
import { AbsoluteTimePipe, RelativeTimePipe } from '../../shared/utils/relative-time.pipe';

/**
 * One device: its health, its telemetry history, its alerts and its ingestion credential.
 *
 * The id arrives through withComponentInputBinding, so there is no ActivatedRoute plumbing.
 */
@Component({
  selector: 'dp-device-detail',
  standalone: true,
  imports: [
    CommonModule,
    ReactiveFormsModule,
    RouterLink,
    PageHeaderComponent,
    TrendChartComponent,
    BatteryMeterComponent,
    ConnectivityBadgeComponent,
    LifecycleBadgeComponent,
    SeverityBadgeComponent,
    AlertStatusBadgeComponent,
    EmptyStateComponent,
    ModalComponent,
    IfPermittedComponent,
    CopyButtonComponent,
    RelativeTimePipe,
    AbsoluteTimePipe,
  ],
  template: `
    <div class="page">
      @if (device(); as d) {
        <dp-page-header [title]="d.deviceName" [description]="d.deviceTypeName + ' · ' + d.locationName">
          <a routerLink="/devices" class="btn btn-sm">Back to devices</a>

          <dp-if-permitted [permission]="perm.deviceUpdate">
            <button type="button" class="btn btn-sm" (click)="openEdit()">Edit</button>
          </dp-if-permitted>

          <dp-if-permitted [permission]="perm.deviceManageCredentials">
            @if (d.hasApiKey) {
              <button type="button" class="btn btn-sm" (click)="rotateKey()" [disabled]="busy()">
                Rotate key
              </button>
              <button type="button" class="btn btn-sm" (click)="revokeKey()" [disabled]="busy()">
                Revoke key
              </button>
            } @else {
              <button type="button" class="btn btn-sm btn-primary" (click)="issueKey()" [disabled]="busy()">
                Issue ingestion key
              </button>
            }
          </dp-if-permitted>

          <dp-if-permitted [permission]="perm.deviceRetire">
            @if (d.lifecycleStatus !== 'Retired') {
              <button type="button" class="btn btn-sm btn-danger" (click)="retireOpen.set(true)">
                Retire
              </button>
            }
          </dp-if-permitted>
        </dp-page-header>

        @if (d.lifecycleStatus === 'Retired') {
          <div class="notice" role="status">
            This device is retired. Its history is kept, but it no longer accepts telemetry.
          </div>
        }

        <div class="stack-lg">
          <!-- Current state -->
          <section class="facts grid">
            <div class="card card-body">
              <span class="fact-label">Code</span>
              <span class="mono">{{ d.deviceCode }}</span>
            </div>
            <div class="card card-body">
              <span class="fact-label">Connectivity</span>
              <dp-connectivity [status]="d.connectivityStatus" />
            </div>
            <div class="card card-body">
              <span class="fact-label">Lifecycle</span>
              <dp-lifecycle [status]="d.lifecycleStatus" />
            </div>
            <div class="card card-body">
              <span class="fact-label">Last seen</span>
              <span [title]="d.lastSeenAt ? (d.lastSeenAt | absoluteTime: true) : ''">
                {{ d.lastSeenAt ? (d.lastSeenAt | relativeTime) : 'never reported' }}
              </span>
            </div>
            <div class="card card-body">
              <span class="fact-label">Latest temperature</span>
              <span class="mono">
                {{ latest() ? latest()!.temperature + ' °C' : '—' }}
              </span>
            </div>
            <div class="card card-body">
              <span class="fact-label">Latest battery</span>
              <dp-battery [level]="latest()?.battery ?? null" />
            </div>
            <div class="card card-body">
              <span class="fact-label">Latest signal</span>
              <span class="mono">
                {{ latest() ? latest()!.signalStrength + ' dBm' : '—' }}
              </span>
            </div>
            <div class="card card-body">
              <span class="fact-label">Registered</span>
              <span class="small">{{ d.createdAt | absoluteTime }}</span>
            </div>
          </section>

          <!-- Trend -->
          <section class="card">
            <div class="card-header">
              <h2>Telemetry trend</h2>
              <span class="spacer"></span>
              <div class="pill-group">
                @for (option of trendOptions; track option.hours) {
                  <button
                    type="button"
                    [class.active]="trendHours() === option.hours"
                    (click)="setTrendHours(option.hours)"
                  >
                    {{ option.label }}
                  </button>
                }
              </div>
            </div>
            <div class="card-body">
              <dp-trend-chart [points]="trend()" unit="°C" />
            </div>
          </section>

          <div class="two-up">
            <!-- Readings -->
            <section class="card">
              <div class="card-header">
                <h2>Recent readings</h2>
                <span class="spacer"></span>
                <span class="muted small">newest first</span>
              </div>

              @if (readings().length === 0) {
                <dp-empty
                  title="No readings yet"
                  message="Issue an ingestion key and point a device or the simulator at the API."
                />
              } @else {
                <div class="table-wrap scroll">
                  <table class="data">
                    <thead>
                      <tr>
                        <th>Recorded</th>
                        <th class="right">Temp</th>
                        <th>Battery</th>
                        <th class="right">Signal</th>
                      </tr>
                    </thead>
                    <tbody>
                      @for (reading of readings(); track reading.telemetryId) {
                        <tr>
                          <td class="small nowrap" [title]="reading.recordedAt | absoluteTime: true">
                            {{ reading.recordedAt | relativeTime }}
                          </td>
                          <td class="right mono">{{ reading.temperature }}</td>
                          <td><dp-battery [level]="reading.battery" /></td>
                          <td class="right mono">{{ reading.signalStrength }}</td>
                        </tr>
                      }
                    </tbody>
                  </table>
                </div>
              }
            </section>

            <!-- Alerts -->
            <section class="card">
              <div class="card-header">
                <h2>Alerts</h2>
                <span class="spacer"></span>
                <a [routerLink]="['/alerts']" [queryParams]="{ deviceId: d.deviceId }" class="small">
                  Open in alerts
                </a>
              </div>

              @if (alerts().length === 0) {
                <dp-empty title="No alerts" message="This device has not triggered any rule." />
              } @else {
                <ul class="alert-list">
                  @for (alert of alerts(); track alert.alertId) {
                    <li>
                      <div class="row row-wrap">
                        <dp-severity [severity]="alert.severity" />
                        <dp-alert-status [status]="alert.status" />
                        <span class="spacer"></span>
                        <span class="subtle small">{{ alert.createdAt | relativeTime }}</span>
                      </div>
                      <p class="alert-message">{{ alert.message }}</p>
                    </li>
                  }
                </ul>
              }
            </section>
          </div>
        </div>
      } @else if (loading()) {
        <div class="skeleton" style="height: 240px; border-radius: 12px"></div>
      } @else {
        <dp-empty title="Device not found" message="It may have been removed.">
          <a routerLink="/devices" class="btn btn-primary">Back to devices</a>
        </dp-empty>
      }
    </div>

    <!-- Edit -->
    <dp-modal [open]="editOpen()" title="Edit device" (closed)="editOpen.set(false)">
      <form [formGroup]="editForm" (ngSubmit)="saveEdit()">
        <div class="card-body stack">
          <div class="field">
            <label for="editName">Display name</label>
            <input id="editName" type="text" formControlName="deviceName" />
            @for (message of editErrors('deviceName'); track message) {
              <span class="field-error">{{ message }}</span>
            }
          </div>

          <div class="field">
            <label for="editType">Device type</label>
            <select id="editType" formControlName="deviceTypeId">
              @for (type of deviceTypes(); track type.deviceTypeId) {
                <option [value]="type.deviceTypeId">{{ type.name }}</option>
              }
            </select>
            @for (message of editErrors('deviceTypeId'); track message) {
              <span class="field-error">{{ message }}</span>
            }
          </div>

          <div class="field">
            <label for="editLocation">Location</label>
            <select id="editLocation" formControlName="locationId">
              @for (location of locations(); track location.locationId) {
                <option [value]="location.locationId">{{ location.name }}</option>
              }
            </select>
            @for (message of editErrors('locationId'); track message) {
              <span class="field-error">{{ message }}</span>
            }
          </div>

          <div class="field">
            <label for="editLifecycle">Lifecycle status</label>
            <select id="editLifecycle" formControlName="lifecycleStatus">
              <option value="Registered">Registered</option>
              <option value="Active">Active</option>
              <option value="Inactive">Inactive</option>
            </select>
            <span class="field-hint">
              Connectivity is not set here — the system derives Online and Offline from telemetry.
            </span>
          </div>
        </div>

        <div class="card-footer row">
          <span class="spacer"></span>
          <button type="button" class="btn" (click)="editOpen.set(false)">Cancel</button>
          <button type="submit" class="btn btn-primary" [disabled]="busy()">
            @if (busy()) {
              <span class="spinner"></span>
            }
            Save
          </button>
        </div>
      </form>
    </dp-modal>

    <!-- Retire -->
    <dp-modal [open]="retireOpen()" title="Retire this device" (closed)="retireOpen.set(false)">
      <div class="card-body stack">
        <p class="small">
          Retiring keeps the device and all of its telemetry and alert history, hides it from the
          active device list, closes its open alerts, and revokes its ingestion key. It is not a
          deletion, and it can be undone by setting the lifecycle back to Active.
        </p>

        <div class="field">
          <label for="retireReason">Reason (recorded in the audit log)</label>
          <input id="retireReason" type="text" [formControl]="retireReason" placeholder="Decommissioned" />
        </div>
      </div>

      <div class="card-footer row">
        <span class="spacer"></span>
        <button type="button" class="btn" (click)="retireOpen.set(false)">Cancel</button>
        <button type="button" class="btn btn-danger" (click)="retire()" [disabled]="busy()">
          Retire device
        </button>
      </div>
    </dp-modal>

    <!-- The issued key, shown exactly once -->
    <dp-modal [open]="issuedKey() !== null" title="Ingestion key issued" (closed)="issuedKey.set(null)">
      <div class="card-body stack">
        <p class="small">
          Copy this now. It is stored only as a hash, so it cannot be shown again — if it is
          lost, issue a new one.
        </p>

        <div class="key-box mono">{{ issuedKey()?.apiKey }}</div>

        <dp-copy [value]="issuedKey()?.apiKey ?? ''" label="ingestion key" />

        <p class="field-hint">
          The device sends it as the <code>X-Device-Key</code> header on
          <code>POST /api/v1/telemetry</code>. It authorises that one device to submit its own
          readings and nothing else.
        </p>
      </div>

      <div class="card-footer row">
        <span class="spacer"></span>
        <button type="button" class="btn btn-primary" (click)="issuedKey.set(null)">Done</button>
      </div>
    </dp-modal>
  `,
  styles: [
    `
      .notice {
        padding: 0.65rem 0.9rem;
        margin-bottom: 1rem;
        font-size: 0.85rem;
        color: var(--warn);
        background: var(--warn-soft);
        border-radius: var(--radius);
      }

      .facts { grid-template-columns: repeat(auto-fit, minmax(170px, 1fr)); }

      .facts .card-body {
        display: grid;
        gap: 0.3rem;
        padding: 0.75rem 0.9rem;
        align-content: start;
        justify-items: start;
      }

      .fact-label {
        font-size: 0.7rem;
        font-weight: 600;
        text-transform: uppercase;
        letter-spacing: 0.04em;
        color: var(--text-muted);
      }

      .two-up {
        display: grid;
        gap: 1rem;
        grid-template-columns: minmax(0, 1.3fr) minmax(0, 1fr);
      }

      @media (max-width: 1050px) {
        .two-up { grid-template-columns: 1fr; }
      }

      .scroll { max-height: 420px; overflow-y: auto; }

      .alert-list {
        margin: 0;
        padding: 0;
        list-style: none;
        max-height: 420px;
        overflow-y: auto;
      }

      .alert-list li {
        padding: 0.7rem 1.1rem;
        border-bottom: 1px solid var(--border);
      }

      .alert-list li:last-child { border-bottom: none; }

      .alert-message {
        margin: 0.35rem 0 0;
        font-size: 0.82rem;
        line-height: 1.4;
      }

      .key-box {
        padding: 0.7rem 0.8rem;
        font-size: 0.8rem;
        background: var(--surface-3);
        border: 1px solid var(--border);
        border-radius: var(--radius);
        word-break: break-all;
        user-select: all;
      }
    `,
  ],
})
export class DeviceDetailComponent implements OnInit {
  private readonly api = inject(ApiService);
  private readonly auth = inject(AuthService);
  private readonly notifications = inject(NotificationService);
  private readonly router = inject(Router);
  private readonly fb = inject(FormBuilder);

  readonly perm = Permissions;

  /** Route parameter, bound by withComponentInputBinding. */
  readonly id = input.required({ transform: numberAttribute });

  readonly device = signal<Device | null>(null);
  readonly latest = signal<TelemetryReading | null>(null);
  readonly readings = signal<TelemetryReading[]>([]);
  readonly trend = signal<TelemetryTrendPoint[]>([]);
  readonly alerts = signal<Alert[]>([]);
  readonly deviceTypes = signal<DeviceType[]>([]);
  readonly locations = signal<Location[]>([]);

  readonly loading = signal(true);
  readonly busy = signal(false);
  readonly editOpen = signal(false);
  readonly retireOpen = signal(false);
  readonly issuedKey = signal<DeviceApiKey | null>(null);
  readonly trendHours = signal(24);

  readonly trendOptions = [
    { hours: 6, label: '6h' },
    { hours: 24, label: '24h' },
    { hours: 168, label: '7d' },
  ];

  readonly retireReason = this.fb.nonNullable.control('');

  private serverErrors: Record<string, string[]> = {};

  readonly editForm = this.fb.nonNullable.group({
    deviceName: ['', [Validators.required, Validators.maxLength(200)]],
    deviceTypeId: [0, [Validators.min(1)]],
    locationId: [0, [Validators.min(1)]],
    lifecycleStatus: ['Active' as LifecycleStatus],
  });

  ngOnInit(): void {
    this.loadDevice();
    this.loadTelemetry();
    this.loadAlerts();
    this.loadReferenceData();
  }

  private loadDevice(): void {
    this.api.getDevice(this.id()).subscribe({
      next: (device) => {
        this.device.set(device);
        this.loading.set(false);
      },
      error: () => {
        this.device.set(null);
        this.loading.set(false);
      },
    });
  }

  private loadTelemetry(): void {
    if (!this.auth.has(Permissions.telemetryView)) {
      return;
    }

    this.api.getLatestTelemetry(this.id()).subscribe({ next: (reading) => this.latest.set(reading) });

    this.api
      .getDeviceTelemetry(this.id(), 1, 50)
      .subscribe({ next: (page) => this.readings.set(page.items) });

    this.loadTrend();
  }

  private loadTrend(): void {
    if (!this.auth.has(Permissions.telemetryView)) {
      return;
    }

    this.api.getDeviceTrend(this.id(), this.trendHours()).subscribe({
      next: (points) => this.trend.set(points),
    });
  }

  setTrendHours(hours: number): void {
    this.trendHours.set(hours);
    this.loadTrend();
  }

  private loadAlerts(): void {
    if (!this.auth.has(Permissions.alertView)) {
      return;
    }

    this.api
      .getAlerts({ deviceId: this.id(), pageSize: 25 })
      .subscribe({ next: (page) => this.alerts.set(page.items) });
  }

  private loadReferenceData(): void {
    if (!this.auth.has(Permissions.referenceDataView)) {
      return;
    }

    this.api.getDeviceTypes().subscribe({ next: (types) => this.deviceTypes.set(types) });
    this.api.getLocations().subscribe({ next: (locations) => this.locations.set(locations) });
  }

  openEdit(): void {
    const device = this.device();

    if (!device) {
      return;
    }

    this.serverErrors = {};

    this.editForm.reset({
      deviceName: device.deviceName,
      deviceTypeId: device.deviceTypeId,
      locationId: device.locationId,
      // A retired device is edited back to Active, since the Retired option is not offered
      // here — reactivation is the only meaningful edit for one.
      lifecycleStatus: device.lifecycleStatus === 'Retired' ? 'Active' : device.lifecycleStatus,
    });

    this.editOpen.set(true);
  }

  editErrors(field: string): string[] {
    const control = this.editForm.get(field);
    const messages: string[] = [];

    if (control && (control.dirty || control.touched)) {
      if (control.hasError('required')) messages.push('This field is required.');
      if (control.hasError('maxlength')) messages.push('That is too long.');
      if (control.hasError('min')) messages.push('Choose an option.');
    }

    return [...messages, ...(this.serverErrors[field] ?? [])];
  }

  saveEdit(): void {
    const device = this.device();
    this.serverErrors = {};

    if (!device || this.editForm.invalid) {
      this.editForm.markAllAsTouched();
      return;
    }

    this.busy.set(true);
    const value = this.editForm.getRawValue();

    this.api
      .updateDevice(device.deviceId, {
        deviceName: value.deviceName.trim(),
        deviceTypeId: Number(value.deviceTypeId),
        locationId: Number(value.locationId),
        lifecycleStatus: value.lifecycleStatus,
        // Sent back so the API rejects the edit if someone else changed the device after this
        // form was opened, rather than silently overwriting them (Appendix D.1).
        rowVersion: device.rowVersion,
      })
      .subscribe({
        next: (updated) => {
          this.busy.set(false);
          this.editOpen.set(false);
          this.device.set(updated);
          this.notifications.success('Device updated.');
        },
        error: (error: unknown) => {
          this.busy.set(false);
          this.serverErrors = fieldErrorsFrom(error);
        },
      });
  }

  issueKey(): void {
    this.busy.set(true);

    this.api.issueDeviceApiKey(this.id()).subscribe({
      next: (key) => {
        this.busy.set(false);
        this.issuedKey.set(key);
        this.loadDevice();
      },
      error: () => this.busy.set(false),
    });
  }

  rotateKey(): void {
    // Issuing replaces the previous key, so rotation is the same call. Confirmed first because
    // it immediately stops the device's current key working.
    const confirmed = window.confirm(
      'Issuing a new key immediately invalidates the current one. The device will stop reporting until it is given the new key. Continue?',
    );

    if (confirmed) {
      this.issueKey();
    }
  }

  revokeKey(): void {
    const confirmed = window.confirm(
      'Revoking the key stops this device submitting telemetry until a new key is issued. Continue?',
    );

    if (!confirmed) {
      return;
    }

    this.busy.set(true);

    this.api.revokeDeviceApiKey(this.id()).subscribe({
      next: () => {
        this.busy.set(false);
        this.notifications.success('Ingestion key revoked.');
        this.loadDevice();
      },
      error: () => this.busy.set(false),
    });
  }

  retire(): void {
    this.busy.set(true);

    this.api.retireDevice(this.id(), this.retireReason.value.trim() || null).subscribe({
      next: () => {
        this.busy.set(false);
        this.retireOpen.set(false);
        this.notifications.success('Device retired. Its history has been kept.');
        void this.router.navigate(['/devices']);
      },
      error: () => this.busy.set(false),
    });
  }
}
