import { CommonModule } from '@angular/common';
import {
  Component,
  EventEmitter,
  Output,
  booleanAttribute,
  computed,
  inject,
  input,
  numberAttribute,
} from '@angular/core';

import { AlertSeverity, AlertStatus, ConnectivityStatus, LifecycleStatus } from '../../core/models/api.models';
import { AuthService } from '../../core/auth/auth.service';
import { NotificationService } from '../../core/services/notification.service';

/**
 * Small presentational components shared across the feature pages.
 *
 * Collected in one file because each is a handful of lines and a template; one file per
 * five-line component would be more navigation than structure. Anything that grows real
 * behaviour gets its own file.
 */

/** Page title, optional description, and a slot for page-level actions. */
@Component({
  selector: 'dp-page-header',
  standalone: true,
  imports: [CommonModule],
  template: `
    <header class="header">
      <div class="titles">
        <h1>{{ title() }}</h1>
        @if (description()) {
          <p class="muted small">{{ description() }}</p>
        }
      </div>
      <div class="actions">
        <ng-content />
      </div>
    </header>
  `,
  styles: [
    `
      .header {
        display: flex;
        gap: 1rem;
        align-items: flex-start;
        margin-bottom: 1.25rem;
        flex-wrap: wrap;
      }

      .titles { flex: 1 1 320px; }
      .titles p { margin: 0.2rem 0 0; max-width: 72ch; }

      .actions {
        display: flex;
        gap: 0.5rem;
        align-items: center;
        flex-wrap: wrap;
      }
    `,
  ],
})
export class PageHeaderComponent {
  readonly title = input.required<string>();
  readonly description = input<string>('');
}

/** Headline number with a label, used for the dashboard counters. */
@Component({
  selector: 'dp-stat',
  standalone: true,
  imports: [CommonModule],
  template: `
    <div class="stat card" [class.accent]="accent()">
      <span class="label">{{ label() }}</span>
      <span class="value" [style.color]="color() || null">{{ value() }}</span>
      @if (hint()) {
        <span class="hint subtle small">{{ hint() }}</span>
      }
    </div>
  `,
  styles: [
    `
      .stat {
        display: grid;
        gap: 0.2rem;
        padding: 0.9rem 1rem;
        align-content: start;
      }

      .label {
        font-size: 0.72rem;
        font-weight: 600;
        text-transform: uppercase;
        letter-spacing: 0.04em;
        color: var(--text-muted);
      }

      .value {
        font-size: 1.75rem;
        font-weight: 600;
        line-height: 1.15;
        font-variant-numeric: tabular-nums;
      }

      .accent { border-color: var(--accent); }
    `,
  ],
})
export class StatComponent {
  readonly label = input.required<string>();
  readonly value = input.required<string | number>();
  readonly hint = input<string>('');
  readonly color = input<string>('');
  readonly accent = input(false, { transform: booleanAttribute });
}

/** Connectivity badge. Separate from lifecycle, matching the backend's split. */
@Component({
  selector: 'dp-connectivity',
  standalone: true,
  imports: [CommonModule],
  template: `
    <span class="badge" [class]="cssClass()">
      <span class="dot"></span>{{ status() }}
    </span>
  `,
})
export class ConnectivityBadgeComponent {
  readonly status = input.required<ConnectivityStatus>();

  readonly cssClass = computed(() => {
    switch (this.status()) {
      case 'Online':
        return 'badge-ok';
      case 'Offline':
        return 'badge-danger';
      default:
        // Unknown is not a problem — it means the device has simply never reported — so it
        // reads as neutral rather than as a failure.
        return 'badge-neutral';
    }
  });
}

/** Lifecycle badge: where the device sits administratively. */
@Component({
  selector: 'dp-lifecycle',
  standalone: true,
  imports: [CommonModule],
  template: `<span class="badge" [class]="cssClass()">{{ status() }}</span>`,
})
export class LifecycleBadgeComponent {
  readonly status = input.required<LifecycleStatus>();

  readonly cssClass = computed(() => {
    switch (this.status()) {
      case 'Active':
        return 'badge-ok';
      case 'Registered':
        return 'badge-info';
      case 'Inactive':
        return 'badge-warn';
      default:
        return 'badge-neutral';
    }
  });
}

/** Severity badge, coloured on the monotonic severity scale. */
@Component({
  selector: 'dp-severity',
  standalone: true,
  imports: [CommonModule],
  template: `
    <span class="sev" [style.--sev]="color()">
      <span class="dot"></span>{{ severity() }}
    </span>
  `,
  styles: [
    `
      .sev {
        display: inline-flex;
        gap: 0.3rem;
        align-items: center;
        padding: 0.15rem 0.5rem;
        font-size: 0.72rem;
        font-weight: 600;
        color: var(--sev);
        background: color-mix(in srgb, var(--sev) 14%, transparent);
        border-radius: 999px;
        white-space: nowrap;
      }
    `,
  ],
})
export class SeverityBadgeComponent {
  readonly severity = input.required<AlertSeverity>();

  readonly color = computed(() => severityColor(this.severity()));
}

export function severityColor(severity: AlertSeverity | null): string {
  switch (severity) {
    case 'Critical':
      return 'var(--sev-critical)';
    case 'High':
      return 'var(--sev-high)';
    case 'Medium':
      return 'var(--sev-medium)';
    case 'Low':
      return 'var(--sev-low)';
    case 'Info':
      return 'var(--sev-info)';
    default:
      return 'var(--text-subtle)';
  }
}

/** Alert status badge. */
@Component({
  selector: 'dp-alert-status',
  standalone: true,
  imports: [CommonModule],
  template: `<span class="badge" [class]="cssClass()">{{ status() }}</span>`,
})
export class AlertStatusBadgeComponent {
  readonly status = input.required<AlertStatus>();

  readonly cssClass = computed(() => {
    switch (this.status()) {
      case 'Open':
        return 'badge-danger';
      case 'Acknowledged':
        return 'badge-warn';
      default:
        return 'badge-ok';
    }
  });
}

/**
 * Pagination controls bound to the API's PagedResult shape.
 *
 * Shows the row range rather than only page numbers: "41–60 of 237" answers "how much is
 * there" in a way that "page 3 of 12" does not.
 */
@Component({
  selector: 'dp-paginator',
  standalone: true,
  imports: [CommonModule],
  template: `
    @if (totalCount() > 0) {
      <div class="paginator">
        <span class="muted small">
          {{ firstRow() }}&ndash;{{ lastRow() }} of {{ totalCount() }}
        </span>

        <span class="spacer"></span>

        <label class="muted small size">
          Rows
          <select [value]="pageSize()" (change)="changeSize($event)">
            @for (size of sizes; track size) {
              <option [value]="size">{{ size }}</option>
            }
          </select>
        </label>

        <div class="row">
          <button
            type="button"
            class="btn btn-sm"
            [disabled]="page() <= 1"
            (click)="pageChange.emit(page() - 1)"
          >
            Previous
          </button>

          <span class="muted small nowrap">Page {{ page() }} of {{ totalPages() || 1 }}</span>

          <button
            type="button"
            class="btn btn-sm"
            [disabled]="page() >= totalPages()"
            (click)="pageChange.emit(page() + 1)"
          >
            Next
          </button>
        </div>
      </div>
    }
  `,
  styles: [
    `
      .paginator {
        display: flex;
        gap: 0.75rem;
        align-items: center;
        flex-wrap: wrap;
        padding: 0.7rem 1rem;
        border-top: 1px solid var(--border);
      }

      .size { display: inline-flex; gap: 0.4rem; align-items: center; }
      .size select { width: auto; padding: 0.2rem 0.4rem; font-size: 0.8rem; }
    `,
  ],
})
export class PaginatorComponent {
  readonly page = input(1, { transform: numberAttribute });
  readonly pageSize = input(20, { transform: numberAttribute });
  readonly totalCount = input(0, { transform: numberAttribute });
  readonly totalPages = input(0, { transform: numberAttribute });

  @Output() readonly pageChange = new EventEmitter<number>();
  @Output() readonly pageSizeChange = new EventEmitter<number>();

  readonly sizes = [10, 20, 50, 100];

  readonly firstRow = computed(() =>
    this.totalCount() === 0 ? 0 : (this.page() - 1) * this.pageSize() + 1,
  );

  readonly lastRow = computed(() => Math.min(this.page() * this.pageSize(), this.totalCount()));

  changeSize(event: Event): void {
    this.pageSizeChange.emit(Number((event.target as HTMLSelectElement).value));
  }
}

/** Shown in place of a table body while the first load is in flight. */
@Component({
  selector: 'dp-loading-rows',
  standalone: true,
  imports: [CommonModule],
  template: `
    <div class="rows" [attr.aria-busy]="true" aria-label="Loading">
      @for (row of placeholders(); track $index) {
        <div class="skeleton" [style.height.px]="16"></div>
      }
    </div>
  `,
  styles: [
    `
      .rows { display: grid; gap: 0.85rem; padding: 1.1rem; }
    `,
  ],
})
export class LoadingRowsComponent {
  readonly count = input(5, { transform: numberAttribute });
  readonly placeholders = computed(() => Array.from({ length: this.count() }));
}

/** Message shown when a list has no rows, with a slot for a call to action. */
@Component({
  selector: 'dp-empty',
  standalone: true,
  imports: [CommonModule],
  template: `
    <div class="empty-state">
      <h3>{{ title() }}</h3>
      @if (message()) {
        <p class="muted small">{{ message() }}</p>
      }
      <ng-content />
    </div>
  `,
})
export class EmptyStateComponent {
  readonly title = input.required<string>();
  readonly message = input<string>('');
}

/**
 * A modal dialog.
 *
 * Closes on Escape and on a backdrop click, because a dialog that traps the user is worse than
 * no dialog. The host sets `[open]`; the component only reports that the user asked to close.
 */
@Component({
  selector: 'dp-modal',
  standalone: true,
  imports: [CommonModule],
  template: `
    @if (open()) {
      <div
        class="backdrop"
        role="presentation"
        (click)="closed.emit()"
        (keydown.escape)="closed.emit()"
      >
        <div
          class="panel card"
          role="dialog"
          aria-modal="true"
          [attr.aria-label]="title()"
          [style.max-width]="width()"
          (click)="$event.stopPropagation()"
        >
          <div class="card-header">
            <h2>{{ title() }}</h2>
            <span class="spacer"></span>
            <button type="button" class="btn btn-ghost btn-icon" aria-label="Close" (click)="closed.emit()">
              &times;
            </button>
          </div>

          <ng-content />
        </div>
      </div>
    }
  `,
  styles: [
    `
      .backdrop {
        position: fixed;
        inset: 0;
        z-index: 100;
        display: grid;
        place-items: start center;
        padding: 4vh 1rem;
        overflow-y: auto;
        background: rgb(8 12 16 / 55%);
        backdrop-filter: blur(2px);
      }

      .panel {
        width: 100%;
        box-shadow: var(--shadow-lg);
        animation: rise 0.14s ease-out;
      }

      @keyframes rise {
        from { transform: translateY(8px); opacity: 0; }
        to { transform: translateY(0); opacity: 1; }
      }
    `,
  ],
})
export class ModalComponent {
  readonly open = input.required<boolean>();
  readonly title = input.required<string>();
  readonly width = input<string>('560px');

  @Output() readonly closed = new EventEmitter<void>();
}

/** Copies a value to the clipboard and confirms it. Used for device API keys and trace ids. */
@Component({
  selector: 'dp-copy',
  standalone: true,
  imports: [CommonModule],
  template: `
    <button type="button" class="btn btn-sm" (click)="copy()" [attr.aria-label]="'Copy ' + label()">
      {{ copied ? 'Copied' : 'Copy' }}
    </button>
  `,
})
export class CopyButtonComponent {
  private readonly notifications = inject(NotificationService);

  readonly value = input.required<string>();
  readonly label = input<string>('value');

  copied = false;

  async copy(): Promise<void> {
    try {
      await navigator.clipboard.writeText(this.value());
      this.copied = true;
      setTimeout(() => (this.copied = false), 1800);
    } catch {
      // The Clipboard API needs a secure context and permission, neither of which is
      // guaranteed. Saying so beats a button that silently does nothing.
      this.notifications.warning('Could not copy automatically. Select the value and copy it manually.');
    }
  }
}

/**
 * Renders its content only when the user holds one of the given permissions.
 *
 * A convenience, not a control. Hiding a button is UX; the backend returning 403 is the
 * security boundary (§29). Named as a component rather than a directive so it can wrap a block.
 */
@Component({
  selector: 'dp-if-permitted',
  standalone: true,
  imports: [CommonModule],
  template: `
    @if (allowed()) {
      <ng-content />
    }
  `,
})
export class IfPermittedComponent {
  private readonly auth = inject(AuthService);

  /** One permission key, or several separated by spaces (any one of them is enough). */
  readonly permission = input.required<string>();

  readonly allowed = computed(() => this.auth.hasAny(...this.permission().split(/\s+/).filter(Boolean)));
}
