import { CommonModule } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { ApiService } from '../../core/api.service';
import { UsageDayDto } from '../../core/models';
import { EmptyStateComponent } from '../../shared/ui/empty-state.component';
import { IconComponent } from '../../shared/ui/icon.component';
import { ProgressBarComponent } from '../../shared/ui/progress-bar.component';
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

const CHART_WIDTH = 640;
const CHART_HEIGHT = 180;
const CHART_BOTTOM_PADDING = 22;
const CHART_BAR_GAP = 3;
const MAX_LABELS = 8;

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

@Component({
  selector: 'app-usage-panel',
  imports: [CommonModule, IconComponent, ProgressBarComponent, EmptyStateComponent],
  templateUrl: './usage-panel.component.html',
})
export class UsagePanelComponent {
  private readonly api = inject(ApiService);
  protected readonly ctx = inject(ProjectContextService);

  protected readonly ranges: readonly DayRange[] = [7, 30, 90];
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

  protected readonly estimatedSavedTokens = computed(() => this.summary()?.totalEstimatedSavedTokens ?? 0);
  protected readonly estimatedNetTokens = computed(() => this.summary()?.estimatedNetTokens ?? 0);
  protected readonly chart = computed(() => buildChart(this.summary()?.byDay ?? []));
}
