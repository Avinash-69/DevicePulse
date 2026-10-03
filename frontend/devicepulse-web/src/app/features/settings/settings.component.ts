import { Component, OnInit, computed, inject, signal, ChangeDetectionStrategy } from '@angular/core';
import { FormBuilder, ReactiveFormsModule } from '@angular/forms';

import { ApiService } from '../../core/services/api.service';
import { AuthService } from '../../core/auth/auth.service';
import { Permissions } from '../../core/auth/permissions';
import { NotificationService } from '../../core/services/notification.service';
import { messageFrom } from '../../core/interceptors/error.interceptor';
import { Setting, SettingHistoryEntry } from '../../core/models/api.models';
import {
  EmptyStateComponent,
  ModalComponent,
  PageHeaderComponent,
} from '../../shared/components/ui.components';
import { AbsoluteTimePipe, RelativeTimePipe } from '../../shared/utils/relative-time.pipe';

/**
 * Runtime business settings (§10.1, §18).
 *
 * Each control is chosen from the setting's own declared metadata — a number input with min and
 * max for an Integer, a switch for a Boolean, a dropdown for an Enum. That is the payoff of
 * keeping the settings table typed instead of letting it become an untyped key-value dump
 * (Development Rule 6): this page needs no per-setting special casing, and a setting added on
 * the backend appears here, correctly rendered and correctly validated, with no frontend change.
 */
@Component({
  selector: 'dp-settings',
  standalone: true,
  imports: [
    ReactiveFormsModule,
    PageHeaderComponent,
    EmptyStateComponent,
    ModalComponent,
    AbsoluteTimePipe,
    RelativeTimePipe
],
  template: `
    <div class="page">
      <dp-page-header
        title="Settings"
      />

      @if (loading()) {
        <div class="skeleton" style="height: 320px; border-radius: 12px"></div>
      } @else if (settings().length === 0) {
        <div class="card">
          <dp-empty title="No settings" message="The backend has not seeded its settings catalog yet." />
        </div>
      } @else {
        <div class="stack-lg">
          @for (group of grouped(); track group.category) {
            <section class="card">
              <div class="card-header">
                <h2>{{ group.category }}</h2>
              </div>

              <div class="settings">
                @for (setting of group.settings; track setting.key) {
                  <div class="setting">
                    <div class="info">
                      <span class="key mono">{{ setting.key }}</span>
                      @if (setting.description) {
                        <p class="muted small">{{ setting.description }}</p>
                      }
                      <p class="subtle small">
                        Version {{ setting.version }}
                        @if (setting.updatedBy) {
                          · changed by {{ setting.updatedBy }}
                          @if (setting.updatedAt) {
                            {{ setting.updatedAt | relativeTime }}
                          }
                        } @else {
                          · never changed
                        }
                      </p>
                    </div>

                    <div class="control">
                      @switch (setting.valueType) {
                        @case ('Boolean') {
                          <label class="switch">
                            <input
                              type="checkbox"
                              [checked]="draft()[setting.key] === 'true'"
                              [disabled]="!canEdit(setting)"
                              (change)="setDraft(setting.key, asChecked($event) ? 'true' : 'false')"
                            />
                            <span class="switch-track"></span>
                          </label>
                          <span class="muted small">
                            {{ draft()[setting.key] === 'true' ? 'Enabled' : 'Disabled' }}
                          </span>
                        }

                        @case ('Enum') {
                          <select
                            [value]="draft()[setting.key]"
                            [disabled]="!canEdit(setting)"
                            (change)="setDraft(setting.key, asValue($event))"
                            [attr.aria-label]="setting.key"
                          >
                            @for (option of setting.allowedValues ?? []; track option) {
                              <option [value]="option">{{ option }}</option>
                            }
                          </select>
                        }

                        @case ('String') {
                          <input
                            type="text"
                            [value]="draft()[setting.key]"
                            [disabled]="!canEdit(setting)"
                            (input)="setDraft(setting.key, asValue($event))"
                            [attr.aria-label]="setting.key"
                          />
                        }

                        @default {
                          <!-- Integer and Decimal: the declared bounds become the input's own
                               min and max, so the browser enforces the same range the API does. -->
                          <div class="numeric">
                            <input
                              type="number"
                              [value]="draft()[setting.key]"
                              [min]="setting.minValue ?? null"
                              [max]="setting.maxValue ?? null"
                              [step]="setting.valueType === 'Decimal' ? 'any' : 1"
                              [disabled]="!canEdit(setting)"
                              (input)="setDraft(setting.key, asValue($event))"
                              [attr.aria-label]="setting.key"
                            />
                            @if (setting.unit) {
                              <span class="unit muted small">{{ setting.unit }}</span>
                            }
                          </div>

                          @if (setting.minValue !== null || setting.maxValue !== null) {
                            <span class="subtle small nowrap">
                              {{ setting.minValue }}–{{ setting.maxValue }}
                            </span>
                          }
                        }
                      }
                    </div>

                    <div class="actions">
                      @if (isDirty(setting)) {
                        <button
                          type="button"
                          class="btn btn-sm btn-primary"
                          (click)="openSave(setting)"
                          [disabled]="savingKey() === setting.key"
                        >
                          Save
                        </button>
                        <button type="button" class="btn btn-sm btn-ghost" (click)="revert(setting)">
                          Revert
                        </button>
                      } @else {
                        @if (canEdit(setting)) {
                          <button
                            type="button"
                            class="btn btn-sm btn-ghost"
                            (click)="openHistory(setting)"
                            title="View change history"
                          >
                            History
                          </button>
                        } @else {
                          <span class="badge badge-neutral">read only</span>
                        }
                      }
                    </div>
                  </div>
                }
              </div>
            </section>
          }
        </div>
      }
    </div>

    <!-- Confirm, with a reason. -->
    <dp-modal [open]="pending() !== null" title="Apply this change" (closed)="pending.set(null)">
      @if (pending(); as setting) {
        <div class="card-body stack">
          <div class="change">
            <div>
              <span class="fact-label">Setting</span>
              <span class="mono small">{{ setting.key }}</span>
            </div>
            <div class="change-values">
              <span class="old mono">{{ setting.value }}</span>
              <span class="arrow" aria-hidden="true">→</span>
              <span class="new mono">{{ draft()[setting.key] }}</span>
              @if (setting.unit) {
                <span class="muted small">{{ setting.unit }}</span>
              }
            </div>
          </div>

          <p class="muted small">
            This takes effect immediately across the application and will appear in the audit log.
          </p>

          <div class="field">
            <label for="reason">Why are you changing this?</label>
            <input
              id="reason"
              type="text"
              [formControl]="changeReason"
              placeholder="Faster offline detection during the trial deployment"
            />
            <span class="field-hint">
              Kept with the version history. Optional, but it is what makes the history readable
              later.
            </span>
          </div>

          @if (saveError()) {
            <span class="field-error">{{ saveError() }}</span>
          }
        </div>

        <div class="card-footer row">
          <span class="spacer"></span>
          <button type="button" class="btn" (click)="pending.set(null)">Cancel</button>
          <button
            type="button"
            class="btn btn-primary"
            (click)="save(setting)"
            [disabled]="savingKey() !== null"
          >
            @if (savingKey()) {
              <span class="spinner"></span>
            }
            Apply change
          </button>
        </div>
      }
    </dp-modal>

    <!-- Version history. -->
    <dp-modal
      [open]="historyFor() !== null"
      [title]="'History · ' + (historyFor()?.key ?? '')"
      width="640px"
      (closed)="historyFor.set(null)"
    >
      <div class="card-body-flush">
        @if (history().length === 0) {
          <dp-empty title="No changes yet" message="This setting is still at its seeded default." />
        } @else {
          <div class="table-wrap">
            <table class="data">
              <thead>
                <tr>
                  <th>Version</th>
                  <th>From</th>
                  <th>To</th>
                  <th>Changed by</th>
                  <th>When</th>
                  <th>Reason</th>
                </tr>
              </thead>
              <tbody>
                @for (entry of history(); track entry.settingHistoryId) {
                  <tr>
                    <td class="mono">{{ entry.version }}</td>
                    <td class="mono subtle">{{ entry.oldValue ?? '—' }}</td>
                    <td class="mono">{{ entry.newValue }}</td>
                    <td class="muted">{{ entry.changedBy ?? 'system' }}</td>
                    <td class="muted small nowrap">{{ entry.changedAt | absoluteTime }}</td>
                    <td class="muted small">{{ entry.changeReason ?? '—' }}</td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        }
      </div>
    </dp-modal>
  `,
  changeDetection: ChangeDetectionStrategy.Eager,
  styles: [
    `
      .settings { display: grid; }

      .setting {
        display: grid;
        gap: var(--sp-3);
        grid-template-columns: minmax(0, 1fr) minmax(170px, 260px) auto;
        align-items: center;
        padding: var(--sp-3) var(--sp-4);
        border-bottom: 1px solid var(--line);
      }

      .setting:last-child { border-bottom: none; }

      @media (max-width: 900px) {
        .setting { grid-template-columns: 1fr; }
      }

      .info { min-width: 0; }

      .key {
        font-size: 0.82rem;
        font-weight: 500;
      }

      .info p { margin: var(--sp-1) 0 0; }

      .control {
        display: flex;
        gap: var(--sp-2);
        align-items: center;
      }

      .numeric {
        position: relative;
        display: flex;
        gap: var(--sp-2);
        align-items: center;
        flex: 1 1 auto;
      }

      .unit { flex: 0 0 auto; }

      .actions {
        display: flex;
        gap: var(--sp-1);
        align-items: center;
        justify-content: flex-end;
      }

      .fact-label {
        display: block;
        font-size: 0.7rem;
        font-weight: 600;
        text-transform: uppercase;
        letter-spacing: 0.04em;
        color: var(--text-2);
      }

      .change {
        display: grid;
        gap: var(--sp-2);
        padding: var(--sp-3) var(--sp-4);
        background: var(--panel-2);
        border: 1px solid var(--line);
        border-radius: var(--r-md);
      }

      .change-values {
        display: flex;
        gap: var(--sp-2);
        align-items: center;
        flex-wrap: wrap;
      }

      .old {
        padding: var(--sp-1) var(--sp-2);
        color: var(--text-2);
        background: var(--panel-3);
        border-radius: var(--r-sm);
        text-decoration: line-through;
      }

      .new {
        padding: var(--sp-1) var(--sp-2);
        color: var(--accent);
        background: var(--accent-wash);
        border-radius: var(--r-sm);
        font-weight: 600;
      }

      .arrow { color: var(--text-3); }
    `,
  ],
})
export class SettingsComponent implements OnInit {
  private readonly api = inject(ApiService);
  private readonly auth = inject(AuthService);
  private readonly notifications = inject(NotificationService);
  private readonly fb = inject(FormBuilder);

  readonly settings = signal<Setting[]>([]);
  readonly loading = signal(true);
  readonly savingKey = signal<string | null>(null);
  readonly pending = signal<Setting | null>(null);
  readonly saveError = signal<string | null>(null);

  readonly historyFor = signal<Setting | null>(null);
  readonly history = signal<SettingHistoryEntry[]>([]);

  readonly changeReason = this.fb.nonNullable.control('');

  /** Edited-but-unsaved values, keyed by setting key. */
  readonly draft = signal<Record<string, string>>({});

  private readonly canManage = computed(() => this.auth.has(Permissions.settingsManage));

  /** Grouped by the category the backend declares, so related settings sit together. */
  readonly grouped = computed(() => {
    const groups = new Map<string, Setting[]>();

    for (const setting of this.settings()) {
      const existing = groups.get(setting.category);

      if (existing) {
        existing.push(setting);
      } else {
        groups.set(setting.category, [setting]);
      }
    }

    return [...groups.entries()]
      .map(([category, settings]) => ({ category, settings }))
      .sort((a, b) => a.category.localeCompare(b.category));
  });

  ngOnInit(): void {
    this.load();
  }

  private load(): void {
    this.loading.set(true);

    this.api.getSettings().subscribe({
      next: (settings) => {
        this.settings.set(settings);
        this.draft.set(Object.fromEntries(settings.map((s) => [s.key, s.value])));
        this.loading.set(false);
      },
      error: () => this.loading.set(false),
    });
  }

  canEdit(setting: Setting): boolean {
    // Two independent conditions: the setting must be declared editable, and the user must
    // hold settings.manage. The API enforces both again on the PUT.
    return setting.isEditable && this.canManage();
  }

  isDirty(setting: Setting): boolean {
    return this.canEdit(setting) && this.draft()[setting.key] !== setting.value;
  }

  setDraft(key: string, value: string): void {
    this.draft.update((current) => ({ ...current, [key]: value }));
  }

  revert(setting: Setting): void {
    this.setDraft(setting.key, setting.value);
  }

  asValue(event: Event): string {
    return (event.target as HTMLInputElement | HTMLSelectElement).value;
  }

  asChecked(event: Event): boolean {
    return (event.target as HTMLInputElement).checked;
  }

  openSave(setting: Setting): void {
    this.saveError.set(null);
    this.changeReason.setValue('');
    this.pending.set(setting);
  }

  save(setting: Setting): void {
    this.savingKey.set(setting.key);
    this.saveError.set(null);

    this.api
      .updateSetting(setting.key, {
        value: this.draft()[setting.key],
        changeReason: this.changeReason.value.trim() || null,
        rowVersion: setting.rowVersion,
      })
      .subscribe({
        next: (updated) => {
          this.savingKey.set(null);
          this.pending.set(null);

          this.settings.update((all) => all.map((s) => (s.key === updated.key ? updated : s)));
          this.setDraft(updated.key, updated.value);

          this.notifications.success(
            `${updated.key} is now ${updated.value}${updated.unit ? ' ' + updated.unit : ''}.`,
            `Version ${updated.version} · applied immediately`,
          );
        },
        error: (error: unknown) => {
          this.savingKey.set(null);

          // Shown inside the dialog rather than as a toast: the user is looking at the value
          // they just typed, and that is where the complaint about it belongs.
          this.saveError.set(messageFrom(error, 'That value was rejected.'));
        },
      });
  }

  openHistory(setting: Setting): void {
    this.historyFor.set(setting);
    this.history.set([]);

    this.api.getSettingHistory(setting.key).subscribe({
      next: (entries) => this.history.set(entries),
    });
  }
}
