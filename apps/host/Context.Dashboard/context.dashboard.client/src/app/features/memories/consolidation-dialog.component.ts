import { CommonModule } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, DestroyRef, Injector, afterNextRender, computed, effect, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxChange, MatCheckboxModule } from '@angular/material/checkbox';
import { MatDialogModule } from '@angular/material/dialog';
import { MatExpansionModule } from '@angular/material/expansion';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { ApiService } from '../../core/api.service';
import {
  ConsolidationReportStatus,
  ReconsolidationGroup,
  ReconsolidationReportSummaryDto,
  ReconsolidationStats,
} from '../../core/models';
import { SignalrService } from '../../core/signalr.service';
import { ProjectContextService } from '../projects/project-context.service';

const FINISHED_STATUSES: readonly ConsolidationReportStatus[] = ['Applied', 'Discarded', 'Failed'];

function isFinished(status: ConsolidationReportStatus): boolean {
  return FINISHED_STATUSES.includes(status);
}

function deleteFailureMessage(httpStatus: number): string {
  switch (httpStatus) {
    case 404:
      return 'The report no longer exists.';
    case 409:
      return 'The report is being applied and cannot be deleted yet.';
    default:
      return 'Failed to delete the report.';
  }
}

/**
 * Batch memory reconsolidation: queue a dry run, review its report group by group, then apply it
 * with some groups excluded or discard it. Runs are background jobs, so after queueing one the
 * reports list refetches on every job-count push until it changes. A report's details expand
 * inline under its row, one at a time. Opened from the Memories page with that page's injector,
 * so it shares the page's ProjectContextService.
 */
@Component({
  selector: 'app-consolidation-dialog',
  imports: [
    CommonModule,
    MatButtonModule,
    MatCheckboxModule,
    MatDialogModule,
    MatExpansionModule,
    MatIconModule,
    MatProgressBarModule,
    MatProgressSpinnerModule,
    MatTooltipModule,
  ],
  templateUrl: './consolidation-dialog.component.html',
  styleUrl: './consolidation-dialog.component.scss',
})
export class ConsolidationDialogComponent {
  private readonly api = inject(ApiService);
  private readonly snackBar = inject(MatSnackBar);
  private readonly signalr = inject(SignalrService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly ctx = inject(ProjectContextService);
  private readonly injector = inject(Injector);

  private readonly reportsResource = this.api.reconsolidationReportsResource(this.ctx.projectId);
  protected readonly reports = computed(() => (this.reportsResource.hasValue() ? this.reportsResource.value() : []));
  protected readonly reportsLoading = this.reportsResource.isLoading;

  protected readonly selectedReportId = signal<string | null>(null);
  private readonly reportResource = this.api.reconsolidationReportResource(this.selectedReportId);
  // A report that is not the selected one is never shown: it would flash under the wrong row.
  protected readonly selected = computed(() => {
    const dto = this.reportResource.hasValue() ? this.reportResource.value() ?? null : null;
    return dto?.id === this.selectedReportId() ? dto : null;
  });
  protected readonly selectedLoading = this.reportResource.isLoading;
  // Reloads keep the list and report on screen; a first load replaces them with a full bar instead.
  protected readonly refreshing = computed(
    () =>
      (this.reportsLoading() && this.reports().length > 0) ||
      (this.selectedLoading() && this.selected() !== null),
  );

  protected readonly excluded = signal<ReadonlySet<number>>(new Set());
  protected readonly busyAction = signal<'dryRun' | 'apply' | 'discard' | 'delete' | 'clear' | null>(null);
  protected readonly busy = computed(() => this.busyAction() !== null);
  protected readonly deletingId = signal<string | null>(null);
  protected readonly finishedCount = computed(() => this.reports().filter((r) => isFinished(r.status)).length);

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
    this.busyAction.set('dryRun');
    this.api.triggerReconsolidation(this.ctx.projectId(), true).subscribe({
      next: () => {
        this.busyAction.set(null);
        this.awaitedSignature.set(this.signature());
        this.snackBar.open('Dry run queued. Its report appears here when the job finishes.', 'Dismiss', { duration: 4000 });
      },
      error: () => {
        this.busyAction.set(null);
        this.snackBar.open('Failed to queue the dry run.', 'Dismiss');
      },
    });
  }

  protected refresh(): void {
    this.reportsResource.reload();
  }

  protected select(reportId: string, row: HTMLElement): void {
    const opening = this.selectedReportId() !== reportId;
    this.setSelected(opening ? reportId : null);
    if (opening) {
      // Collapsing the previously open report moves this row, so bring it back to the top once rendered.
      afterNextRender(() => row.scrollIntoView({ block: 'start', behavior: 'smooth' }), { injector: this.injector });
    }
  }

  private setSelected(reportId: string | null): void {
    this.excluded.set(new Set());
    this.selectedReportId.set(reportId);
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

    this.busyAction.set('apply');
    this.api.applyReconsolidation(reportId, [...this.excluded()]).subscribe({
      next: () => {
        this.busyAction.set(null);
        this.awaitedSignature.set(this.signature());
        this.snackBar.open('Apply queued.', 'Dismiss', { duration: 3000 });
      },
      error: () => {
        this.busyAction.set(null);
        this.snackBar.open('Failed to apply the report — it may no longer be pending.', 'Dismiss');
      },
    });
  }

  protected discard(): void {
    const reportId = this.selectedReportId();
    if (!reportId || !confirm('Discard this consolidation report?')) {
      return;
    }

    this.busyAction.set('discard');
    this.api.discardReconsolidation(reportId).subscribe({
      next: () => {
        this.busyAction.set(null);
        this.reportsResource.reload();
        this.reportResource.reload();
      },
      error: () => {
        this.busyAction.set(null);
        this.snackBar.open('Failed to discard the report — it may no longer be pending.', 'Dismiss');
      },
    });
  }

  protected deleteReport(report: ReconsolidationReportSummaryDto): void {
    if (!confirm(`Delete this ${report.status.toLowerCase()} report permanently?`)) {
      return;
    }

    this.busyAction.set('delete');
    this.deletingId.set(report.id);
    this.api.deleteReconsolidationReport(report.id).subscribe({
      next: () => {
        this.busyAction.set(null);
        this.deletingId.set(null);
        if (this.selectedReportId() === report.id) {
          this.setSelected(null);
        }
        this.reportsResource.reload();
        this.snackBar.open('Report deleted.', 'Dismiss', { duration: 3000 });
      },
      error: (err: HttpErrorResponse) => {
        this.busyAction.set(null);
        this.deletingId.set(null);
        if (err.status === 404) {
          this.reportsResource.reload();
        }
        this.snackBar.open(deleteFailureMessage(err.status), 'Dismiss');
      },
    });
  }

  protected clearFinished(): void {
    const count = this.finishedCount();
    if (!confirm(`Delete ${count} applied, discarded and failed ${count === 1 ? 'report' : 'reports'}? Pending reports are kept.`)) {
      return;
    }

    this.busyAction.set('clear');
    this.api.cleanupReconsolidationReports().subscribe({
      next: (result) => {
        this.busyAction.set(null);
        const open = this.reports().find((r) => r.id === this.selectedReportId());
        if (open && isFinished(open.status)) {
          this.setSelected(null);
        }
        this.reportsResource.reload();
        this.snackBar.open(`Removed ${result.removed} ${result.removed === 1 ? 'report' : 'reports'}.`, 'Dismiss', { duration: 3000 });
      },
      error: () => {
        this.busyAction.set(null);
        this.snackBar.open('Failed to clear the finished reports.', 'Dismiss');
      },
    });
  }

  protected statsLine(stats: ReconsolidationStats | null): string {
    if (!stats) {
      return '';
    }
    return `${stats.groups} groups · ${stats.canonicals} canonicals from ${stats.sourcesMerged} sources · `
      + `${stats.contradictions} contradictions · ${stats.retiredChangeLogs} change logs retired`
      + (stats.usedModel ? '' : ' · heuristic only');
  }

  protected titleOf(id: string): string {
    return this.titles().get(id) ?? id;
  }
}
