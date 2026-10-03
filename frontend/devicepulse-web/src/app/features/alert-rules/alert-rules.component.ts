import { Component, OnInit, inject, signal, ChangeDetectionStrategy } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';

import { ApiService } from '../../core/services/api.service';
import { AuthService } from '../../core/auth/auth.service';
import { Permissions } from '../../core/auth/permissions';
import { NotificationService } from '../../core/services/notification.service';
import { fieldErrorsFrom } from '../../core/interceptors/error.interceptor';
import {
  AlertMetric,
  AlertOperator,
  AlertRule,
  AlertRuleVocabulary,
  AlertSeverity,
  DeviceType,
} from '../../core/models/api.models';
import {
  EmptyStateComponent,
  IfPermittedComponent,
  ModalComponent,
  PageHeaderComponent,
  SeverityBadgeComponent,
} from '../../shared/components/ui.components';
import { AbsoluteTimePipe, DurationPipe } from '../../shared/utils/relative-time.pipe';

/**
 * Alert rule administration (§10.2, §16).
 *
 * This is the page the master reference's headline claim rests on: the business says "battery
 * under 15% should raise a Medium alert", an administrator expresses it here, and the backend
 * starts applying it — no code change, no redeploy, no SQL console (Appendix A.1).
 *
 * The metric, operator and severity dropdowns are populated from the API's own vocabulary
 * endpoint rather than hardcoded, so this form cannot offer a combination the backend would
 * reject (Appendix C item 5).
 */
@Component({
  selector: 'dp-alert-rules',
  standalone: true,
  imports: [
    ReactiveFormsModule,
    PageHeaderComponent,
    SeverityBadgeComponent,
    EmptyStateComponent,
    ModalComponent,
    IfPermittedComponent,
    AbsoluteTimePipe,
    DurationPipe
],
  template: `
    <div class="page">
      <dp-page-header
        title="Alert rules"
      >
        <dp-if-permitted [permission]="perm.alertRuleManage">
          <button type="button" class="btn btn-primary" (click)="openCreate()">New rule</button>
        </dp-if-permitted>
      </dp-page-header>

      @if (loading()) {
        <div class="skeleton" style="height: 260px; border-radius: 12px"></div>
      } @else if (rules().length === 0) {
        <div class="panel">
          <dp-empty title="No alert rules" message="Without a rule, no alerts will ever be raised.">
            <dp-if-permitted [permission]="perm.alertRuleManage">
              <button type="button" class="btn btn-primary" (click)="openCreate()">Create the first rule</button>
            </dp-if-permitted>
          </dp-empty>
        </div>
      } @else {
        <div class="rules">
          @for (rule of rules(); track rule.alertRuleId) {
            <article class="panel rule" [class.disabled]="!rule.isEnabled">
              <div class="panel-body">
                <div class="row row-wrap">
                  <dp-severity [severity]="rule.severity" />
                  <h3>{{ rule.name }}</h3>
                  <span class="spacer"></span>

                  <dp-if-permitted [permission]="perm.alertRuleManage">
                    <label class="switch" [title]="rule.isEnabled ? 'Disable this rule' : 'Enable this rule'">
                      <input
                        type="checkbox"
                        [checked]="rule.isEnabled"
                        [disabled]="busyId() === rule.alertRuleId"
                        (change)="toggle(rule)"
                      />
                      <span class="switch-track"></span>
                    </label>
                  </dp-if-permitted>
                </div>

                <p class="condition mono">{{ rule.conditionSummary }}</p>

                @if (rule.description) {
                  <p class="text-2 small">{{ rule.description }}</p>
                }

                <dl class="meta">
                  <div>
                    <dt>Applies to</dt>
                    <dd>{{ rule.deviceTypeName ?? 'all device types' }}</dd>
                  </div>
                  <div>
                    <dt>Cooldown</dt>
                    <dd>{{ rule.cooldownSeconds | duration }}</dd>
                  </div>
                  <div>
                    <dt>Times triggered</dt>
                    <dd class="mono">{{ rule.triggeredCount }}</dd>
                  </div>
                  <div>
                    <dt>Last changed</dt>
                    <dd>{{ (rule.updatedAt ?? rule.createdAt) | absoluteTime }}</dd>
                  </div>
                </dl>
              </div>

              <dp-if-permitted [permission]="perm.alertRuleManage">
                <div class="panel-foot row">
                  @if (!rule.isEnabled) {
                    <span class="badge badge-neutral">disabled</span>
                  }
                  <span class="spacer"></span>
                  <button type="button" class="btn btn-sm" (click)="openEdit(rule)">Edit</button>
                  <button type="button" class="btn btn-sm btn-ghost" (click)="remove(rule)">Delete</button>
                </div>
              </dp-if-permitted>
            </article>
          }
        </div>
      }
    </div>

    <dp-modal
      [open]="formOpen()"
      [title]="editing() ? 'Edit rule' : 'New alert rule'"
      width="620px"
      (closed)="formOpen.set(false)"
    >
      <form [formGroup]="form" (ngSubmit)="save()">
        <div class="panel-body stack">
          <div class="field">
            <label for="name">Name</label>
            <input id="name" type="text" formControlName="name" placeholder="High temperature" />
            @for (message of errorsFor('name'); track message) {
              <span class="field-error">{{ message }}</span>
            }
          </div>

          <!-- The condition, assembled from the closed vocabulary the API serves. -->
          <fieldset class="condition-builder">
            <legend>Condition</legend>

            <div class="builder-row">
              <div class="field">
                <label for="metric">When</label>
                <select id="metric" formControlName="metric">
                  @for (metric of vocabulary()?.metrics ?? []; track metric.value) {
                    <option [value]="metric.value">{{ metric.label }}</option>
                  }
                </select>
              </div>

              <div class="field narrow">
                <label for="operator">is</label>
                <select id="operator" formControlName="operator">
                  @for (operator of vocabulary()?.operators ?? []; track operator.value) {
                    <option [value]="operator.value">{{ operator.label }}</option>
                  }
                </select>
              </div>

              <div class="field narrow">
                <label for="threshold">{{ unitLabel() }}</label>
                <input id="threshold" type="number" step="any" formControlName="threshold" />
              </div>
            </div>

            @for (message of errorsFor('threshold'); track message) {
              <span class="field-error">{{ message }}</span>
            }
            @for (message of errorsFor('operator'); track message) {
              <span class="field-error">{{ message }}</span>
            }

            <p class="preview">
              {{ preview() }}
            </p>
          </fieldset>

          <div class="two-col">
            <div class="field">
              <label for="severity">Severity</label>
              <select id="severity" formControlName="severity">
                @for (severity of vocabulary()?.severities ?? []; track severity.value) {
                  <option [value]="severity.value">{{ severity.label }}</option>
                }
              </select>
            </div>

            <div class="field">
              <label for="cooldown">Cooldown (seconds)</label>
              <input id="cooldown" type="number" min="0" max="86400" formControlName="cooldownSeconds" />
              <span class="field-hint">
                Suppresses repeats. Zero means every matching reading raises an alert.
              </span>
              @for (message of errorsFor('cooldownSeconds'); track message) {
                <span class="field-error">{{ message }}</span>
              }
            </div>
          </div>

          <div class="field">
            <label for="deviceType">Applies to</label>
            <select id="deviceType" formControlName="deviceTypeId">
              <option [value]="0">All device types</option>
              @for (type of deviceTypes(); track type.deviceTypeId) {
                <option [value]="type.deviceTypeId">{{ type.name }}</option>
              }
            </select>
          </div>

          <div class="field">
            <label for="description">Description</label>
            <textarea
              id="description"
              formControlName="description"
              placeholder="Why this rule exists and what someone should do when it fires."
            ></textarea>
          </div>

          @if (!editing()) {
            <label class="checkbox">
              <input type="checkbox" formControlName="isEnabled" />
              Enable this rule straight away
            </label>
          }
        </div>

        <div class="panel-foot row">
          <span class="spacer"></span>
          <button type="button" class="btn" (click)="formOpen.set(false)">Cancel</button>
          <button type="submit" class="btn btn-primary" [disabled]="saving()">
            @if (saving()) {
              <span class="spinner"></span>
            }
            {{ editing() ? 'Save changes' : 'Create rule' }}
          </button>
        </div>
      </form>
    </dp-modal>
  `,
  changeDetection: ChangeDetectionStrategy.Eager,
  styles: [
    `
      .rules {
        display: grid;
        gap: var(--sp-4);
        grid-template-columns: repeat(auto-fill, minmax(330px, 1fr));
      }

      .rule {
        display: flex;
        flex-direction: column;
      }

      .rule .panel-body { flex: 1 1 auto; }

      .rule.disabled { opacity: 0.68; }

      .rule h3 { font-size: 0.95rem; }

      .condition {
        margin: var(--sp-2) 0 var(--sp-2);
        padding: var(--sp-2) var(--sp-2);
        font-size: 0.82rem;
        background: var(--panel-3);
        border-radius: var(--r-md);
      }

      .meta {
        display: grid;
        gap: var(--sp-2) var(--sp-4);
        grid-template-columns: 1fr 1fr;
        margin: var(--sp-3) 0 0;
      }

      .meta dt {
        font-size: 0.68rem;
        font-weight: 600;
        text-transform: uppercase;
        letter-spacing: 0.04em;
        color: var(--text-3);
      }

      .meta dd {
        margin: 2px 0 0;
        font-size: 0.82rem;
      }

      .condition-builder {
        margin: 0;
        padding: var(--sp-3);
        border: 1px solid var(--line);
        border-radius: var(--r-md);
      }

      .condition-builder legend {
        padding: 0 var(--sp-1);
        font-size: 0.78rem;
        font-weight: 600;
        color: var(--text-2);
      }

      .builder-row {
        display: grid;
        gap: var(--sp-2);
        grid-template-columns: 1.5fr 0.9fr 0.9fr;
        align-items: end;
      }

      @media (max-width: 600px) {
        .builder-row { grid-template-columns: 1fr; }
      }

      .preview {
        margin: var(--sp-3) 0 0;
        padding: var(--sp-2) var(--sp-2);
        font-size: 0.8rem;
        color: var(--accent);
        background: var(--accent-wash);
        border-radius: var(--r-md);
      }

      .two-col {
        display: grid;
        gap: var(--sp-3);
        grid-template-columns: 1fr 1fr;
      }

      @media (max-width: 600px) {
        .two-col { grid-template-columns: 1fr; }
      }
    `,
  ],
})
export class AlertRulesComponent implements OnInit {
  private readonly api = inject(ApiService);
  private readonly auth = inject(AuthService);
  private readonly notifications = inject(NotificationService);
  private readonly fb = inject(FormBuilder);

  readonly perm = Permissions;

  readonly rules = signal<AlertRule[]>([]);
  readonly vocabulary = signal<AlertRuleVocabulary | null>(null);
  readonly deviceTypes = signal<DeviceType[]>([]);

  readonly loading = signal(true);
  readonly saving = signal(false);
  readonly busyId = signal<number | null>(null);
  readonly formOpen = signal(false);
  readonly editing = signal<AlertRule | null>(null);

  private serverErrors: Record<string, string[]> = {};

  readonly form = this.fb.nonNullable.group({
    name: ['', [Validators.required, Validators.maxLength(150)]],
    description: [''],
    metric: ['Temperature' as AlertMetric],
    operator: ['GreaterThan' as AlertOperator],
    threshold: [40, [Validators.required]],
    severity: ['High' as AlertSeverity],
    cooldownSeconds: [300, [Validators.required, Validators.min(0), Validators.max(86_400)]],
    deviceTypeId: [0],
    isEnabled: [true],
  });

  ngOnInit(): void {
    this.load();

    this.api.getAlertRuleVocabulary().subscribe({ next: (v) => this.vocabulary.set(v) });

    if (this.auth.has(Permissions.referenceDataView)) {
      this.api.getDeviceTypes().subscribe({ next: (types) => this.deviceTypes.set(types) });
    }
  }

  private load(): void {
    this.loading.set(true);

    this.api.getAlertRules().subscribe({
      next: (rules) => {
        this.rules.set(rules);
        this.loading.set(false);
      },
      error: () => this.loading.set(false),
    });
  }

  /** The unit for the currently-selected metric, so the threshold field is labelled correctly. */
  unitLabel(): string {
    const metric = this.form.controls.metric.value;
    const unit = this.vocabulary()?.metrics.find((m) => m.value === metric)?.unit;

    return unit ? `Threshold (${unit.trim()})` : 'Threshold';
  }

  /**
   * A plain-language restatement of the rule being built. Worth the few lines: "Temperature >
   * 40" is easy to misread, and an administrator about to change production behaviour should
   * see what they are about to create spelled out.
   */
  preview(): string {
    const value = this.form.getRawValue();
    const vocabulary = this.vocabulary();

    const metricLabel =
      vocabulary?.metrics.find((m) => m.value === value.metric)?.label ?? value.metric;
    const operatorLabel =
      vocabulary?.operators.find((o) => o.value === value.operator)?.label ?? value.operator;
    const unit = vocabulary?.metrics.find((m) => m.value === value.metric)?.unit ?? '';

    const scope =
      Number(value.deviceTypeId) === 0
        ? 'any device'
        : this.deviceTypes().find((t) => t.deviceTypeId === Number(value.deviceTypeId))?.name ??
          'the selected type';

    const cooldown =
      Number(value.cooldownSeconds) === 0
        ? 'every matching reading raises an alert'
        : `at most once every ${value.cooldownSeconds}s per device`;

    if (value.metric === 'LastSeenAgeSeconds') {
      return `Raise a ${value.severity} alert when ${scope} has not reported for ${operatorLabel} ${value.threshold}s — ${cooldown}.`;
    }

    return `Raise a ${value.severity} alert when ${scope} reports ${metricLabel.toLowerCase()} ${operatorLabel} ${value.threshold}${unit} — ${cooldown}.`;
  }

  openCreate(): void {
    this.serverErrors = {};
    this.editing.set(null);

    this.form.reset({
      name: '',
      description: '',
      metric: 'Temperature',
      operator: 'GreaterThan',
      threshold: 40,
      severity: 'High',
      cooldownSeconds: 300,
      deviceTypeId: 0,
      isEnabled: true,
    });

    this.formOpen.set(true);
  }

  openEdit(rule: AlertRule): void {
    this.serverErrors = {};
    this.editing.set(rule);

    this.form.reset({
      name: rule.name,
      description: rule.description ?? '',
      metric: rule.metric,
      operator: rule.operator,
      threshold: rule.threshold,
      severity: rule.severity,
      cooldownSeconds: rule.cooldownSeconds,
      deviceTypeId: rule.deviceTypeId ?? 0,
      isEnabled: rule.isEnabled,
    });

    this.formOpen.set(true);
  }

  errorsFor(field: string): string[] {
    const control = this.form.get(field);
    const messages: string[] = [];

    if (control && (control.dirty || control.touched)) {
      if (control.hasError('required')) messages.push('This field is required.');
      if (control.hasError('maxlength')) messages.push('That is too long.');
      if (control.hasError('min')) messages.push('That is below the allowed minimum.');
      if (control.hasError('max')) messages.push('That is above the allowed maximum.');
    }

    return [...messages, ...(this.serverErrors[field] ?? [])];
  }

  save(): void {
    this.serverErrors = {};

    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    this.saving.set(true);
    const value = this.form.getRawValue();

    // Zero is the UI's way of saying "all device types"; the API models that as null.
    const deviceTypeId = Number(value.deviceTypeId) === 0 ? null : Number(value.deviceTypeId);
    const rule = this.editing();

    const done = (message: string) => {
      this.saving.set(false);
      this.formOpen.set(false);
      this.notifications.success(message);
      this.load();
    };

    const failed = (error: unknown) => {
      this.saving.set(false);
      this.serverErrors = fieldErrorsFrom(error);
    };

    if (rule) {
      this.api
        .updateAlertRule(rule.alertRuleId, {
          name: value.name.trim(),
          description: value.description.trim() || null,
          metric: value.metric,
          operator: value.operator,
          threshold: Number(value.threshold),
          severity: value.severity,
          cooldownSeconds: Number(value.cooldownSeconds),
          deviceTypeId,
          // Round-tripped so a concurrent edit is rejected rather than silently overwritten.
          rowVersion: rule.rowVersion,
        })
        .subscribe({
          next: () => done('Rule updated. It applies from the next reading.'),
          error: failed,
        });

      return;
    }

    this.api
      .createAlertRule({
        name: value.name.trim(),
        description: value.description.trim() || null,
        metric: value.metric,
        operator: value.operator,
        threshold: Number(value.threshold),
        severity: value.severity,
        isEnabled: value.isEnabled,
        cooldownSeconds: Number(value.cooldownSeconds),
        deviceTypeId,
      })
      .subscribe({
        next: () => done('Rule created. It applies from the next reading.'),
        error: failed,
      });
  }

  toggle(rule: AlertRule): void {
    this.busyId.set(rule.alertRuleId);

    this.api.setAlertRuleStatus(rule.alertRuleId, !rule.isEnabled).subscribe({
      next: (updated) => {
        this.busyId.set(null);
        this.rules.update((rules) =>
          rules.map((r) => (r.alertRuleId === updated.alertRuleId ? updated : r)),
        );
        this.notifications.success(`${updated.name} ${updated.isEnabled ? 'enabled' : 'disabled'}.`);
      },
      error: () => {
        this.busyId.set(null);
        // Reloaded so the switch does not sit in a state the server rejected.
        this.load();
      },
    });
  }

  remove(rule: AlertRule): void {
    // Steered toward disabling, which is almost always what is actually wanted: it keeps the
    // definition and its history, where deleting discards both.
    const confirmed = window.confirm(
      `Delete "${rule.name}"?\n\nThe ${rule.triggeredCount} alert(s) it has raised will be kept, but the rule definition and its history will be gone. Disabling it instead keeps everything and stops it firing.`,
    );

    if (!confirmed) {
      return;
    }

    this.busyId.set(rule.alertRuleId);

    this.api.deleteAlertRule(rule.alertRuleId).subscribe({
      next: () => {
        this.busyId.set(null);
        this.notifications.success(`${rule.name} deleted.`);
        this.load();
      },
      error: () => this.busyId.set(null),
    });
  }
}
