import { CommonModule } from '@angular/common';
import { Component, computed, input, signal } from '@angular/core';
import { UsageDayDto } from '../../core/models';

export type SeriesFilter = 'both' | 'spent' | 'saved';

interface DayBarItem {
  index: number;
  day: string;
  label: string;
  tokens: number;
  saved: number;
  calls: number;
  net: number;
  groupX: number;
  groupWidth: number;
  spentX: number;
  spentY: number;
  spentWidth: number;
  spentHeight: number;
  savedX: number;
  savedY: number;
  savedWidth: number;
  savedHeight: number;
}

interface ChartModel {
  width: number;
  height: number;
  plotLeft: number;
  plotTop: number;
  plotHeight: number;
  maxValue: number;
  midLabel: string;
  maxLabel: string;
  bars: DayBarItem[];
  labelStride: number;
}

const CHART_HEIGHT = 210;
const PLOT_LEFT = 38;
const PLOT_TOP = 14;
const BOTTOM_PADDING = 26;
const SLOT_GAP = 6;
const MAX_X_LABELS = 8;

function formatCompact(value: number): string {
  if (value >= 1_000_000) {
    return `${(value / 1_000_000).toFixed(1)}M`;
  }
  if (value >= 1_000) {
    return `${(value / 1_000).toFixed(value >= 10_000 ? 0 : 1)}k`;
  }
  return String(Math.round(value));
}

function formatDayLabel(day: string): string {
  return new Date(`${day}T00:00:00`).toLocaleDateString(undefined, { month: 'short', day: 'numeric' });
}

function buildActivityChart(days: readonly UsageDayDto[], series: SeriesFilter): ChartModel | null {
  if (days.length === 0) {
    return null;
  }

  const width = Math.max(640, PLOT_LEFT + days.length * 24);
  const plotWidth = width - PLOT_LEFT;
  const plotHeight = CHART_HEIGHT - PLOT_TOP - BOTTOM_PADDING;
  const maxValue = Math.max(
    ...days.map((d) => {
      if (series === 'spent') {
        return d.tokens;
      }
      if (series === 'saved') {
        return d.estimatedSavedTokens;
      }
      return Math.max(d.tokens, d.estimatedSavedTokens);
    }),
    1,
  );

  const slotWidth = (plotWidth - SLOT_GAP * (days.length - 1)) / days.length;
  const dual = series === 'both';
  const innerGap = dual ? Math.max(1.5, slotWidth * 0.1) : 0;
  const barWidth = dual ? Math.max(2, (slotWidth - innerGap) / 2) : Math.max(3, slotWidth * 0.72);

  const bars: DayBarItem[] = days.map((d, index) => {
    const groupX = PLOT_LEFT + index * (slotWidth + SLOT_GAP);
    const spentH = d.tokens > 0 ? Math.max((d.tokens / maxValue) * plotHeight, 3) : 0;
    const savedH = d.estimatedSavedTokens > 0 ? Math.max((d.estimatedSavedTokens / maxValue) * plotHeight, 3) : 0;
    const singleOffset = (slotWidth - barWidth) / 2;

    const spentX = dual ? groupX : groupX + singleOffset;
    const savedX = dual ? groupX + barWidth + innerGap : groupX + singleOffset;

    return {
      index,
      day: d.day,
      label: formatDayLabel(d.day),
      tokens: d.tokens,
      saved: d.estimatedSavedTokens,
      calls: d.calls,
      net: d.estimatedSavedTokens - d.tokens,
      groupX,
      groupWidth: slotWidth,
      spentX,
      spentY: PLOT_TOP + plotHeight - spentH,
      spentWidth: barWidth,
      spentHeight: spentH,
      savedX,
      savedY: PLOT_TOP + plotHeight - savedH,
      savedWidth: barWidth,
      savedHeight: savedH,
    };
  });

  return {
    width,
    height: CHART_HEIGHT,
    plotLeft: PLOT_LEFT,
    plotTop: PLOT_TOP,
    plotHeight,
    maxValue,
    midLabel: formatCompact(maxValue / 2),
    maxLabel: formatCompact(maxValue),
    bars,
    labelStride: Math.max(1, Math.ceil(days.length / MAX_X_LABELS)),
  };
}

@Component({
  selector: 'app-activity-bar-chart',
  standalone: true,
  imports: [CommonModule],
  template: `
    @if (chart(); as c) {
      <div class="space-y-3">
        <div class="flex flex-wrap items-center justify-between gap-2">
          <div class="inline-flex rounded-xl border border-slate-200 bg-slate-100/80 p-0.5 text-xs" role="group" aria-label="Series visibility">
            <button
              type="button"
              class="rounded-lg px-2.5 py-1 font-medium transition"
              [class]="series() === 'both' ? 'bg-white text-slate-900 shadow-2xs' : 'text-slate-600 hover:text-slate-900'"
              (click)="series.set('both')"
            >
              Both
            </button>
            <button
              type="button"
              class="inline-flex items-center gap-1.5 rounded-lg px-2.5 py-1 font-medium transition"
              [class]="series() === 'spent' ? 'bg-white text-indigo-700 shadow-2xs' : 'text-slate-600 hover:text-slate-900'"
              (click)="series.set('spent')"
            >
              <span class="h-2 w-2 rounded-full bg-indigo-500"></span>
              Spent
            </button>
            <button
              type="button"
              class="inline-flex items-center gap-1.5 rounded-lg px-2.5 py-1 font-medium transition"
              [class]="series() === 'saved' ? 'bg-white text-emerald-700 shadow-2xs' : 'text-slate-600 hover:text-slate-900'"
              (click)="series.set('saved')"
            >
              <span class="h-2 w-2 rounded-full bg-emerald-500"></span>
              Saved est.
            </button>
          </div>

          @if (focusedBar(); as bar) {
            <div class="flex flex-wrap items-center gap-x-3 gap-y-1 rounded-xl border border-slate-200 bg-slate-50/90 px-3 py-1.5 text-xs tabular-nums">
              <span class="font-semibold text-slate-800">{{ bar.label }}</span>
              <span class="text-indigo-700">Spent {{ bar.tokens | number }}</span>
              <span class="text-emerald-700">Saved {{ bar.saved | number }}</span>
              <span class="text-slate-500">{{ bar.calls | number }} calls</span>
            </div>
          }
        </div>

        <div class="overflow-x-auto pb-1">
          <div [class]="days().length > 14 ? 'min-w-[540px]' : 'w-full'">
            <svg
              class="h-52 w-full overflow-visible select-none"
              [attr.viewBox]="'0 0 ' + c.width + ' ' + c.height"
              preserveAspectRatio="none"
              role="img"
              aria-label="Daily tokens spent and estimated savings bar chart"
            >
              <!-- Gridlines -->
              <line
                [attr.x1]="c.plotLeft"
                [attr.y1]="c.plotTop"
                [attr.x2]="c.width"
                [attr.y2]="c.plotTop"
                stroke="#e2e8f0"
                stroke-dasharray="4 4"
                stroke-width="1"
              />
              <line
                [attr.x1]="c.plotLeft"
                [attr.y1]="c.plotTop + c.plotHeight / 2"
                [attr.x2]="c.width"
                [attr.y2]="c.plotTop + c.plotHeight / 2"
                stroke="#e2e8f0"
                stroke-dasharray="4 4"
                stroke-width="1"
              />
              <line
                [attr.x1]="c.plotLeft"
                [attr.y1]="c.plotTop + c.plotHeight"
                [attr.x2]="c.width"
                [attr.y2]="c.plotTop + c.plotHeight"
                stroke="#cbd5e1"
                stroke-width="1"
              />

              <text [attr.x]="c.plotLeft - 6" [attr.y]="c.plotTop + 3" text-anchor="end" class="fill-slate-400 text-[10px]">
                {{ c.maxLabel }}
              </text>
              <text [attr.x]="c.plotLeft - 6" [attr.y]="c.plotTop + c.plotHeight / 2 + 3" text-anchor="end" class="fill-slate-400 text-[10px]">
                {{ c.midLabel }}
              </text>

              @for (bar of c.bars; track bar.day) {
                <g
                  class="cursor-pointer"
                  (mouseenter)="hoveredDay.set(bar.day)"
                  (mouseleave)="hoveredDay.set(null)"
                >
                  <rect
                    [attr.x]="bar.groupX"
                    [attr.y]="c.plotTop"
                    [attr.width]="bar.groupWidth"
                    [attr.height]="c.plotHeight"
                    [attr.fill]="hoveredDay() === bar.day ? '#eef2ff' : 'transparent'"
                    rx="4"
                  />

                  @if (series() !== 'saved' && bar.spentHeight > 0) {
                    <rect
                      class="fill-indigo-500 transition-opacity duration-150"
                      rx="3"
                      [attr.x]="bar.spentX"
                      [attr.y]="bar.spentY"
                      [attr.width]="bar.spentWidth"
                      [attr.height]="bar.spentHeight"
                      [style.opacity]="hoveredDay() && hoveredDay() !== bar.day ? 0.45 : 1"
                    />
                  }

                  @if (series() !== 'spent' && bar.savedHeight > 0) {
                    <rect
                      class="fill-emerald-500 transition-opacity duration-150"
                      rx="3"
                      [attr.x]="bar.savedX"
                      [attr.y]="bar.savedY"
                      [attr.width]="bar.savedWidth"
                      [attr.height]="bar.savedHeight"
                      [style.opacity]="hoveredDay() && hoveredDay() !== bar.day ? 0.45 : 1"
                    />
                  }

                  @if (bar.index % c.labelStride === 0) {
                    <text
                      [attr.x]="bar.groupX + bar.groupWidth / 2"
                      [attr.y]="c.height - 6"
                      text-anchor="middle"
                      class="fill-slate-500 text-[10px]"
                    >
                      {{ bar.label }}
                    </text>
                  }

                  <title>
                    {{ bar.label }} — Spent: {{ bar.tokens | number }} · Saved est.: {{ bar.saved | number }} · Calls: {{ bar.calls | number }}
                  </title>
                </g>
              }
            </svg>
          </div>
        </div>
      </div>
    } @else {
      <p class="py-8 text-center text-xs text-slate-500">No daily data.</p>
    }
  `,
})
export class ActivityBarChartComponent {
  readonly days = input<readonly UsageDayDto[]>([]);

  protected readonly series = signal<SeriesFilter>('both');
  protected readonly hoveredDay = signal<string | null>(null);

  protected readonly chart = computed(() => buildActivityChart(this.days(), this.series()));

  protected readonly focusedBar = computed(() => {
    const c = this.chart();
    if (!c || c.bars.length === 0) {
      return null;
    }
    const dayKey = this.hoveredDay();
    if (dayKey) {
      return c.bars.find((b) => b.day === dayKey) ?? null;
    }
    return c.bars[c.bars.length - 1];
  });
}
