import { CommonModule } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { MatButtonToggleModule, MatButtonToggleChange } from '@angular/material/button-toggle';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { ApiService } from '../../core/api.service';
import { UsageDayDto } from '../../core/models';
import { ProjectContextService } from '../projects/project-context.service';

type DayRange = 7 | 30 | 90;

interface UsageChartBar {
  day: string;
  label: string;
  tokens: number;
  calls: number;
  x: number;
  y: number;
  width: number;
  height: number;
}

interface UsageChart {
  width: number;
  height: number;
  plotHeight: number;
  maxTokens: number;
  bars: UsageChartBar[];
  labelStride: number;
}

// SVG viewBox units, not pixels -- the <svg> itself scales to its container width via CSS.
const CHART_WIDTH = 640;
const CHART_HEIGHT = 180;
const CHART_BOTTOM_PADDING = 22; // room for the day-label row under the bars
const CHART_BAR_GAP = 3;
const MAX_LABELS = 8; // thin out day labels once the range has more bars than this fits legibly

// day is an ISO date-only string (yyyy-MM-dd); parse as local midnight so the label doesn't
// shift a day depending on the viewer's timezone offset from UTC.
function formatDayLabel(day: string): string {
  return new Date(`${day}T00:00:00`).toLocaleDateString(undefined, { month: 'short', day: 'numeric' });
}

function buildChart(byDay: UsageDayDto[]): UsageChart | null {
  if (byDay.length === 0) {
    return null;
  }
  const maxTokens = Math.max(...byDay.map((d) => d.tokens), 1);
  const barWidth = (CHART_WIDTH - CHART_BAR_GAP * (byDay.length - 1)) / byDay.length;
  const plotHeight = CHART_HEIGHT - CHART_BOTTOM_PADDING;
  const bars = byDay.map((d, i) => {
    const barHeight = (d.tokens / maxTokens) * plotHeight;
    return {
      day: d.day,
      label: formatDayLabel(d.day),
      tokens: d.tokens,
      calls: d.calls,
      x: i * (barWidth + CHART_BAR_GAP),
      y: plotHeight - barHeight,
      width: Math.max(barWidth, 0.5),
      // A day with real (nonzero) tokens always renders at least a sliver, so it doesn't
      // disappear next to a much larger neighboring bar.
      height: d.tokens > 0 ? Math.max(barHeight, 1) : 0,
    };
  });
  return {
    width: CHART_WIDTH,
    height: CHART_HEIGHT,
    plotHeight,
    maxTokens,
    bars,
    labelStride: Math.max(1, Math.ceil(byDay.length / MAX_LABELS)),
  };
}

/**
 * Usage tab (BACKLOG-27 dashboard follow-up): aggregated LLM token-usage stats for the project,
 * from GET /api/usage — totals, a by-purpose breakdown table, and a hand-rolled inline-SVG bar
 * chart of the by-day time series. No charting library: package.json has none to reuse (vis-network
 * is graph-only) and one bar chart doesn't justify adding a dependency for.
 *
 * No prompt/completion split is shown even though UsageRecord has PromptTokens/CompletionTokens —
 * UsageRecorder.GetSummaryAsync (the only aggregation this endpoint exposes) sums TotalTokens only,
 * so that split isn't in UsageSummaryDto to bind to.
 */
@Component({
  selector: 'app-usage-panel',
  imports: [CommonModule, MatButtonToggleModule, MatCardModule, MatIconModule, MatProgressBarModule],
  templateUrl: './usage-panel.component.html',
  styleUrl: './usage-panel.component.scss',
})
export class UsagePanelComponent {
  private readonly api = inject(ApiService);
  protected readonly ctx = inject(ProjectContextService);

  protected readonly days = signal<DayRange>(30);

  private readonly usageResource = this.api.usageSummaryResource(this.ctx.projectId, this.days);
  protected readonly summary = computed(() => (this.usageResource.hasValue() ? this.usageResource.value() : null));
  protected readonly loading = this.usageResource.isLoading;
  protected readonly error = computed(() => Boolean(this.usageResource.error()));

  protected readonly avgTokensPerCall = computed(() => {
    const summary = this.summary();
    if (!summary || summary.totalCalls === 0) {
      return 0;
    }
    return Math.round(summary.totalTokens / summary.totalCalls);
  });

  protected readonly chart = computed(() => buildChart(this.summary()?.byDay ?? []));

  protected setDays(event: MatButtonToggleChange): void {
    this.days.set(event.value as DayRange);
  }
}
