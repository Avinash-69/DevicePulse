import { Component, OnInit, inject, signal, ChangeDetectionStrategy } from '@angular/core';
import { FormBuilder, ReactiveFormsModule } from '@angular/forms';
import { debounceTime, distinctUntilChanged } from 'rxjs';

import { ApiService } from '../../core/services/api.service';
import { AuditEntry, AuditQuery, PagedResult } from '../../core/models/api.models';
import {
  EmptyStateComponent,
  LoadingRowsComponent,
  PageHeaderComponent,
  PaginatorComponent,
} from '../../shared/components/ui.components';
import { AbsoluteTimePipe, RelativeTimePipe } from '../../shared/utils/relative-time.pipe';

/**
 * The administrative audit trail (§17).
 *
 * Read-only, and there is no write endpoint to call even if this page wanted one — an audit
 * trail an administrator can edit is not an audit trail.
 *
 * Old and new values are stored as JSON of just the changed fields, so this page renders them
 * as a field-by-field diff. That is what turns "a setting was changed" into "the offline
 * timeout went from 300 to 90, by this person, for this reason".
 */
@Component({
  selector: 'dp-audit-log',
  standalone: true,
  imports: [
    ReactiveFormsModule,
    PageHeaderComponent,
    PaginatorComponent,
    EmptyStateComponent,
    LoadingRowsComponent,
    RelativeTimePipe,
    AbsoluteTimePipe
],
  template: `
    <div class="page">
      <dp-page-header
        title="Audit log"
        description="Who changed what, when, and from what value. Read-only by design."
      />

      <div class="card">
        <div class="filters">
          <div class="field search">
            <input
              type="search"
              placeholder="Search action, entity or user"
              [formControl]="searchControl"
              aria-label="Search the audit log"
            />
          </div>

          <div class="field">
            <select [value]="query().entityType ?? ''" (change)="setEntityType($event)" aria-label="Entity type">
              <option value="">Any entity</option>
              @for (type of entityTypes; track type) {
                <option [value]="type">{{ type }}</option>
              }
            </select>
          </div>

          <div class="field">
            <input
              type="datetime-local"
              [formControl]="fromControl"
              aria-label="From"
              title="From"
            />
          </div>

          <div class="field">
            <input type="datetime-local" [formControl]="toControl" aria-label="To" title="To" />
          </div>

          @if (hasFilters()) {
            <button type="button" class="btn btn-sm btn-ghost" (click)="clear()">Clear</button>
          }
        </div>

        @if (loading()) {
          <dp-loading-rows [count]="8" />
        } @else {
          @if (result(); as page) {
            @if (page.items.length === 0) {
              <dp-empty
                title="Nothing recorded"
                [message]="
                  hasFilters()
                    ? 'No entries match these filters.'
                    : 'Administrative changes will appear here as they happen.'
                "
              />
            } @else {
              <div class="entries">
                @for (entry of page.items; track entry.auditId) {
                  <article class="entry">
                    <div class="entry-head">
                      <span class="action">{{ entry.action }}</span>
                      <span class="entity muted small">
                        {{ entry.entityType }}@if (entry.entityId) {
                          <span class="mono"> · {{ entry.entityId }}</span>
                        }
                      </span>
                      <span class="spacer"></span>
                      <span class="muted small nowrap" [title]="entry.timestamp | absoluteTime: true">
                        {{ entry.timestamp | relativeTime }}
                      </span>
                    </div>

                    <div class="entry-who muted small">
                      <span>{{ entry.userEmail ?? 'system' }}</span>
                      @if (entry.ipAddress) {
                        <span class="mono subtle">{{ entry.ipAddress }}</span>
                      }
                      @if (entry.correlationId) {
                        <span class="mono subtle" title="Correlation ID for this request">
                          {{ entry.correlationId }}
                        </span>
                      }
                    </div>

                    @if (diffFor(entry); as diff) {
                      @if (diff.length > 0) {
                        <table class="diff">
                          <tbody>
                            @for (row of diff; track row.field) {
                              <tr>
                                <th>{{ row.field }}</th>
                                <td class="old mono">{{ row.before }}</td>
                                <td class="arrow" aria-hidden="true">→</td>
                                <td class="new mono">{{ row.after }}</td>
                              </tr>
                            }
                          </tbody>
                        </table>
                      }
                    }
                  </article>
                }
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
  `,
  changeDetection: ChangeDetectionStrategy.Eager,
  styles: [
    `
      .filters {
        display: flex;
        gap: 0.6rem;
        align-items: center;
        flex-wrap: wrap;
        padding: 0.8rem 1rem;
        border-bottom: 1px solid var(--border);
      }

      .filters .field select,
      .filters .field input[type='datetime-local'] { width: auto; }
      .filters .search { flex: 1 1 220px; }

      .entries { display: grid; }

      .entry {
        padding: 0.8rem 1.1rem;
        border-bottom: 1px solid var(--border);
      }

      .entry:last-child { border-bottom: none; }

      .entry-head {
        display: flex;
        gap: 0.55rem;
        align-items: baseline;
        flex-wrap: wrap;
      }

      .action {
        font-size: 0.85rem;
        font-weight: 600;
      }

      .entry-who {
        display: flex;
        gap: 0.7rem;
        flex-wrap: wrap;
        margin-top: 0.2rem;
      }

      .diff {
        width: auto;
        margin-top: 0.55rem;
        border-collapse: collapse;
        font-size: 0.78rem;
      }

      .diff th {
        padding: 0.15rem 0.6rem 0.15rem 0;
        text-align: left;
        font-weight: 500;
        color: var(--text-muted);
        white-space: nowrap;
      }

      .diff td { padding: 0.15rem 0.3rem; }

      .old {
        color: var(--text-muted);
        text-decoration: line-through;
      }

      .new { color: var(--accent); font-weight: 600; }
      .arrow { color: var(--text-subtle); }
    `,
  ],
})
export class AuditLogComponent implements OnInit {
  private readonly api = inject(ApiService);
  private readonly fb = inject(FormBuilder);

  readonly result = signal<PagedResult<AuditEntry> | null>(null);
  readonly loading = signal(true);
  readonly query = signal<AuditQuery>({ page: 1, pageSize: 25 });

  readonly searchControl = this.fb.nonNullable.control('');
  readonly fromControl = this.fb.nonNullable.control('');
  readonly toControl = this.fb.nonNullable.control('');

  /**
   * The entity types the backend writes audit entries for. Hardcoded because the API exposes no
   * "distinct entity types" endpoint, and a free-text box here would be worse — the user would
   * have to guess the exact spelling.
   */
  readonly entityTypes = [
    'User',
    'Role',
    'Device',
    'AlertRule',
    'Alert',
    'SystemSetting',
    'DeviceType',
    'Location',
  ];

  /** Parsed diffs, cached per entry so an impure render does not re-parse JSON every pass. */
  private readonly diffCache = new Map<number, { field: string; before: string; after: string }[]>();

  ngOnInit(): void {
    this.load();

    this.searchControl.valueChanges
      .pipe(debounceTime(300), distinctUntilChanged())
      .subscribe((term) => this.patchQuery({ search: term || undefined, page: 1 }));

    this.fromControl.valueChanges
      .pipe(debounceTime(400), distinctUntilChanged())
      .subscribe((value) => this.patchQuery({ from: toIso(value), page: 1 }));

    this.toControl.valueChanges
      .pipe(debounceTime(400), distinctUntilChanged())
      .subscribe((value) => this.patchQuery({ to: toIso(value), page: 1 }));
  }

  private load(): void {
    this.loading.set(true);

    this.api.getAuditLog(this.query()).subscribe({
      next: (page) => {
        this.diffCache.clear();
        this.result.set(page);
        this.loading.set(false);
      },
      error: () => this.loading.set(false),
    });
  }

  private patchQuery(patch: Partial<AuditQuery>): void {
    this.query.update((current) => ({ ...current, ...patch }));
    this.load();
  }

  goToPage(page: number): void {
    this.patchQuery({ page });
  }

  setPageSize(pageSize: number): void {
    this.patchQuery({ pageSize, page: 1 });
  }

  setEntityType(event: Event): void {
    const value = (event.target as HTMLSelectElement).value;
    this.patchQuery({ entityType: value || undefined, page: 1 });
  }

  hasFilters(): boolean {
    const q = this.query();
    return Boolean(q.search || q.entityType || q.from || q.to);
  }

  clear(): void {
    this.searchControl.setValue('', { emitEvent: false });
    this.fromControl.setValue('', { emitEvent: false });
    this.toControl.setValue('', { emitEvent: false });
    this.query.set({ page: 1, pageSize: this.query().pageSize });
    this.load();
  }

  /**
   * Turns the stored old/new JSON into a field-by-field diff.
   *
   * Only fields that actually differ are shown. The backend records the whole changed
   * projection, which often includes fields that happened to stay the same, and listing those
   * as changes would make every edit look bigger than it was.
   */
  diffFor(entry: AuditEntry): { field: string; before: string; after: string }[] {
    const cached = this.diffCache.get(entry.auditId);

    if (cached) {
      return cached;
    }

    const before = parseJson(entry.oldValue);
    const after = parseJson(entry.newValue);

    const fields = [...new Set([...Object.keys(before), ...Object.keys(after)])];

    const rows = fields
      .map((field) => ({
        field: humanise(field),
        before: format(before[field]),
        after: format(after[field]),
      }))
      .filter((row) => row.before !== row.after);

    this.diffCache.set(entry.auditId, rows);
    return rows;
  }
}

function parseJson(value: string | null): Record<string, unknown> {
  if (!value) {
    return {};
  }

  try {
    const parsed = JSON.parse(value);

    // Some entries record an array (a role name list, for instance) rather than an object.
    if (Array.isArray(parsed)) {
      return { value: parsed };
    }

    return typeof parsed === 'object' && parsed !== null ? (parsed as Record<string, unknown>) : { value: parsed };
  } catch {
    // Not JSON — show it as a single opaque value rather than dropping it.
    return { value };
  }
}

function format(value: unknown): string {
  if (value === undefined) {
    return '—';
  }

  if (value === null) {
    return 'none';
  }

  if (Array.isArray(value)) {
    return value.length === 0 ? 'none' : value.join(', ');
  }

  if (typeof value === 'object') {
    return JSON.stringify(value);
  }

  return String(value);
}

/** "LifecycleStatus" becomes "Lifecycle status", which reads better in a diff table. */
function humanise(field: string): string {
  const spaced = field.replace(/([a-z0-9])([A-Z])/g, '$1 $2');
  return spaced.charAt(0).toUpperCase() + spaced.slice(1).toLowerCase();
}

/** datetime-local gives a zone-less local string; the API expects an ISO instant. */
function toIso(value: string): string | undefined {
  if (!value) {
    return undefined;
  }

  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? undefined : date.toISOString();
}
