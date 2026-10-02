import { Injectable, signal } from '@angular/core';

export type NoticeKind = 'success' | 'error' | 'info' | 'warning';

export interface Notice {
  id: number;
  kind: NoticeKind;
  message: string;
  /** Shown in small print — used for the traceId on a server error, so a user can quote it. */
  detail?: string;
  /** Milliseconds before auto-dismissal. Zero means it stays until dismissed. */
  timeoutMs: number;
}

/**
 * The application's toast queue.
 *
 * Errors do not auto-dismiss by default: the one piece of information a user needs to report a
 * failure is the traceId, and a message that vanishes after four seconds takes it with it.
 */
@Injectable({ providedIn: 'root' })
export class NotificationService {
  private readonly items = signal<Notice[]>([]);
  private nextId = 1;

  readonly notices = this.items.asReadonly();

  success(message: string, detail?: string): void {
    this.push('success', message, detail, 4000);
  }

  info(message: string, detail?: string): void {
    this.push('info', message, detail, 4000);
  }

  warning(message: string, detail?: string): void {
    this.push('warning', message, detail, 7000);
  }

  error(message: string, detail?: string): void {
    this.push('error', message, detail, 0);
  }

  dismiss(id: number): void {
    this.items.update((current) => current.filter((n) => n.id !== id));
  }

  dismissAll(): void {
    this.items.set([]);
  }

  private push(kind: NoticeKind, message: string, detail: string | undefined, timeoutMs: number): void {
    const notice: Notice = { id: this.nextId++, kind, message, detail, timeoutMs };

    // Capped so a failing poll cannot stack hundreds of identical toasts over the UI.
    this.items.update((current) => [...current.slice(-4), notice]);

    if (timeoutMs > 0) {
      setTimeout(() => this.dismiss(notice.id), timeoutMs);
    }
  }
}
