import { CommonModule } from '@angular/common';
import { Component, DestroyRef, computed, effect, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxChange, MatCheckboxModule } from '@angular/material/checkbox';
import { MatExpansionModule } from '@angular/material/expansion';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { ApiService } from '../../core/api.service';
import { ReconsolidationGroup, ReconsolidationStats } from '../../core/models';
import { SignalrService } from '../../core/signalr.service';
import { ProjectContextService } from '../projects/project-context.service';

/**
 * Batch memory reconsolidation: queue a dry run, review its report group by group, then apply it
 * with some groups excluded or discard it. Runs are background jobs, so after queueing one the
 * reports list refetches on every job-count push until it changes.
 */
@Component({
  selector: 'app-consolidation-panel',
  imports: [CommonModule, MatButtonModule, MatCheckboxModule, MatExpansionModule, MatIconModule, MatProgressBarModule, MatTooltipModule],
  templateUrl: './consolidation-panel.component.html',
  styleUrl: './consolidation-panel.component.scss',
})
export class ConsolidationPanelComponent {
  private readonly api = inject(ApiService);
  private readonly snackBar = inject(MatSnackBar);
  private readonly signalr = inject(SignalrService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly ctx = inject(ProjectContextService);

  private readonly reportsResource = this.api.reconsolidationReportsResource(this.ctx.projectId);
  protected readonly reports = computed(() => (this.reportsResource.hasValue() ? this.reportsResource.value() : []));
  protected readonly reportsLoading = this.reportsResource.isLoading;

  protected readonly selectedReportId = signal<string | null>(null);
  private readonly reportResource = this.api.reconsolidationReportResource(this.selectedReportId);
  protected readonly selected = computed(() => (this.reportResource.hasValue() ? this.reportResource.value() ?? null : null));
  protected readonly selectedLoading = this.reportResource.isLoading;

  protected readonly excluded = signal<ReadonlySet<number>>(new Set());
  protected readonly busy = signal(false);

  /** Report ids and statuses when a job was queued; null when nothing is awaited. */
  private readonly awaitedSignature = signal<string | null>(null);
  private readonly signature = computed(() => this.reports().map((r) => `${r.id}:${r.status}`).join(','));

  protected readonly applicableGroups = computed(() => {
    const report = this.selected()?.report;
    return report ? report.groups.filter((g) => !g.ambiguous && !this.excluded().has(g.index)).length : 0;
  });

  protected readonly titles = computed(() => {
    const map = new Map<string, string>();
    for (const group of this.selected()?.report.groups ?? []) {
      for (const source of group.sources) {
        map.set(source.id, source.title);
      }
    }
    return map;
  });

  constructor() {
    this.signalr.jobCountsChanged$.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(() => {
      if (this.awaitedSignature() !== null) {
        this.reportsResource.reload();
        if (this.selectedReportId()) {
          this.reportResource.reload();
        }
      }
    });

    effect(() => {
      const awaited = this.awaitedSignature();
      if (awaited !== null && this.signature() !== awaited) {
        this.awaitedSignature.set(null);
      }
    });

    effect(() => {
      if (this.reportsResource.error()) {
        this.snackBar.open('Failed to load consolidation reports.', 'Dismiss');
      }
    });
  }

  protected dryRun(): void {
    this.busy.set(true);
    this.api.triggerReconsolidation(this.ctx.projectId(), true).subscribe({
      next: () => {
        this.busy.set(false);
        this.awaitedSignature.set(this.signature());
        this.snackBar.open('Dry run queued. Its report appears here when the job finishes.', 'Dismiss', { duration: 4000 });
      },
      error: () => {
        this.busy.set(false);
        this.snackBar.open('Failed to queue the dry run.', 'Dismiss');
      },
    });
  }

  protected refresh(): void {
    this.reportsResource.reload();
  }

  protected select(reportId: string): void {
    this.excluded.set(new Set());
    this.selectedReportId.set(this.selectedReportId() === reportId ? null : reportId);
  }

  protected toggleGroup(group: ReconsolidationGroup, event: MatCheckboxChange): void {
    this.excluded.update((current) => {
      const next = new Set(current);
      if (event.checked) {
        next.delete(group.index);
      } else {
        next.add(group.index);
      }
      return next;
    });
  }

  protected apply(): void {
    const reportId = this.selectedReportId();
    if (!reportId) {
      return;
    }

    this.busy.set(true);
    this.api.applyReconsolidation(reportId, [...this.excluded()]).subscribe({
      next: () => {
        this.busy.set(false);
        this.awaitedSignature.set(this.signature());
        this.snackBar.open('Apply queued.', 'Dismiss', { duration: 3000 });
      },
      error: () => {
        this.busy.set(false);
        this.snackBar.open('Failed to apply the report — it may no longer be pending.', 'Dismiss');
      },
    });
  }

  protected discard(): void {
    const reportId = this.selectedReportId();
    if (!reportId || !confirm('Discard this consolidation report?')) {
      return;
    }

    this.busy.set(true);
    this.api.discardReconsolidation(reportId).subscribe({
      next: () => {
        this.busy.set(false);
        this.reportsResource.reload();
        this.reportResource.reload();
      },
      error: () => {
        this.busy.set(false);
        this.snackBar.open('Failed to discard the report — it may no longer be pending.', 'Dismiss');
      },
    });
  }

  protected statsLine(stats: ReconsolidationStats | null): string {
    if (!stats) {
      return '';
    }
    return `${stats.groups} groups · ${stats.canonicals} canonicals from ${stats.sourcesMerged} sources · `
      + `${stats.contradictions} contradictions · ${stats.reclassifiedEvents} events reclassified`
      + (stats.usedModel ? '' : ' · heuristic only');
  }

  protected titleOf(id: string): string {
    return this.titles().get(id) ?? id;
  }
}
