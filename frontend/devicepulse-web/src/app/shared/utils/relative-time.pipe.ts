import { Pipe, PipeTransform } from '@angular/core';

/**
 * Formats a timestamp as "3 minutes ago" / "in 2 hours".
 *
 * This is the right format for almost every timestamp in this application: what an operator
 * needs from `lastSeenAt` is "how stale is this", and working that out from an absolute
 * timestamp is arithmetic the UI should be doing instead.
 *
 * Deliberately impure. A pure pipe would be evaluated once and then show "a few seconds ago"
 * indefinitely, which is worse than wrong — it looks live while being frozen. The cost is
 * re-evaluation on each change detection pass, which for a few dozen cells of string formatting
 * is not measurable.
 */
@Pipe({
  name: 'relativeTime',
  standalone: true,
  pure: false,
})
export class RelativeTimePipe implements PipeTransform {
  private readonly formatter = new Intl.RelativeTimeFormat(undefined, { numeric: 'auto' });

  transform(value: string | Date | null | undefined): string {
    if (!value) {
      return '';
    }

    const date = typeof value === 'string' ? new Date(value) : value;

    if (Number.isNaN(date.getTime())) {
      return '';
    }

    const seconds = (date.getTime() - Date.now()) / 1000;
    const absolute = Math.abs(seconds);

    // Below the minute threshold the exact number is noise, and a counter ticking every second
    // draws the eye away from whatever actually matters on the page.
    if (absolute < 45) {
      return 'just now';
    }

    const units: [Intl.RelativeTimeFormatUnit, number][] = [
      ['year', 31_536_000],
      ['month', 2_592_000],
      ['week', 604_800],
      ['day', 86_400],
      ['hour', 3_600],
      ['minute', 60],
    ];

    for (const [unit, unitSeconds] of units) {
      if (absolute >= unitSeconds) {
        return this.formatter.format(Math.round(seconds / unitSeconds), unit);
      }
    }

    return this.formatter.format(Math.round(seconds), 'second');
  }
}

/**
 * Absolute timestamp, for the places where the exact moment matters — audit entries, setting
 * history, and the tooltip behind a relative time.
 */
@Pipe({ name: 'absoluteTime', standalone: true })
export class AbsoluteTimePipe implements PipeTransform {
  transform(value: string | Date | null | undefined, withSeconds = false): string {
    if (!value) {
      return '—';
    }

    const date = typeof value === 'string' ? new Date(value) : value;

    if (Number.isNaN(date.getTime())) {
      return '—';
    }

    return date.toLocaleString(undefined, {
      year: 'numeric',
      month: 'short',
      day: 'numeric',
      hour: '2-digit',
      minute: '2-digit',
      ...(withSeconds ? { second: '2-digit' } : {}),
    });
  }
}

/** Formats a duration in seconds the way the cooldown and timeout fields are read. */
@Pipe({ name: 'duration', standalone: true })
export class DurationPipe implements PipeTransform {
  transform(seconds: number | null | undefined): string {
    if (seconds === null || seconds === undefined) {
      return '—';
    }

    if (seconds === 0) {
      // Zero is meaningful for a cooldown: it means every matching reading raises an alert.
      return 'none';
    }

    if (seconds < 60) {
      return `${seconds}s`;
    }

    if (seconds < 3600) {
      const minutes = Math.round(seconds / 60);
      return `${minutes} min`;
    }

    if (seconds < 86_400) {
      const hours = seconds / 3600;
      return `${Number.isInteger(hours) ? hours : hours.toFixed(1)}h`;
    }

    const days = seconds / 86_400;
    return `${Number.isInteger(days) ? days : days.toFixed(1)}d`;
  }
}
