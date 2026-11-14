import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, input, signal } from '@angular/core';

export interface DonutSegment {
  label: string;
  value: number;
  color: string;
  hint?: string;
}

interface ComputedArc {
  index: number;
  label: string;
  value: number;
  percent: number;
  color: string;
  hint?: string;
  strokeDasharray: string;
  strokeDashoffset: number;
}

const RADIUS = 44;
const CIRCUMFERENCE = 2 * Math.PI * RADIUS;

@Component({
  selector: 'app-donut-chart',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [CommonModule],
  template: `
    @if (total() <= 0) {
      <div class="flex flex-col items-center justify-center gap-2 py-6 text-center">
        <svg viewBox="0 0 120 120" class="h-28 w-28" aria-hidden="true">
          <circle cx="60" cy="60" r="44" fill="none" stroke="#e2e8f0" stroke-width="12" stroke-dasharray="6 4" />
          <text x="60" y="64" text-anchor="middle" class="fill-slate-400 text-[11px] font-medium">0</text>
        </svg>
        <p class="text-xs text-slate-500">{{ emptyLabel() }}</p>
      </div>
    } @else {
      <div
        [class]="
          stacked()
            ? 'flex flex-col items-center gap-4'
            : 'flex flex-col items-center gap-4 sm:flex-row sm:items-center'
        "
      >
        <div class="relative shrink-0" [class]="compact() ? 'h-28 w-28' : 'h-36 w-36'">
          <svg viewBox="0 0 120 120" class="h-full w-full overflow-visible" role="img" [attr.aria-label]="ariaSummary()">
            <circle cx="60" cy="60" r="44" fill="none" stroke="#f1f5f9" stroke-width="13" />
            <g transform="rotate(-90 60 60)">
              @for (arc of arcs(); track arc.label) {
                <circle
                  cx="60"
                  cy="60"
                  r="44"
                  fill="none"
                  class="cursor-pointer transition-all duration-200"
                  [attr.stroke]="arc.color"
                  [attr.stroke-width]="hoveredIndex() === arc.index ? 16 : 13"
                  [attr.stroke-dasharray]="arc.strokeDasharray"
                  [attr.stroke-dashoffset]="arc.strokeDashoffset"
                  [attr.stroke-linecap]="'butt'"
                  [style.opacity]="hoveredIndex() === null || hoveredIndex() === arc.index ? 1 : 0.3"
                  (mouseenter)="hoveredIndex.set(arc.index)"
                  (mouseleave)="hoveredIndex.set(null)"
                >
                  <title>{{ arc.label }}: {{ arc.value | number }} ({{ arc.percent | number: '1.0-1' }}%)</title>
                </circle>
              }
            </g>
          </svg>

          <div class="pointer-events-none absolute inset-0 flex flex-col items-center justify-center px-3 text-center">
            @if (activeArc(); as active) {
              <span class="text-lg font-bold tracking-tight text-slate-900 tabular-nums">
                {{ active.value | number }}
              </span>
              <span class="max-w-full truncate text-[10px] font-semibold text-slate-600">
                {{ active.percent | number: '1.0-1' }}%
              </span>
            } @else {
              <span class="text-lg font-bold tracking-tight text-slate-900 tabular-nums">
                {{ centerValue() || (total() | number) }}
              </span>
              @if (centerLabel()) {
                <span class="text-[10px] font-medium tracking-wide text-slate-500 uppercase">
                  {{ centerLabel() }}
                </span>
              }
            }
          </div>
        </div>

        <ul class="w-full min-w-0 flex-1 space-y-1.5">
          @for (arc of arcs(); track arc.label) {
            <li>
              <button
                type="button"
                class="flex w-full items-center justify-between gap-2 rounded-lg px-2 py-1.5 text-left text-xs transition"
                [class]="hoveredIndex() === arc.index ? 'bg-slate-100 ring-1 ring-slate-200' : 'hover:bg-slate-50'"
                (mouseenter)="hoveredIndex.set(arc.index)"
                (mouseleave)="hoveredIndex.set(null)"
                (focus)="hoveredIndex.set(arc.index)"
                (blur)="hoveredIndex.set(null)"
                (click)="hoveredIndex.set(hoveredIndex() === arc.index ? null : arc.index)"
                [attr.aria-pressed]="hoveredIndex() === arc.index"
              >
                <span class="flex min-w-0 items-center gap-2">
                  <span class="h-2.5 w-2.5 shrink-0 rounded-full" [style.background-color]="arc.color"></span>
                  <span class="truncate font-medium text-slate-800" [title]="arc.hint || arc.label">
                    {{ arc.label }}
                  </span>
                </span>
                <span class="flex shrink-0 items-center gap-2 tabular-nums">
                  <span class="font-semibold text-slate-900">{{ arc.value | number }}</span>
                  <span class="w-10 text-right text-[11px] text-slate-500">{{ arc.percent | number: '1.0-0' }}%</span>
                </span>
              </button>
            </li>
          }
        </ul>
      </div>
    }
  `,
})
export class DonutChartComponent {
  readonly segments = input<readonly DonutSegment[]>([]);
  readonly centerValue = input('');
  readonly centerLabel = input('');
  readonly emptyLabel = input('No data recorded');
  readonly compact = input(false);
  readonly stacked = input(false);

  protected readonly hoveredIndex = signal<number | null>(null);

  protected readonly positiveSegments = computed(() => this.segments().filter((s) => s.value > 0));
  protected readonly total = computed(() => this.positiveSegments().reduce((sum, s) => sum + s.value, 0));

  protected readonly arcs = computed<readonly ComputedArc[]>(() => {
    const list = this.positiveSegments();
    const total = this.total();
    if (total <= 0 || list.length === 0) {
      return [];
    }

    const gap = list.length > 1 ? 2.2 : 0;
    let offset = 0;

    return list.map((seg, index) => {
      const sliceLen = (seg.value / total) * CIRCUMFERENCE;
      const visibleLen = Math.max(1, sliceLen - gap);
      const arc: ComputedArc = {
        index,
        label: seg.label,
        value: seg.value,
        percent: (seg.value / total) * 100,
        color: seg.color,
        hint: seg.hint,
        strokeDasharray: `${visibleLen.toFixed(2)} ${CIRCUMFERENCE.toFixed(2)}`,
        strokeDashoffset: -offset,
      };
      offset += sliceLen;
      return arc;
    });
  });

  protected readonly activeArc = computed(() => {
    const idx = this.hoveredIndex();
    if (idx === null) {
      return null;
    }
    return this.arcs().find((a) => a.index === idx) ?? null;
  });

  protected readonly ariaSummary = computed(() =>
    this.arcs()
      .map((a) => `${a.label}: ${a.value}`)
      .join(', '),
  );
}
