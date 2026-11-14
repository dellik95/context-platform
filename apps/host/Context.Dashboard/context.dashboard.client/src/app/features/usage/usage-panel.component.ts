import { CommonModule } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { ApiService } from '../../core/api.service';
import { ActivityBarChartComponent } from '../../shared/ui/activity-bar-chart.component';
import { DonutChartComponent, DonutSegment } from '../../shared/ui/donut-chart.component';
import { EmptyStateComponent } from '../../shared/ui/empty-state.component';
import { IconComponent } from '../../shared/ui/icon.component';
import { ProgressBarComponent } from '../../shared/ui/progress-bar.component';
import { ProjectContextService } from '../projects/project-context.service';

type DayRange = 7 | 30 | 90;

const PURPOSE_COLORS = [
  '#4f46e5',
  '#0ea5e9',
  '#10b981',
  '#8b5cf6',
  '#f59e0b',
  '#ec4899',
  '#64748b',
] as const;

@Component({
  selector: 'app-usage-panel',
  imports: [
    CommonModule,
    IconComponent,
    ProgressBarComponent,
    EmptyStateComponent,
    ActivityBarChartComponent,
    DonutChartComponent,
  ],
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

  protected readonly savingsMultiple = computed(() => {
    const summary = this.summary();
    if (!summary || summary.totalTokens <= 0) {
      return null;
    }
    return summary.totalEstimatedSavedTokens / summary.totalTokens;
  });

  protected readonly purposeSegments = computed<readonly DonutSegment[]>(() => {
    const summary = this.summary();
    if (!summary) {
      return [];
    }
    return summary.byPurpose.map((p, index) => ({
      label: p.purpose,
      value: p.tokens > 0 ? p.tokens : p.estimatedSavedTokens,
      color: PURPOSE_COLORS[index % PURPOSE_COLORS.length],
      hint: `${p.calls} calls · saved ${p.estimatedSavedTokens.toLocaleString()} est.`,
    }));
  });

  protected readonly purposeTotal = computed(() =>
    this.purposeSegments().reduce((sum, seg) => sum + seg.value, 0),
  );

  protected purposeColor(index: number): string {
    return PURPOSE_COLORS[index % PURPOSE_COLORS.length];
  }

  protected purposeSharePercent(tokens: number, saved: number): number {
    const total = this.purposeTotal();
    if (total <= 0) {
      return 0;
    }
    const metric = tokens > 0 ? tokens : saved;
    return Math.min(100, Math.round((metric / total) * 100));
  }
}
