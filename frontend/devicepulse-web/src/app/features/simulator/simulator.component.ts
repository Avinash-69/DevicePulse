import { CommonModule } from '@angular/common';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';

import { ApiService } from '../../core/services/api.service';
import { AuthService } from '../../core/auth/auth.service';
import { Permissions } from '../../core/auth/permissions';
import { NotificationService } from '../../core/services/notification.service';
import { messageFrom } from '../../core/interceptors/error.interceptor';
import { Device } from '../../core/models/api.models';
import {
  CopyButtonComponent,
  IfPermittedComponent,
  PageHeaderComponent,
} from '../../shared/components/ui.components';

/**
 * IoT simulator (§19, §20).
 *
 * An honest note about what this page is and is not.
 *
 * §20 of the master reference sketches Start and Stop buttons. This page deliberately does not
 * have them, because the simulator is a separate console application and the API has no endpoint
 * that launches a process. Rendering buttons that only pretend to work would violate Development
 * Rule 3 — every feature claimed must actually exist — and would be worse than useless the first
 * time someone pressed one in an interview demo.
 *
 * What it does instead is the part that is real and genuinely useful:
 *   * builds the exact command line for a chosen configuration, so a load-test run is copy-paste,
 *   * shows live ingestion state so the effect of a run is visible here,
 *   * sends an individual reading through the real ingestion endpoint, for testing a rule
 *     without starting the simulator at all.
 *
 * Remote start/stop is listed in the README as future work, and needs a backend process
 * supervisor to be anything other than theatre.
 */
@Component({
  selector: 'dp-simulator',
  standalone: true,
  imports: [
    CommonModule,
    ReactiveFormsModule,
    PageHeaderComponent,
    CopyButtonComponent,
    IfPermittedComponent,
  ],
  template: `
    <div class="page">
      <dp-page-header
        title="Simulator"
        description="Generate realistic telemetry for a fleet of virtual devices, and test the ingestion and alerting pipeline."
      />

      <div class="two-up">
        <div class="stack">
          <!-- Command builder -->
          <section class="card">
            <div class="card-header">
              <h2>Build a run</h2>
              <span class="spacer"></span>
              <span class="muted small">every value below is a command-line override</span>
            </div>

            <div class="card-body stack">
              <div class="grid-two">
                <div class="field">
                  <label for="deviceCount">Devices</label>
                  <input id="deviceCount" type="number" min="1" max="5000" [formControl]="form.controls.deviceCount" />
                  <span class="field-hint">The master reference targets 10 / 100 / 500 / 1000.</span>
                </div>

                <div class="field">
                  <label for="interval">Reporting interval (seconds)</label>
                  <input id="interval" type="number" min="0.01" step="0.01" [formControl]="form.controls.intervalSeconds" />
                </div>

                <div class="field">
                  <label for="rounds">Rounds</label>
                  <input id="rounds" type="number" min="0" [formControl]="form.controls.rounds" />
                  <span class="field-hint">Zero runs until you stop it with Ctrl+C.</span>
                </div>

                <div class="field">
                  <label for="burstSize">Burst size</label>
                  <input id="burstSize" type="number" min="1" max="1000" [formControl]="form.controls.burstSize" />
                  <span class="field-hint">Readings per bulk request. The API caps a batch at 1000.</span>
                </div>

                <div class="field">
                  <label for="tempMin">Temperature min (°C)</label>
                  <input id="tempMin" type="number" step="any" [formControl]="form.controls.temperatureMin" />
                </div>

                <div class="field">
                  <label for="tempMax">Temperature max (°C)</label>
                  <input id="tempMax" type="number" step="any" [formControl]="form.controls.temperatureMax" />
                </div>

                <div class="field">
                  <label for="anomaly">Anomaly probability</label>
                  <input
                    id="anomaly"
                    type="number"
                    min="0"
                    max="1"
                    step="0.01"
                    [formControl]="form.controls.anomalyProbability"
                  />
                  <span class="field-hint">Chance of a spike above the range, to trip the rules.</span>
                </div>

                <div class="field">
                  <label for="failure">Dropout probability</label>
                  <input
                    id="failure"
                    type="number"
                    min="0"
                    max="1"
                    step="0.01"
                    [formControl]="form.controls.failureProbability"
                  />
                  <span class="field-hint">Chance a device goes quiet, exercising offline detection.</span>
                </div>

                <div class="field">
                  <label for="seed">Random seed</label>
                  <input id="seed" type="number" [formControl]="form.controls.randomSeed" />
                  <span class="field-hint">
                    Fixed, so two runs produce identical data and a throughput difference can
                    only come from the system under test.
                  </span>
                </div>

                <div class="field">
                  <label for="drain">Battery drain per reading (%)</label>
                  <input id="drain" type="number" min="0" step="0.01" [formControl]="form.controls.batteryDrain" />
                </div>
              </div>

              <label class="checkbox">
                <input type="checkbox" [formControl]="form.controls.burstMode" />
                Burst mode — send each round as one bulk request instead of one request per device
              </label>

              @if (!form.controls.burstMode.value) {
                <p class="warn-note small">
                  With burst mode off, a round of {{ form.controls.deviceCount.value }} devices
                  becomes {{ form.controls.deviceCount.value }} HTTP requests. Useful for measuring
                  per-request overhead, far slower for anything else.
                </p>
              }

              @if (rangeInvalid()) {
                <span class="field-error">Temperature min must be below max.</span>
              }
            </div>
          </section>

          <!-- Command -->
          <section class="card">
            <div class="card-header">
              <h2>Run it</h2>
              <span class="spacer"></span>
              <dp-copy [value]="command()" label="command" />
            </div>

            <div class="card-body stack">
              <pre class="command">{{ command() }}</pre>

              <p class="muted small">
                Run from <code>backend/src/DevicePulse.Simulator</code>. Credentials come from
                <code>dotnet user-secrets</code> — they are never written into a command or a
                config file that could be committed.
              </p>

              <details>
                <summary>First-time setup</summary>
                <pre class="command small">{{ setupCommand }}</pre>
                <p class="muted small">
                  The account needs the <code>telemetry.ingest</code> permission. In development
                  the seeded Super Admin has it; a real deployment would use a dedicated service
                  account with that one permission and nothing else.
                </p>
              </details>
            </div>
          </section>
        </div>

        <div class="stack">
          <!-- Live state -->
          <section class="card">
            <div class="card-header">
              <h2>Current ingestion state</h2>
              <span class="spacer"></span>
              <button type="button" class="btn btn-sm" (click)="loadState()" [disabled]="loading()">
                Refresh
              </button>
            </div>

            <div class="card-body">
              <dl class="state">
                <div>
                  <dt>Devices reporting</dt>
                  <dd class="mono">{{ onlineCount() }}</dd>
                </div>
                <div>
                  <dt>Devices silent</dt>
                  <dd class="mono">{{ offlineCount() }}</dd>
                </div>
                <div>
                  <dt>Never reported</dt>
                  <dd class="mono">{{ unknownCount() }}</dd>
                </div>
                <div>
                  <dt>With an ingestion key</dt>
                  <dd class="mono">{{ keyedCount() }}</dd>
                </div>
              </dl>

              <p class="muted small">
                The simulator authenticates as a user and names the device in each reading. A real
                device authenticates with its own key instead and can only ever report as itself.
              </p>
            </div>
          </section>

          <!-- Single reading -->
          <dp-if-permitted [permission]="perm.telemetryIngest">
            <section class="card">
              <div class="card-header">
                <h2>Send one reading</h2>
                <span class="spacer"></span>
                <span class="muted small">tests a rule without starting the simulator</span>
              </div>

              <form [formGroup]="manualForm" (ngSubmit)="sendOne()">
                <div class="card-body stack">
                  <div class="field">
                    <label for="manualDevice">Device</label>
                    <select id="manualDevice" [formControl]="manualForm.controls.deviceId">
                      <option [value]="0" disabled>Choose a device</option>
                      @for (device of devices(); track device.deviceId) {
                        <option [value]="device.deviceId">
                          {{ device.deviceName }} ({{ device.deviceCode }})
                        </option>
                      }
                    </select>
                  </div>

                  <div class="grid-two">
                    <div class="field">
                      <label for="manualTemp">Temperature (°C)</label>
                      <input id="manualTemp" type="number" step="any" [formControl]="manualForm.controls.temperature" />
                    </div>

                    <div class="field">
                      <label for="manualBattery">Battery (%)</label>
                      <input
                        id="manualBattery"
                        type="number"
                        min="0"
                        max="100"
                        step="any"
                        [formControl]="manualForm.controls.battery"
                      />
                    </div>

                    <div class="field">
                      <label for="manualSignal">Signal (dBm)</label>
                      <input
                        id="manualSignal"
                        type="number"
                        min="-150"
                        max="0"
                        [formControl]="manualForm.controls.signalStrength"
                      />
                    </div>
                  </div>

                  @if (manualError()) {
                    <span class="field-error">{{ manualError() }}</span>
                  }

                  @if (lastResult(); as result) {
                    <div class="result" [class.duplicate]="result.duplicate">
                      @if (result.duplicate) {
                        Recognised as a retry and discarded — no second reading was stored.
                      } @else {
                        Reading {{ result.telemetryId }} accepted.
                        @if (result.alertsRaised > 0) {
                          <strong>{{ result.alertsRaised }} alert(s) raised.</strong>
                        } @else {
                          No rule matched.
                        }
                      }
                    </div>
                  }
                </div>

                <div class="card-footer row">
                  <span class="spacer"></span>
                  <button type="submit" class="btn btn-primary" [disabled]="sending()">
                    @if (sending()) {
                      <span class="spinner"></span>
                    }
                    Send reading
                  </button>
                </div>
              </form>
            </section>
          </dp-if-permitted>

          <!-- Honest scope note -->
          <section class="card">
            <div class="card-header"><h2>Why there is no Start button</h2></div>
            <div class="card-body">
              <p class="muted small">
                The simulator is a separate console application, and this API has no endpoint that
                launches or stops a process. Buttons here would be decoration.
              </p>
              <p class="muted small">
                Driving it remotely needs a backend process supervisor (or a containerised worker
                the API can scale), which is tracked as future work rather than faked here.
              </p>
            </div>
          </section>
        </div>
      </div>
    </div>
  `,
  styles: [
    `
      .two-up {
        display: grid;
        gap: 1rem;
        grid-template-columns: minmax(0, 1.25fr) minmax(0, 1fr);
        align-items: start;
      }

      @media (max-width: 1100px) {
        .two-up { grid-template-columns: 1fr; }
      }

      .grid-two {
        display: grid;
        gap: 0.75rem;
        grid-template-columns: 1fr 1fr;
      }

      @media (max-width: 560px) {
        .grid-two { grid-template-columns: 1fr; }
      }

      .command {
        margin: 0;
        padding: 0.75rem 0.85rem;
        font-family: var(--font-mono);
        font-size: 0.78rem;
        line-height: 1.6;
        color: var(--text);
        background: var(--surface-3);
        border: 1px solid var(--border);
        border-radius: var(--radius);
        white-space: pre-wrap;
        word-break: break-word;
      }

      details summary {
        cursor: pointer;
        font-size: 0.82rem;
        color: var(--text-muted);
        margin-bottom: 0.5rem;
      }

      .state {
        display: grid;
        gap: 0.6rem 1rem;
        grid-template-columns: 1fr 1fr;
        margin: 0 0 0.8rem;
      }

      .state dt {
        font-size: 0.68rem;
        font-weight: 600;
        text-transform: uppercase;
        letter-spacing: 0.04em;
        color: var(--text-subtle);
      }

      .state dd {
        margin: 0.1rem 0 0;
        font-size: 1.15rem;
        font-weight: 600;
      }

      .warn-note {
        margin: 0;
        padding: 0.5rem 0.65rem;
        color: var(--warn);
        background: var(--warn-soft);
        border-radius: var(--radius);
      }

      .result {
        padding: 0.55rem 0.7rem;
        font-size: 0.82rem;
        color: var(--ok);
        background: var(--ok-soft);
        border-radius: var(--radius);
      }

      .result.duplicate {
        color: var(--info);
        background: var(--info-soft);
      }
    `,
  ],
})
export class SimulatorComponent implements OnInit {
  private readonly api = inject(ApiService);
  private readonly auth = inject(AuthService);
  private readonly notifications = inject(NotificationService);
  private readonly fb = inject(FormBuilder);

  readonly perm = Permissions;

  readonly devices = signal<Device[]>([]);
  readonly loading = signal(false);
  readonly sending = signal(false);
  readonly manualError = signal<string | null>(null);
  readonly lastResult = signal<{ telemetryId: number; duplicate: boolean; alertsRaised: number } | null>(null);

  readonly onlineCount = signal(0);
  readonly offlineCount = signal(0);
  readonly unknownCount = signal(0);
  readonly keyedCount = signal(0);

  readonly setupCommand = [
    'cd backend/src/DevicePulse.Simulator',
    'dotnet user-secrets set "Simulator:Email" "superadmin@devicepulse.local"',
    'dotnet user-secrets set "Simulator:Password" "<the password>"',
  ].join('\n');

  readonly form = this.fb.nonNullable.group({
    deviceCount: [100, [Validators.required, Validators.min(1)]],
    intervalSeconds: [5, [Validators.required, Validators.min(0.01)]],
    rounds: [0, [Validators.min(0)]],
    burstMode: [true],
    burstSize: [200, [Validators.min(1), Validators.max(1000)]],
    temperatureMin: [18, [Validators.required]],
    temperatureMax: [45, [Validators.required]],
    anomalyProbability: [0.05, [Validators.min(0), Validators.max(1)]],
    failureProbability: [0.02, [Validators.min(0), Validators.max(1)]],
    batteryDrain: [0.05, [Validators.min(0)]],
    randomSeed: [1337],
  });

  readonly manualForm = this.fb.nonNullable.group({
    deviceId: [0, [Validators.min(1)]],
    temperature: [47, [Validators.required]],
    battery: [55, [Validators.required, Validators.min(0), Validators.max(100)]],
    signalStrength: [-65, [Validators.required, Validators.min(-150), Validators.max(0)]],
  });

  readonly rangeInvalid = computed(() => {
    const { temperatureMin, temperatureMax } = this.form.getRawValue();
    return Number(temperatureMin) >= Number(temperatureMax);
  });

  ngOnInit(): void {
    this.loadState();
  }

  loadState(): void {
    if (!this.auth.has(Permissions.deviceView)) {
      return;
    }

    this.loading.set(true);

    // Only active devices, because a retired one cannot accept telemetry and listing it as a
    // target would just produce a rejection.
    this.api.getDevices({ pageSize: 200, lifecycleStatus: 'Active' }).subscribe({
      next: (page) => {
        this.devices.set(page.items);

        this.onlineCount.set(page.items.filter((d) => d.connectivityStatus === 'Online').length);
        this.offlineCount.set(page.items.filter((d) => d.connectivityStatus === 'Offline').length);
        this.unknownCount.set(page.items.filter((d) => d.connectivityStatus === 'Unknown').length);
        this.keyedCount.set(page.items.filter((d) => d.hasApiKey).length);

        if (this.manualForm.controls.deviceId.value === 0 && page.items.length > 0) {
          this.manualForm.controls.deviceId.setValue(page.items[0].deviceId);
        }

        this.loading.set(false);
      },
      error: () => this.loading.set(false),
    });
  }

  /**
   * Builds the command line.
   *
   * Only values that differ from the simulator's own defaults are emitted, so the command stays
   * short and readable — and so it documents what is actually being varied for this run.
   */
  readonly command = computed(() => {
    const value = this.form.getRawValue();

    const defaults: Record<string, number | boolean> = {
      DeviceCount: 10,
      IntervalSeconds: 5,
      Rounds: 0,
      BurstMode: true,
      BurstSize: 200,
      TemperatureMin: 18,
      TemperatureMax: 45,
      AnomalyProbability: 0.05,
      FailureProbability: 0.02,
      BatteryDrainPerReading: 0.05,
      RandomSeed: 1337,
    };

    const chosen: Record<string, number | boolean> = {
      DeviceCount: Number(value.deviceCount),
      IntervalSeconds: Number(value.intervalSeconds),
      Rounds: Number(value.rounds),
      BurstMode: value.burstMode,
      BurstSize: Number(value.burstSize),
      TemperatureMin: Number(value.temperatureMin),
      TemperatureMax: Number(value.temperatureMax),
      AnomalyProbability: Number(value.anomalyProbability),
      FailureProbability: Number(value.failureProbability),
      BatteryDrainPerReading: Number(value.batteryDrain),
      RandomSeed: Number(value.randomSeed),
    };

    const overrides = Object.entries(chosen)
      .filter(([key, v]) => v !== defaults[key])
      .map(([key, v]) => `--Simulator:${key}=${v}`);

    if (overrides.length === 0) {
      return 'dotnet run';
    }

    // Line-continued so a long command stays readable when pasted into a terminal.
    return `dotnet run -- \\\n  ${overrides.join(' \\\n  ')}`;
  });

  sendOne(): void {
    this.manualError.set(null);
    this.lastResult.set(null);

    if (this.manualForm.invalid) {
      this.manualForm.markAllAsTouched();
      this.manualError.set('Choose a device and provide a valid reading.');
      return;
    }

    const value = this.manualForm.getRawValue();
    this.sending.set(true);

    this.api
      .ingestTelemetry({
        deviceId: Number(value.deviceId),
        temperature: Number(value.temperature),
        battery: Number(value.battery),
        signalStrength: Number(value.signalStrength),
        // A fresh id each time, so this button is never mistaken for a retry and silently
        // deduplicated — which would look like the reading had been ignored.
        messageId: `ui-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`,
      })
      .subscribe({
        next: (result) => {
          this.sending.set(false);
          this.lastResult.set(result);

          if (result.alertsRaised > 0) {
            this.notifications.success(
              `${result.alertsRaised} alert(s) raised by that reading.`,
              'Open the Alerts page to see them.',
            );
          }

          this.loadState();
        },
        error: (error: unknown) => {
          this.sending.set(false);
          this.manualError.set(messageFrom(error, 'The reading was rejected.'));
        },
      });
  }
}
