import {
  Component,
  EventEmitter,
  Output,
  booleanAttribute,
  computed,
  inject,
  input,
  numberAttribute,
  ChangeDetectionStrategy
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
 *
 * Two conventions run through all of them, and they are what keep the screens looking like one
 * product rather than twelve:
 *
 *   - Nothing here draws a box. Enclosure comes from `.panel`, used where it genuinely helps;
 *     these components contribute type, colour and spacing only.
 *   - Status is coloured text with a marker, not a filled pill. A table of forty rows with
 *     forty lozenges reads as decoration competing with the data it is describing.
 */

/**
 * Page title and page-level actions, on one baseline, over a rule.
 *
 * There is deliberately no description input. Twelve pages previously carried a sentence of
 * explanatory subtitle that nobody reads after the first visit and that costs a line of
 * vertical space on every screen forever. Where a screen genuinely needs to explain itself, it
 * does so next to the control that needs explaining.
 */
@Component({
  selector: 'dp-page-header',
  standalone: true,
  imports: [],
  template: `
    <header class="page-head">
      <h1>{{ title() }}</h1>
      @if (context()) {
        <span class="context small">{{ context() }}</span>
      }
      <span class="spacer"></span>
      <div class="actions">
        <ng-content />
      </div>
    </header>
  `,
  changeDetection: ChangeDetectionStrategy.Eager,
  styles: [
    `
      .page-head {
        display: flex;
        gap: var(--sp-3);
        align-items: baseline;
        flex-wrap: wrap;
        padding-bottom: var(--sp-3);
        margin-bottom: var(--sp-5);
        border-bottom: 1px solid var(--line);
      }

      /* A short live fact, not prose: "24 devices, 3 offline". */
      .context { color: var(--text-3); }

      .actions {
        display: flex;
        gap: var(--sp-2);
        align-items: center;
        flex-wrap: wrap;
      }
    `,
  ],
})
export class PageHeaderComponent {
  readonly title = input.required<string>();
  /** A short live fact about the page, shown beside the title. Not a sentence of prose. */
  readonly context = input<string>('');
}

/**
 * A readout: a small label over a large tabular figure.
 *
 * Deliberately not a card. Five bordered, rounded, shadowed metric boxes in a row is the most
 * recognisable template pattern there is; bare readouts separated by space read as an
 * instrument cluster, and they sit closer together so the figures can actually be compared.
 */
@Component({
  selector: 'dp-stat',
  standalone: true,
  imports: [],
  template: `
    <div class="readout" [class.readout-lead]="lead()">
      <span class="label">{{ label() }}</span>
      <span class="readout-value" [style.color]="color() || null">{{ value() }}</span>
      @if (hint()) {
        <span class="readout-hint">{{ hint() }}</span>
      }
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.Eager,
})
export class StatComponent {
  readonly label = input.required<string>();
  readonly value = input.required<string | number>();
  readonly hint = input<string>('');
  readonly color = input<string>('');
  /** The one figure on a screen that answers its central question. At most one per screen. */
  readonly lead = input(false, { transform: booleanAttribute });
  /** Retained so existing call sites keep compiling; `lead` is the replacement. */
  readonly accent = input(false, { transform: booleanAttribute });
}

/**
 * Connectivity: is the device talking to us right now?
 *
 * A filled marker means reporting, a hollow one means known to be silent, and grey means it has
 * never reported at all. The three states are distinguishable without reading the word, which
 * is what lets an operator scan a column of forty rows instead of parsing it.
 */
@Component({
  selector: 'dp-connectivity',
  standalone: true,
  imports: [],
  changeDetection: ChangeDetectionStrategy.Eager,
  template: `
    <span class="stat-text" [class]="toneClass()">
      <span class="dot" [class.dot-hollow]="hollow()"></span>{{ status() }}
    </span>
  `,
})
export class ConnectivityBadgeComponent {
  readonly status = input.required<ConnectivityStatus>();

  readonly toneClass = computed(() => {
    switch (this.status()) {
      case 'Online':
        // The normal state: a green marker beside plain text. A column of forty green words
        // made "fine" the loudest thing in the table.
        return 'stat-calm stat-ok';
      case 'Offline':
        return 'stat-danger';
      default:
        // Unknown is not a fault -- it means the device has simply never reported -- so it
        // recedes rather than shouting.
        return 'stat-quiet';
    }
  });

  readonly hollow = computed(() => this.status() === 'Offline');
}

/**
 * Lifecycle: where the device sits administratively.
 *
 * This is metadata, not health, so it is plain text at secondary weight. A coloured pill here,
 * next to the connectivity column, made two unrelated things look equally urgent.
 */
@Component({
  selector: 'dp-lifecycle',
  standalone: true,
  imports: [],
  changeDetection: ChangeDetectionStrategy.Eager,
  template: `<span class="lifecycle" [class.retired]="status() === 'Retired'">{{ status() }}</span>`,
  styles: [
    `
      .lifecycle {
        font-size: var(--fs-sm);
        color: var(--text-2);
        white-space: nowrap;
      }

      /* Retired is the one lifecycle state that changes how the whole row should be read. */
      .retired {
        color: var(--text-3);
        text-decoration: line-through;
        text-decoration-color: var(--line-strong);
      }
    `,
  ],
})
export class LifecycleBadgeComponent {
  readonly status = input.required<LifecycleStatus>();
}

/**
 * Severity, on the monotonic scale.
 *
 * Drawn as a four-step level meter beside the word: Info lights none, Critical lights all four.
 * The steps make the ordering readable down a column without relying on telling five hues apart,
 * which also keeps it legible for colour-blind operators.
 */
@Component({
  selector: 'dp-severity',
  standalone: true,
  imports: [],
  template: `
    <span class="sev" [style.--sev]="color()">
      <span class="meter" aria-hidden="true">
        @for (step of steps; track step) {
          <i [class.on]="step <= level()"></i>
        }
      </span>
      {{ severity() }}
    </span>
  `,
  changeDetection: ChangeDetectionStrategy.Eager,
  styles: [
    `
      .sev {
        display: inline-flex;
        gap: var(--sp-2);
        align-items: center;
        font-size: var(--fs-sm);
        color: var(--text);
        white-space: nowrap;
      }

      .meter {
        display: inline-flex;
        gap: 2px;
        align-items: flex-end;
        height: 10px;
      }

      .meter i {
        width: 3px;
        background: var(--line-strong);
        border-radius: 0.5px;
      }

      .meter i:nth-child(1) { height: 4px; }
      .meter i:nth-child(2) { height: 6px; }
      .meter i:nth-child(3) { height: 8px; }
      .meter i:nth-child(4) { height: 10px; }

      .meter i.on { background: var(--sev); }
    `,
  ],
})
export class SeverityBadgeComponent {
  readonly severity = input.required<AlertSeverity>();

  protected readonly steps = [1, 2, 3, 4];

  readonly color = computed(() => severityColor(this.severity()));

  readonly level = computed(() => severityLevel(this.severity()));
}

/** Info 0 through Critical 4: how many steps of the meter light up. */
export function severityLevel(severity: AlertSeverity | null): number {
  switch (severity) {
    case 'Critical':
      return 4;
    case 'High':
      return 3;
    case 'Medium':
      return 2;
    case 'Low':
      return 1;
    default:
      return 0;
  }
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
      return 'var(--text-3)';
  }
}

/** Alert status: open, claimed by someone, or closed. */
@Component({
  selector: 'dp-alert-status',
  standalone: true,
  imports: [],
  changeDetection: ChangeDetectionStrategy.Eager,
  template: `
    <span class="stat-text" [class]="toneClass()">
      <span class="dot" [class.dot-hollow]="status() === 'Acknowledged'"></span>{{ status() }}
    </span>
  `,
})
export class AlertStatusBadgeComponent {
  readonly status = input.required<AlertStatus>();

  readonly toneClass = computed(() => {
    switch (this.status()) {
      case 'Open':
        return 'stat-calm stat-danger';
      case 'Acknowledged':
        return 'stat-calm stat-warn';
      default:
        return 'stat-quiet';
    }
  });
}

/**
 * Pagination bound to the API's PagedResult shape.
 *
 * Shows the row range rather than only page numbers: "41-60 of 237" answers "how much is
 * there", which "page 3 of 12" does not.
 */
@Component({
  selector: 'dp-paginator',
  standalone: true,
  imports: [],
  template: `
    @if (totalCount() > 0) {
      <div class="paginator">
        <span class="small text-2 num nowrap">
          {{ firstRow() }}&ndash;{{ lastRow() }} of {{ totalCount() }}
        </span>

        <span class="spacer"></span>

        <label class="size small text-3">
          Rows
          <select [value]="pageSize()" (change)="changeSize($event)" aria-label="Rows per page">
            @for (size of sizes; track size) {
              <option [value]="size">{{ size }}</option>
            }
          </select>
        </label>

        <div class="row row-tight">
          <button
            type="button"
            class="btn btn-sm"
            [disabled]="page() <= 1"
            (click)="pageChange.emit(page() - 1)"
            aria-label="Previous page"
          >
            Prev
          </button>

          <span class="small text-3 num nowrap">{{ page() }} / {{ totalPages() || 1 }}</span>

          <button
            type="button"
            class="btn btn-sm"
            [disabled]="page() >= totalPages()"
            (click)="pageChange.emit(page() + 1)"
            aria-label="Next page"
          >
            Next
          </button>
        </div>
      </div>
    }
  `,
  changeDetection: ChangeDetectionStrategy.Eager,
  styles: [
    `
      .paginator {
        display: flex;
        gap: var(--sp-3);
        align-items: center;
        flex-wrap: wrap;
        padding: var(--sp-2) var(--sp-3);
        border-top: 1px solid var(--line);
      }

      .size {
        display: inline-flex;
        gap: var(--sp-2);
        align-items: center;
      }

      .size select {
        width: auto;
        height: 26px;
        padding: 0 var(--sp-5) 0 var(--sp-2);
        font-size: var(--fs-meta);
        background-position: calc(100% - 13px) 11px, calc(100% - 9px) 11px;
      }
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

/**
 * Shown in place of a table body while the first load is in flight.
 *
 * The placeholders use varied widths so the block reads as lines of text rather than as a set
 * of identical bars, and they match the real row height so content does not jump when it lands.
 */
@Component({
  selector: 'dp-loading-rows',
  standalone: true,
  imports: [],
  template: `
    <div class="rows" [attr.aria-busy]="true" aria-label="Loading">
      @for (row of placeholders(); track $index) {
        <div class="skeleton" [style.width.%]="widthFor($index)"></div>
      }
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.Eager,
  styles: [
    `
      .rows {
        display: grid;
        gap: var(--sp-4);
        padding: var(--sp-4) var(--sp-3);
      }

      .skeleton { height: 13px; }
    `,
  ],
})
export class LoadingRowsComponent {
  readonly count = input(5, { transform: numberAttribute });
  readonly placeholders = computed(() => Array.from({ length: this.count() }));

  widthFor(index: number): number {
    return [92, 68, 84, 74, 88, 62][index % 6];
  }
}

/** Message shown when a list has no rows, with a slot for a call to action. */
@Component({
  selector: 'dp-empty',
  standalone: true,
  imports: [],
  changeDetection: ChangeDetectionStrategy.Eager,
  template: `
    <div class="empty-state">
      <h3>{{ title() }}</h3>
      @if (message()) {
        <p>{{ message() }}</p>
      }
      <div class="row">
        <ng-content />
      </div>
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
 *
 * The backdrop is a flat scrim, not a blur. A blurred backdrop is the glassmorphism tell, and
 * it costs a compositing pass on every frame to communicate nothing the scrim does not.
 */
@Component({
  selector: 'dp-modal',
  standalone: true,
  imports: [],
  template: `
    @if (open()) {
      <div
        class="backdrop"
        role="presentation"
        (click)="closed.emit()"
        (keydown.escape)="closed.emit()"
      >
        <div
          class="panel sheet"
          role="dialog"
          aria-modal="true"
          [attr.aria-label]="title()"
          [style.max-width]="width()"
          (click)="$event.stopPropagation()"
        >
          <div class="sheet-head">
            <h2>{{ title() }}</h2>
            <span class="spacer"></span>
            <button type="button" class="btn btn-sm btn-icon btn-ghost" aria-label="Close" (click)="closed.emit()">
              &times;
            </button>
          </div>

          <ng-content />
        </div>
      </div>
    }
  `,
  changeDetection: ChangeDetectionStrategy.Eager,
  styles: [
    `
      .backdrop {
        position: fixed;
        inset: 0;
        z-index: 100;
        display: grid;
        place-items: start center;
        padding: 6vh var(--sp-4) var(--sp-6);
        overflow-y: auto;
        background: rgb(12 15 19 / 50%);
      }

      .sheet {
        width: 100%;
        border-radius: var(--r-lg);
        box-shadow: var(--shadow-modal);
        animation: rise 0.12s ease-out;
      }

      /* Sticky so the title and the close button stay reachable in a long form. */
      .sheet-head {
        position: sticky;
        top: 0;
        z-index: 1;
        display: flex;
        gap: var(--sp-3);
        align-items: center;
        padding: var(--sp-3) var(--sp-4);
        background: var(--panel);
        border-bottom: 1px solid var(--line);
        border-radius: var(--r-lg) var(--r-lg) 0 0;
      }

      .sheet-head h2 { font-size: var(--fs-lg); }

      @keyframes rise {
        from { transform: translateY(6px); opacity: 0; }
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
  imports: [],
  changeDetection: ChangeDetectionStrategy.Eager,
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
  imports: [],
  changeDetection: ChangeDetectionStrategy.Eager,
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
