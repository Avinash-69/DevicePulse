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
      <div class="no-data text-2 small">
        {{ points().length === 0 ? 'No telemetry in this window yet.' : 'Not enough readings to draw a trend yet.' }}
      </div>
    } @else {
      <figure class="chart">
        <!-- Axis labels are HTML laid over the plot rather than SVG text. The SVG stretches to the
             container's width, and text inside a non-uniformly scaled SVG stretches with it. -->
        <div class="plot">
          <svg
            [attr.viewBox]="'0 0 ' + width + ' ' + height"
            preserveAspectRatio="none"
            role="img"
            [attr.aria-label]="ariaLabel()"
          >
            @for (tick of ticks(); track tick.value) {
              <line x1="0" [attr.x2]="width" [attr.y1]="tick.y" [attr.y2]="tick.y" class="grid" />
            }

            <!-- Min/max spread. -->
            <path [attr.d]="bandPath()" class="band" />

            <!-- Average. -->
            <path [attr.d]="linePath()" class="line" />
          </svg>

          <!-- Last point, marked so the current value is obvious. A positioned element rather than
               an SVG circle, which the same scaling would squash into an ellipse. -->
          <span
            class="marker"
            [style.left.%]="(lastPoint().x / width) * 100"
            [style.top.%]="(lastPoint().y / height) * 100"
          ></span>

          @for (tick of ticks(); track tick.value) {
            <span class="y-label num" [style.top.%]="(tick.y / height) * 100">{{ tick.value }}</span>
          }
        </div>

        <!-- Time axis: first and last bucket only. The range is what matters. -->
        <div class="x-axis small text-3 num">
          <span>{{ firstLabel() }}</span>
          <span>{{ lastLabel() }}</span>
        </div>

        <figcaption class="legend text-2 small">
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

      .plot {
        position: relative;
        height: 200px;
        margin-left: 36px;
      }

      svg {
        display: block;
        width: 100%;
        height: 100%;
        overflow: visible;
      }

      .grid {
        stroke: var(--line);
        stroke-width: 1;
        vector-effect: non-scaling-stroke;
      }

      .band {
        fill: var(--accent);
        opacity: 0.14;
      }

      .line {
        fill: none;
        stroke: var(--accent);
        stroke-width: 1.75;
        stroke-linejoin: round;
        stroke-linecap: round;
        /* Keeps the stroke width constant under the non-uniform viewBox scaling. */
        vector-effect: non-scaling-stroke;
      }

      .marker {
        position: absolute;
        width: 7px;
        height: 7px;
        margin: -3.5px 0 0 -3.5px;
        background: var(--accent);
        border-radius: 50%;
        box-shadow: 0 0 0 2px var(--panel);
      }

      .y-label {
        position: absolute;
        right: calc(100% + 8px);
        transform: translateY(-50%);
        font-size: var(--fs-micro);
        color: var(--text-3);
        white-space: nowrap;
      }

      .x-axis {
        display: flex;
        justify-content: space-between;
        margin: var(--sp-1) 0 0 36px;
        font-size: var(--fs-micro);
      }

      .no-data {
        display: grid;
        place-items: center;
        height: 200px;
      }

      .legend {
        display: flex;
        gap: var(--sp-4);
        align-items: center;
        margin-top: var(--sp-2);
        flex-wrap: wrap;
      }

      .key { display: inline-flex; gap: var(--sp-1); align-items: center; }

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

  /**
   * Y scale snapped to round gridline values (1, 2, 2.5 or 5 times a power of ten), so the axis
   * reads 0, 10, 20 rather than -4, 10, 24, 37.
   */
  private readonly scale = computed(() => {
    const data = this.points();
    let min = Math.min(...data.map((p) => p.minTemperature));
    let max = Math.max(...data.map((p) => p.maxTemperature));

    // A perfectly flat series would give a zero-height range and divide by zero.
    if (max - min < 1) {
      min -= 1;
      max += 1;
    }

    const step = niceStep((max - min) / 4);

    return { min: Math.floor(min / step) * step, max: Math.ceil(max / step) * step, step };
  });

  private x(index: number): number {
    const count = this.points().length;
    return count > 1 ? (this.width / (count - 1)) * index : 0;
  }

  private y(value: number): number {
    const { min, max } = this.scale();
    return this.height * (1 - (value - min) / (max - min));
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

  readonly ticks = computed(() => {
    const { min, max, step } = this.scale();
    const count = Math.round((max - min) / step);

    return Array.from({ length: count + 1 }, (_, i) => {
      const value = +(min + step * i).toFixed(6);
      return { value, y: this.y(value) };
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

/** The smallest of 1, 2, 2.5, 5 or 10 times a power of ten that is at least `raw`. */
export function niceStep(raw: number): number {
  const magnitude = 10 ** Math.floor(Math.log10(raw));
  const step = [1, 2, 2.5, 5, 10].find((m) => m * magnitude >= raw) ?? 10;

  return step * magnitude;
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
      <p class="text-2 small">No data yet.</p>
    } @else {
      <ul class="bars">
        @for (row of rows(); track row.label) {
          <li>
            <span class="name" [title]="row.label">{{ row.label }}</span>
            <span class="value num">{{ row.value }}</span>

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
          </li>
        }
      </ul>

      @if (secondaryLabel()) {
        <p class="legend text-2 small">
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
        gap: var(--sp-3);
        margin: 0;
        padding: 0;
        list-style: none;
      }

      /* Name and figure on one line, the bar beneath: full place names stay readable instead of
         being cut to fit a fixed label column. */
      li {
        display: grid;
        grid-template-columns: minmax(0, 1fr) auto;
        gap: var(--sp-1) var(--sp-2);
        align-items: baseline;
      }

      .name {
        font-size: var(--fs-sm);
        color: var(--text-2);
        overflow: hidden;
        text-overflow: ellipsis;
        white-space: nowrap;
      }

      .track {
        grid-column: 1 / -1;
        position: relative;
        height: 6px;
        background: var(--panel-3);
        border-radius: 1px;
        overflow: hidden;
      }

      .fill {
        position: absolute;
        inset: 0 auto 0 0;
        border-radius: 1px;
        transition: width 0.25s ease-out;
      }

      .secondary {
        background: var(--ok);
        opacity: 0.85;
      }

      .value {
        font-size: var(--fs-sm);
        text-align: right;
        color: var(--text);
        font-variant-numeric: tabular-nums;
      }

      .legend {
        display: flex;
        gap: var(--sp-1);
        align-items: center;
        margin: var(--sp-2) 0 0;
      }

      .swatch {
        width: 8px;
        height: 8px;
        background: var(--ok);
        border-radius: 1px;
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
      <p class="text-2 small">{{ emptyMessage() }}</p>
    } @else {
      <div class="breakdown" role="img" [attr.aria-label]="ariaLabel()">
        <p class="headline">
          <span class="total num">{{ total() }}</span>
          <span class="text-2">{{ centreLabel() }}</span>
        </p>

        <!-- One stacked bar rather than a ring. Proportions along a line can be compared at a
             glance; arc lengths around a donut cannot, and the ring was mostly decoration. -->
        <div class="bar">
          @for (slice of visible(); track slice.label) {
            <span [style.flex-grow]="slice.value" [style.background]="slice.color"></span>
          }
        </div>

        <ul class="legend">
          @for (slice of visible(); track slice.label) {
            <li>
              <i class="swatch" [style.background]="slice.color"></i>
              <span>{{ slice.label }}</span>
              <span class="num">{{ slice.value }}</span>
            </li>
          }
        </ul>
      </div>
    }
  `,
  changeDetection: ChangeDetectionStrategy.Eager,
  styles: [
    `
      .breakdown { display: grid; gap: var(--sp-3); }

      .headline {
        display: flex;
        gap: var(--sp-2);
        align-items: baseline;
        margin: 0;
      }

      .total {
        font-size: var(--fs-metric);
        font-weight: var(--fw-semibold);
        line-height: var(--lh-tight);
      }

      .bar {
        display: flex;
        gap: 2px;
        height: 8px;
      }

      .bar span { min-width: 3px; border-radius: 1px; }

      .legend {
        display: grid;
        gap: var(--sp-1);
        margin: 0;
        padding: 0;
        list-style: none;
      }

      .legend li {
        display: grid;
        grid-template-columns: 10px 1fr auto;
        gap: var(--sp-2);
        align-items: center;
        font-size: var(--fs-sm);
        color: var(--text-2);
      }

      .legend .num { color: var(--text); }

      .swatch {
        width: 8px;
        height: 8px;
        border-radius: 1px;
      }
    `,
  ],
})
export class DonutChartComponent {
  readonly slices = input.required<DonutSlice[]>();
  readonly centreLabel = input<string>('total');
  readonly emptyMessage = input<string>('Nothing to show.');

  readonly total = computed(() => this.slices().reduce((sum, s) => sum + s.value, 0));

  readonly visible = computed(() => this.slices().filter((s) => s.value > 0));

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
      <span class="text-3 small">&mdash;</span>
    } @else {
      <span class="battery" [title]="level() + '%'">
        <span class="shell">
          <span class="fill" [style.width.%]="clamped()" [style.background]="color()"></span>
        </span>
        <span class="pct num">{{ clamped() }}%</span>
      </span>
    }
  `,
  changeDetection: ChangeDetectionStrategy.Eager,
  styles: [
    `
      .battery {
        display: inline-flex;
        gap: var(--sp-2);
        align-items: center;
      }

      .shell {
        position: relative;
        width: 34px;
        height: 12px;
        background: var(--panel-3);
        border: 1px solid var(--line-strong);
        border-radius: var(--r-xs);
        overflow: hidden;
      }

      .fill {
        position: absolute;
        inset: 0 auto 0 0;
      }

      .pct {
        font-size: var(--fs-meta);
        color: var(--text-2);
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
