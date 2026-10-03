import { Component, inject, ChangeDetectionStrategy } from '@angular/core';

import { NotificationService } from '../core/services/notification.service';

/** Renders the notification queue. Lives in the shell and on the login page. */
@Component({
  selector: 'dp-toasts',
  standalone: true,
  imports: [],
  template: `
    <div class="toasts" aria-live="polite" aria-atomic="false">
      @for (notice of notifications.notices(); track notice.id) {
        <div class="toast card" [class]="'toast-' + notice.kind" role="status">
          <div class="body">
            <p class="message">{{ notice.message }}</p>
            @if (notice.detail) {
              <p class="detail mono subtle">{{ notice.detail }}</p>
            }
          </div>

          <button
            type="button"
            class="btn btn-ghost btn-icon"
            aria-label="Dismiss"
            (click)="notifications.dismiss(notice.id)"
          >
            &times;
          </button>
        </div>
      }
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.Eager,
  styles: [
    `
      .toasts {
        position: fixed;
        bottom: 1rem;
        right: 1rem;
        z-index: 200;
        display: grid;
        gap: var(--sp-2);
        width: min(380px, calc(100vw - 2rem));
        /* The container must not swallow clicks on the page behind it; each toast re-enables
           pointer events for itself. */
        pointer-events: none;
      }

      .toast {
        display: flex;
        gap: var(--sp-2);
        align-items: flex-start;
        padding: var(--sp-3) var(--sp-3) var(--sp-3) var(--sp-4);
        border-left: 3px solid var(--quiet);
        box-shadow: var(--shadow-modal);
        pointer-events: auto;
        animation: slide-in 0.16s ease-out;
      }

      @keyframes slide-in {
        from { transform: translateX(12px); opacity: 0; }
        to { transform: translateX(0); opacity: 1; }
      }

      .toast-success { border-left-color: var(--ok); }
      .toast-warning { border-left-color: var(--warn); }
      .toast-error { border-left-color: var(--danger); }
      .toast-info { border-left-color: var(--info); }

      .body { flex: 1 1 auto; min-width: 0; }

      .message {
        margin: 0;
        font-size: 0.85rem;
      }

      .detail {
        margin: var(--sp-1) 0 0;
        font-size: 0.72rem;
        word-break: break-all;
      }
    `,
  ],
})
export class ToastsComponent {
  readonly notifications = inject(NotificationService);
}
