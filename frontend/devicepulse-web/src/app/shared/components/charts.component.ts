import { Component, computed, input, ChangeDetectionStrategy } from '@angular/core';

import { TelemetryTrendPoint } from '../../core/models/api.models';

/**
 * Charts, drawn as inline SVG.
 *
 * No charting library, for the same reason the UI has no component framework: this application
 * needs a line chart with a band, a horizontal bar chart and a donut. A charting dependency
 * would be larger than all three put together, and would need its own theming layer to follow
 * the dark-mode custom properties these inherit for free.
 *
 * Everything scales with viewBox and preserveAspectRatio, so the charts are responsive without
 * measuring the DOM or listening for resizes.
 */

export interface BarDatum {
  label: string;
  value: number;
  /** Optional explicit colour; otherwise the accent colour is used. */
  color?: string;
  /** Optional secondary value rendered as a lighter segment, e.g. online within total. */
  secondary?: number;
}

/**
 * Temperature over time: a min/max band with the average as a line.
 *
 * The band is the point of this chart. An average alone hides the spread, and a device whose
 * average looks fine while its maximum is tripping an alert rule is exactly the case an
 * operator needs to see.
 */
@Component({
  selector: 'dp-trend-chart',
  standalone: true,
  imports: [],
  template: `
    @if (points().length < 2) {
      <div class="no-data muted small">
        {{ points().length === 0 ? 'No telemetry in this window yet.' : 'Not enough readings to draw a trend yet.' }}
      </div>
    } @else {
      <figure class="chart">
        <svg
          [attr.viewBox]="'0 0 ' + width + ' ' + height"
          preserveAspectRatio="none"
          role="img"
          [attr.aria-label]="ariaLabel()"
        >
          <!-- Horizontal gridlines, with the value labelled at each one. -->
          @for (line of gridLines(); track line.value) {
            <line
              [attr.x1]="padLeft"
              [attr.x2]="width - padRight"
              [attr.y1]="line.y"
              [attr.y2]="line.y"
              class="grid"
            />
            <text [attr.x]="padLeft - 6" [attr.y]="line.y + 3" class="axis-label" text-anchor="end">
              {{ line.value }}
            </text>
          }

          <!-- Min/max spread. -->
          <path [attr.d]="bandPath()" class="band" />

          <!-- Average. -->
          <path [attr.d]="linePath()" class="line" />

          <!-- Last point, marked so the current value is obvious. -->
          <circle [attr.cx]="lastPoint().x" [attr.cy]="lastPoint().y" r="3" class="marker" />

          <!-- Time axis: first and last bucket only. Labelling every bucket would be unreadable
               at this width, and the range is what matters. -->
          <text [attr.x]="padLeft" [attr.y]="height - 4" class="axis-label">{{ firstLabel() }}</text>
          <text [attr.x]="width - padRight" [attr.y]="height - 4" class="axis-label" text-anchor="end">
            {{ lastLabel() }}
          </text>
        </svg>

        <figcaption class="legend muted small">
          <span class="key"><i class="swatch line-swatch"></i>Average</span>
          <span class="key"><i class="swatch band-swatch"></i>Min&ndash;max range</span>
          <span class="spacer"></span>
          <span>{{ unit() }}</span>
        </figcaption>
      </figure>
    }
  `,
  changeDetection: ChangeDetectionStrategy.Eager,
  styles: [
    `
      .chart { margin: 0; }

      svg {
        width: 100%;
        height: 200px;
        overflow: visible;
      }

      .grid {
        stroke: var(--border);
        stroke-width: 1;
        vector-effect: non-scaling-stroke;
      }

      .band {
        fill: var(--accent);
        opacity: 0.16;
      }

      .line {
        fill: none;
        stroke: var(--accent);
        stroke-width: 2;
        stroke-linejoin: round;
        stroke-linecap: round;
        /* Keeps the stroke 2px regardless of the non-uniform viewBox scaling. */
        vector-effect: non-scaling-stroke;
      }

      .marker {
        fill: var(--accent);
        stroke: var(--surface);
        stroke-width: 2;
        vector-effect: non-scaling-stroke;
      }

      .axis-label {
        font-size: 10px;
        fill: var(--text-subtle);
      }

      .no-data {
        display: grid;
        place-items: center;
        height: 200px;
      }

      .legend {
        display: flex;
        gap: 1rem;
        align-items: center;
        margin-top: 0.5rem;
        flex-wrap: wrap;
      }

      .key { display: inline-flex; gap: 0.35rem; align-items: center; }

      .swatch {
        width: 12px;
        height: 3px;
        border-radius: 2px;
      }

      .line-swatch { background: var(--accent); }

      .band-swatch {
        height: 10px;
        background: var(--accent);
        opacity: 0.25;
      }
    `,
  ],
})
export class TrendChartComponent {
  readonly points = input.required<TelemetryTrendPoint[]>();
  readonly unit = input<string>('°C');

  protected readonly width = 600;
  protected readonly height = 200;
  protected readonly padLeft = 34;
  protected readonly padRight = 8;
  protected readonly padTop = 10;
  protected readonly padBottom = 20;

  /** Y scale, padded so the line never sits exactly on the frame. */
  private readonly scale = computed(() => {
    const data = this.points();
    const mins = data.map((p) => p.minTemperature);
    const maxes = data.map((p) => p.maxTemperature);

    let min = Math.min(...mins);
    let max = Math.max(...maxes);

    // A perfectly flat series would give a zero-height range and divide by zero.
    if (max - min < 1) {
      const mid = (max + min) / 2;
      min = mid - 1;
      max = mid + 1;
    }

    const headroom = (max - min) * 0.1;
    return { min: min - headroom, max: max + headroom };
  });

  private readonly plotWidth = computed(() => this.width - this.padLeft - this.padRight);
  private readonly plotHeight = computed(() => this.height - this.padTop - this.padBottom);

  private x(index: number): number {
    const count = this.points().length;
    const step = count > 1 ? this.plotWidth() / (count - 1) : 0;
    return this.padLeft + index * step;
  }

  private y(value: number): number {
    const { min, max } = this.scale();
    const ratio = (value - min) / (max - min);
    return this.padTop + this.plotHeight() * (1 - ratio);
  }

  readonly linePath = computed(() =>
    this.points()
      .map((p, i) => `${i === 0 ? 'M' : 'L'}${this.x(i).toFixed(1)},${this.y(p.avgTemperature).toFixed(1)}`)
      .join(' '),
  );

  /** Max edge left to right, then min edge right to left, closed — giving the filled band. */
  readonly bandPath = computed(() => {
    const data = this.points();

    const top = data.map((p, i) => `${i === 0 ? 'M' : 'L'}${this.x(i).toFixed(1)},${this.y(p.maxTemperature).toFixed(1)}`);

    const bottom = data
      .map((p, i) => ({ p, i }))
      .reverse()
      .map(({ p, i }) => `L${this.x(i).toFixed(1)},${this.y(p.minTemperature).toFixed(1)}`);

    return [...top, ...bottom, 'Z'].join(' ');
  });

  readonly gridLines = computed(() => {
    const { min, max } = this.scale();
    const steps = 4;

    return Array.from({ length: steps + 1 }, (_, i) => {
      const value = min + ((max - min) / steps) * i;
      return { value: Math.round(value), y: this.y(value) };
    });
  });

  readonly lastPoint = computed(() => {
    const data = this.points();
    const index = data.length - 1;
    return { x: this.x(index), y: this.y(data[index].avgTemperature) };
  });

  readonly firstLabel = computed(() => formatBucket(this.points()[0]?.bucketStart));
  readonly lastLabel = computed(() => formatBucket(this.points()[this.points().length - 1]?.bucketStart));

  readonly ariaLabel = computed(() => {
    const data = this.points();

    if (data.length === 0) {
      return 'Temperature trend, no data';
    }

    const latest = data[data.length - 1];

    return `Temperature trend over ${data.length} hourly buckets. Latest average ${latest.avgTemperature}${this.unit()}, range ${latest.minTemperature} to ${latest.maxTemperature}.`;
  });
}

function formatBucket(iso: string | undefined): string {
  if (!iso) {
    return '';
  }

  return new Date(iso).toLocaleString(undefined, {
    month: 'short',
    day: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
  });
}

/**
 * Horizontal bars.
 *
 * Horizontal rather than vertical because the labels are place and device-type names, which do
 * not fit under a vertical bar without rotating the text — and rotated axis labels are harder
 * to read than a plain list.
 */
@Component({
  selector: 'dp-bar-chart',
  standalone: true,
  imports: [],
  template: `
    @if (data().length === 0) {
      <p class="muted small">No data yet.</p>
    } @else {
      <ul class="bars">
        @for (row of rows(); track row.label) {
          <li>
            <span class="label" [title]="row.label">{{ row.label }}</span>

            <span class="track">
              <span
                class="fill"
                [style.width.%]="row.percent"
                [style.background]="row.color"
              ></span>
              @if (row.secondaryPercent !== null) {
                <span
                  class="fill secondary"
                  [style.width.%]="row.secondaryPercent"
                ></span>
              }
            </span>

            <span class="value mono">{{ row.value }}</span>
          </li>
        }
      </ul>

      @if (secondaryLabel()) {
        <p class="legend muted small">
          <i class="swatch"></i>{{ secondaryLabel() }}
        </p>
      }
    }
  `,
  changeDetection: ChangeDetectionStrategy.Eager,
  styles: [
    `
      .bars {
        display: grid;
        gap: 0.55rem;
        margin: 0;
        padding: 0;
        list-style: none;
      }

      li {
        display: grid;
        grid-template-columns: minmax(80px, 150px) 1fr 44px;
        gap: 0.6rem;
        align-items: center;
      }

      .label {
        font-size: 0.8rem;
        color: var(--text-muted);
        overflow: hidden;
        text-overflow: ellipsis;
        white-space: nowrap;
      }

      .track {
        position: relative;
        height: 18px;
        background: var(--surface-3);
        border-radius: var(--radius-sm);
        overflow: hidden;
      }

      .fill {
        position: absolute;
        inset: 0 auto 0 0;
        border-radius: var(--radius-sm);
        transition: width 0.25s ease-out;
      }

      .secondary {
        background: var(--ok);
        opacity: 0.85;
      }

      .value {
        font-size: 0.8rem;
        text-align: right;
        color: var(--text);
        font-variant-numeric: tabular-nums;
      }

      .legend {
        display: flex;
        gap: 0.35rem;
        align-items: center;
        margin: 0.6rem 0 0;
      }

      .swatch {
        width: 12px;
        height: 10px;
        background: var(--ok);
        border-radius: 2px;
      }
    `,
  ],
})
export class BarChartComponent {
  readonly data = input.required<BarDatum[]>();
  readonly secondaryLabel = input<string>('');

  readonly rows = computed(() => {
    const data = this.data();

    // Scaled against the largest value, not the sum: these are counts per category, and
    // scaling to the total would make every bar tiny once there are more than a few categories.
    const max = Math.max(...data.map((d) => d.value), 1);

    return data.map((d) => ({
      label: d.label,
      value: d.value,
      color: d.color ?? 'var(--accent)',
      percent: (d.value / max) * 100,
      secondaryPercent: d.secondary === undefined ? null : (d.secondary / max) * 100,
    }));
  });
}

export interface DonutSlice {
  label: string;
  value: number;
  color: string;
}

/**
 * Donut chart with a total in the middle.
 *
 * Used for one thing only — alerts by severity — where the parts genuinely do sum to a
 * meaningful whole and there are at most five of them. Both conditions matter: a donut with a
 * dozen slices, or one whose parts are not a whole, is worse than a bar chart.
 */
@Component({
  selector: 'dp-donut',
  standalone: true,
  imports: [],
  template: `
    @if (total() === 0) {
      <p class="muted small">{{ emptyMessage() }}</p>
    } @else {
      <div class="donut-wrap">
        <svg viewBox="0 0 120 120" role="img" [attr.aria-label]="ariaLabel()">
          <!-- Each slice is a stroked circle arc, offset by the slices before it. Cheaper and
               more readable than generating path arcs by hand. -->
          @for (slice of arcs(); track slice.label) {
            <circle
              cx="60"
              cy="60"
              [attr.r]="radius"
              fill="none"
              [attr.stroke]="slice.color"
              [attr.stroke-width]="thickness"
              [attr.stroke-dasharray]="slice.dash"
              [attr.stroke-dashoffset]="slice.offset"
              transform="rotate(-90 60 60)"
            />
          }

          <text x="60" y="57" class="total" text-anchor="middle">{{ total() }}</text>
          <text x="60" y="72" class="total-label" text-anchor="middle">{{ centreLabel() }}</text>
        </svg>

        <ul class="legend">
          @for (slice of slices(); track slice.label) {
            @if (slice.value > 0) {
              <li>
                <i class="swatch" [style.background]="slice.color"></i>
                <span class="label">{{ slice.label }}</span>
                <span class="mono">{{ slice.value }}</span>
              </li>
            }
          }
        </ul>
      </div>
    }
  `,
  changeDetection: ChangeDetectionStrategy.Eager,
  styles: [
    `
      .donut-wrap {
        display: flex;
        gap: 1.25rem;
        align-items: center;
        flex-wrap: wrap;
      }

      svg {
        width: 132px;
        height: 132px;
        flex: 0 0 auto;
      }

      .total {
        font-size: 22px;
        font-weight: 600;
        fill: var(--text);
      }

      .total-label {
        font-size: 9px;
        fill: var(--text-subtle);
        text-transform: uppercase;
        letter-spacing: 0.05em;
      }

      .legend {
        display: grid;
        gap: 0.35rem;
        margin: 0;
        padding: 0;
        list-style: none;
        flex: 1 1 140px;
      }

      .legend li {
        display: grid;
        grid-template-columns: 12px 1fr auto;
        gap: 0.5rem;
        align-items: center;
        font-size: 0.8rem;
      }

      .swatch {
        width: 10px;
        height: 10px;
        border-radius: 3px;
      }

      .label { color: var(--text-muted); }
    `,
  ],
})
export class DonutChartComponent {
  readonly slices = input.required<DonutSlice[]>();
  readonly centreLabel = input<string>('total');
  readonly emptyMessage = input<string>('Nothing to show.');

  protected readonly radius = 48;
  protected readonly thickness = 14;

  readonly total = computed(() => this.slices().reduce((sum, s) => sum + s.value, 0));

  readonly arcs = computed(() => {
    const circumference = 2 * Math.PI * this.radius;
    const total = this.total();
    let consumed = 0;

    return this.slices()
      .filter((s) => s.value > 0)
      .map((slice) => {
        const length = (slice.value / total) * circumference;

        const arc = {
          label: slice.label,
          color: slice.color,
          dash: `${length} ${circumference - length}`,
          // Negative offset advances the arc clockwise past the slices already drawn.
          offset: -consumed,
        };

        consumed += length;
        return arc;
      });
  });

  readonly ariaLabel = computed(() => {
    const parts = this.slices()
      .filter((s) => s.value > 0)
      .map((s) => `${s.label}: ${s.value}`);

    return `${this.centreLabel()} ${this.total()}. ${parts.join(', ')}`;
  });
}

/** Battery level as a small inline meter, used in device tables. */
@Component({
  selector: 'dp-battery',
  standalone: true,
  imports: [],
  template: `
    @if (level() === null) {
      <span class="subtle small">&mdash;</span>
    } @else {
      <span class="battery" [title]="level() + '%'">
        <span class="shell">
          <span class="fill" [style.width.%]="clamped()" [style.background]="color()"></span>
        </span>
        <span class="pct mono">{{ clamped() }}%</span>
      </span>
    }
  `,
  changeDetection: ChangeDetectionStrategy.Eager,
  styles: [
    `
      .battery {
        display: inline-flex;
        gap: 0.4rem;
        align-items: center;
      }

      .shell {
        position: relative;
        width: 34px;
        height: 12px;
        background: var(--surface-3);
        border: 1px solid var(--border-strong);
        border-radius: 3px;
        overflow: hidden;
      }

      .fill {
        position: absolute;
        inset: 0 auto 0 0;
      }

      .pct {
        font-size: 0.75rem;
        color: var(--text-muted);
        font-variant-numeric: tabular-nums;
      }
    `,
  ],
})
export class BatteryMeterComponent {
  readonly level = input.required<number | null>();

  readonly clamped = computed(() => {
    const value = this.level();
    return value === null ? 0 : Math.round(Math.min(100, Math.max(0, value)));
  });

  readonly color = computed(() => {
    const value = this.clamped();

    // Thresholds chosen to sit near the seeded low-battery rule (15%), so the meter turns red
    // at roughly the point the system starts raising alerts.
    if (value <= 15) return 'var(--danger)';
    if (value <= 35) return 'var(--warn)';
    return 'var(--ok)';
  });
}
