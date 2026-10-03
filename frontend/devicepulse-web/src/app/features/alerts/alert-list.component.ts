import { Component, OnInit, inject, signal, ChangeDetectionStrategy } from '@angular/core';
import { FormBuilder, ReactiveFormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';

import { ApiService } from '../../core/services/api.service';
import { Permissions } from '../../core/auth/permissions';
import { NotificationService } from '../../core/services/notification.service';
import { Alert, AlertQuery, AlertSeverity, AlertStatus, PagedResult } from '../../core/models/api.models';
import {
  AlertStatusBadgeComponent,
  EmptyStateComponent,
  IfPermittedComponent,
  LoadingRowsComponent,
  ModalComponent,
  PageHeaderComponent,
  PaginatorComponent,
  SeverityBadgeComponent,
} from '../../shared/components/ui.components';
import { AbsoluteTimePipe, RelativeTimePipe } from '../../shared/utils/relative-time.pipe';

/** The alert inbox: filter, acknowledge, resolve (§16). */
@Component({
  selector: 'dp-alert-list',
  standalone: true,
  imports: [
    ReactiveFormsModule,
    RouterLink,
    PageHeaderComponent,
    PaginatorComponent,
    SeverityBadgeComponent,
    AlertStatusBadgeComponent,
    EmptyStateComponent,
    LoadingRowsComponent,
    ModalComponent,
    IfPermittedComponent,
    RelativeTimePipe,
    AbsoluteTimePipe
],
  template: `
    <div class="page">
      <dp-page-header
        title="Alerts"
      >
        <a routerLink="/alert-rules" class="btn btn-sm">Alert rules</a>
      </dp-page-header>

      <div class="panel">
        <div class="toolbar">
          <div class="segmented">
            @for (option of statusOptions; track option.value) {
              <button
                type="button"
                [class.active]="activeStatus() === option.value"
                (click)="setStatus(option.value)"
              >
                {{ option.label }}
              </button>
            }
          </div>

          <div class="field">
            <select [value]="query().severity ?? ''" (change)="setSeverity($event)" aria-label="Severity">
              <option value="">Any severity</option>
              @for (severity of severities; track severity) {
                <option [value]="severity">{{ severity }}</option>
              }
            </select>
          </div>

          @if (query().deviceId) {
            <span class="badge badge-accent">
              Filtered to one device
              <button type="button" class="clear-chip" aria-label="Clear device filter" (click)="clearDevice()">
                &times;
              </button>
            </span>
          }

          <span class="spacer"></span>

          <button type="button" class="btn btn-sm" (click)="load()" [disabled]="loading()">Refresh</button>
        </div>

        @if (loading()) {
          <dp-loading-rows [count]="6" />
        } @else {
        @if (result(); as page) {
          @if (page.items.length === 0) {
            <dp-empty
              title="No alerts here"
              [message]="
                activeStatus() === 'Open'
                  ? 'Nothing is currently open. That is the good outcome.'
                  : 'No alerts match these filters.'
              "
            />
          } @else {
            <div class="table-wrap">
              <table class="data">
                <thead>
                  <tr>
                    <th>Severity</th>
                    <th>Status</th>
                    <th>Device</th>
                    <th>What happened</th>
                    <th>Rule</th>
                    <th>Raised</th>
                    <th class="right">Actions</th>
                  </tr>
                </thead>
                <tbody>
                  @for (alert of page.items; track alert.alertId) {
                    <tr>
                      <td><dp-severity [severity]="alert.severity" /></td>
                      <td><dp-alert-status [status]="alert.status" /></td>
                      <td>
                        <a [routerLink]="['/devices', alert.deviceId]">{{ alert.deviceName }}</a>
                        <div class="mono subtle small">{{ alert.deviceCode }}</div>
                      </td>
                      <td class="message">
                        {{ alert.message }}
                        @if (alert.resolutionNote) {
                          <div class="subtle small">Resolution: {{ alert.resolutionNote }}</div>
                        }
                      </td>
                      <td class="muted small">{{ alert.alertRuleName ?? 'rule deleted' }}</td>
                      <td class="muted small nowrap" [title]="alert.createdAt | absoluteTime: true">
                        {{ alert.createdAt | relativeTime }}
                      </td>
                      <td class="right nowrap">
                        <dp-if-permitted [permission]="perm.alertResolve">
                          @if (alert.status === 'Open') {
                            <button
                              type="button"
                              class="btn btn-sm"
                              (click)="acknowledge(alert)"
                              [disabled]="busyId() === alert.alertId"
                            >
                              Acknowledge
                            </button>
                          }

                          @if (alert.status !== 'Resolved') {
                            <button
                              type="button"
                              class="btn btn-sm btn-primary"
                              (click)="openResolve(alert)"
                              [disabled]="busyId() === alert.alertId"
                            >
                              Resolve
                            </button>
                          }

                          @if (alert.status === 'Resolved') {
                            <span class="subtle small">
                              by {{ alert.resolvedBy ?? 'the system' }}
                            </span>
                          }
                        </dp-if-permitted>
                      </td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>

            <dp-paginator
              [page]="page.page"
              [pageSize]="page.pageSize"
              [totalCount]="page.totalCount"
              [totalPages]="page.totalPages"
              (pageChange)="goToPage($event)"
              (pageSizeChange)="setPageSize($event)"
            />
          }
        }
      }
      </div>
    </div>

    <dp-modal [open]="resolving() !== null" title="Resolve alert" (closed)="resolving.set(null)">
      <div class="panel-body stack">
        <p class="small">{{ resolving()?.message }}</p>

        <div class="field">
          <label for="note">What was done? (optional, kept with the alert)</label>
          <textarea
            id="note"
            [formControl]="resolutionNote"
            placeholder="Cleared the blocked air intake and confirmed the temperature dropped."
          ></textarea>
          <span class="field-hint">
            A note here is what makes this alert understandable to whoever reads it next month.
          </span>
        </div>
      </div>

      <div class="panel-foot row">
        <span class="spacer"></span>
        <button type="button" class="btn" (click)="resolving.set(null)">Cancel</button>
        <button type="button" class="btn btn-primary" (click)="resolve()" [disabled]="busyId() !== null">
          Resolve
        </button>
      </div>
    </dp-modal>
  `,
  changeDetection: ChangeDetectionStrategy.Eager,
  styles: [
    `

      .toolbar .field select { width: auto; min-width: 150px; }

      .message { max-width: 420px; line-height: 1.4; }

      .clear-chip {
        padding: 0 0 0 var(--sp-1);
        font: inherit;
        color: inherit;
        background: none;
        border: none;
        cursor: pointer;
      }

      td.right .btn + .btn { margin-left: var(--sp-1); }
    `,
  ],
})
export class AlertListComponent implements OnInit {
  private readonly api = inject(ApiService);
  private readonly notifications = inject(NotificationService);
  private readonly fb = inject(FormBuilder);

  readonly perm = Permissions;

  readonly result = signal<PagedResult<Alert> | null>(null);
  readonly loading = signal(true);
  readonly busyId = signal<number | null>(null);
  readonly resolving = signal<Alert | null>(null);

  readonly resolutionNote = this.fb.nonNullable.control('');

  readonly severities: AlertSeverity[] = ['Critical', 'High', 'Medium', 'Low', 'Info'];

  /**
   * "Open" is the default view. An alert inbox that opens on everything ever raised buries the
   * handful of things that currently need attention.
   */
  readonly statusOptions: { value: AlertStatus | 'All'; label: string }[] = [
    { value: 'Open', label: 'Open' },
    { value: 'Acknowledged', label: 'Acknowledged' },
    { value: 'Resolved', label: 'Resolved' },
    { value: 'All', label: 'All' },
  ];

  readonly activeStatus = signal<AlertStatus | 'All'>('Open');
  readonly query = signal<AlertQuery>({ page: 1, pageSize: 20, status: 'Open' });

  ngOnInit(): void {
    // Honours ?deviceId=, so the device list and the dashboard can link straight to a device's
    // alerts without a separate screen.
    const deviceId = new URLSearchParams(window.location.search).get('deviceId');

    if (deviceId) {
      // Status is cleared too: following a link to "this device's alerts" should show all of
      // them, not just the open ones.
      this.activeStatus.set('All');
      this.query.update((q) => ({ ...q, deviceId: Number(deviceId), status: undefined }));
    }

    this.load();
  }

  load(): void {
    this.loading.set(true);

    this.api.getAlerts(this.query()).subscribe({
      next: (page) => {
        this.result.set(page);
        this.loading.set(false);
      },
      error: () => this.loading.set(false),
    });
  }

  private patchQuery(patch: Partial<AlertQuery>): void {
    this.query.update((current) => ({ ...current, ...patch }));
    this.load();
  }

  setStatus(status: AlertStatus | 'All'): void {
    this.activeStatus.set(status);
    this.patchQuery({ status: status === 'All' ? undefined : status, page: 1 });
  }

  setSeverity(event: Event): void {
    const value = (event.target as HTMLSelectElement).value;
    this.patchQuery({ severity: (value || undefined) as AlertSeverity | undefined, page: 1 });
  }

  clearDevice(): void {
    this.patchQuery({ deviceId: undefined, page: 1 });
  }

  goToPage(page: number): void {
    this.patchQuery({ page });
  }

  setPageSize(pageSize: number): void {
    this.patchQuery({ pageSize, page: 1 });
  }

  acknowledge(alert: Alert): void {
    this.busyId.set(alert.alertId);

    this.api.acknowledgeAlert(alert.alertId).subscribe({
      next: (updated) => {
        this.busyId.set(null);
        this.replace(updated);
        this.notifications.success('Alert acknowledged.');
      },
      error: () => this.busyId.set(null),
    });
  }

  openResolve(alert: Alert): void {
    this.resolutionNote.setValue('');
    this.resolving.set(alert);
  }

  resolve(): void {
    const alert = this.resolving();

    if (!alert) {
      return;
    }

    this.busyId.set(alert.alertId);

    this.api.resolveAlert(alert.alertId, this.resolutionNote.value.trim() || null).subscribe({
      next: (updated) => {
        this.busyId.set(null);
        this.resolving.set(null);
        this.notifications.success('Alert resolved.');

        // Reloaded rather than patched in place when viewing only open alerts, because the
        // resolved row no longer belongs in the current filter.
        if (this.activeStatus() === 'Open' || this.activeStatus() === 'Acknowledged') {
          this.load();
        } else {
          this.replace(updated);
        }
      },
      error: () => this.busyId.set(null),
    });
  }

  /** Swaps one row in place, so acknowledging does not reload and lose the scroll position. */
  private replace(updated: Alert): void {
    this.result.update((page) =>
      page
        ? { ...page, items: page.items.map((a) => (a.alertId === updated.alertId ? updated : a)) }
        : page,
    );
  }
}
