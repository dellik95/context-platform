import { CommonModule } from '@angular/common';
import { Component, DestroyRef, computed, effect, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';
import { MatListModule } from '@angular/material/list';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSnackBar } from '@angular/material/snack-bar';
import { ApiService } from '../../core/api.service';
import { SessionDetailDto, SessionDto } from '../../core/models';
import { SignalrService } from '../../core/signalr.service';
import { ProjectContextService } from '../projects/project-context.service';

/** Parsed result of SessionDetailDto.changedFilesJson — a free-form field the CLI writes, so a
 *  parse failure is expected (not exceptional) and falls back to showing the raw string. */
type ChangedFiles = { kind: 'list'; items: string[] } | { kind: 'raw'; raw: string };

@Component({
  selector: 'app-sessions-panel',
  imports: [
    CommonModule,
    MatCardModule,
    MatListModule,
    MatIconModule,
    MatProgressBarModule,
  ],
  templateUrl: './sessions-panel.component.html',
  styleUrl: './sessions-panel.component.scss',
})
export class SessionsPanelComponent {
  private readonly api = inject(ApiService);
  private readonly snackBar = inject(MatSnackBar);
  private readonly signalr = inject(SignalrService);
  private readonly destroyRef = inject(DestroyRef);
  protected readonly ctx = inject(ProjectContextService);

  protected readonly sessions = signal<SessionDto[]>([]);
  protected readonly loading = signal(true);
  protected readonly selectedId = signal<string | null>(null);
  protected readonly detail = signal<SessionDetailDto | null>(null);
  protected readonly detailLoading = signal(false);

  protected readonly changedFiles = computed<ChangedFiles | null>(() => {
    const json = this.detail()?.changedFilesJson;
    if (!json) {
      return null;
    }
    try {
      const parsed = JSON.parse(json);
      return Array.isArray(parsed) ? { kind: 'list', items: parsed as string[] } : { kind: 'raw', raw: json };
    } catch {
      return { kind: 'raw', raw: json };
    }
  });

  constructor() {
    // Reacts to the project id becoming available (or changing) rather than reading it once —
    // this component can be constructed before ProjectDetailComponent's own effect has run.
    effect(() => {
      const projectId = this.ctx.projectId();
      if (projectId) {
        this.loadSessions(projectId);
      }
    });

    // A new/updated summary for this project (session end, or a periodic mid-session summary)
    // should refresh the list — and the open detail pane, if it's the session that just changed.
    this.signalr.projectActivity$.pipe(takeUntilDestroyed(this.destroyRef)).subscribe((event) => {
      if (event.kind !== 'SessionSummaryUpdated' || event.projectId !== this.ctx.projectId()) {
        return;
      }
      this.refreshSessionsPreservingSelection(this.ctx.projectId());
    });
  }

  private loadSessions(projectId: string): void {
    this.loading.set(true);
    this.selectedId.set(null);
    this.detail.set(null);
    this.api.listSessions(projectId).subscribe({
      next: (sessions) => {
        this.sessions.set(sessions);
        this.loading.set(false);
      },
      error: () => {
        this.sessions.set([]);
        this.loading.set(false);
        this.snackBar.open('Could not load sessions — is Context.Host running?', 'Dismiss');
      },
    });
  }

  /** Same list refresh as loadSessions, but a live update shouldn't blow away whatever the user has open. */
  private refreshSessionsPreservingSelection(projectId: string): void {
    this.api.listSessions(projectId).subscribe({
      next: (sessions) => {
        this.sessions.set(sessions);
        const selectedId = this.selectedId();
        if (selectedId) {
          this.reloadDetail(selectedId);
        }
      },
      error: () => {
        // Best-effort refresh — a failed background poll shouldn't surface a snackbar over
        // whatever the user is already looking at.
      },
    });
  }

  private reloadDetail(sessionId: string): void {
    this.api.getSession(sessionId).subscribe({
      next: (detail) => this.detail.set(detail),
      error: () => {
        // Same best-effort reasoning as refreshSessionsPreservingSelection.
      },
    });
  }

  protected hasSummaryContent(detail: SessionDetailDto): boolean {
    return Boolean(
      detail.completed ||
        detail.discovered ||
        detail.decisions ||
        detail.openIssues ||
        detail.nextSteps ||
        this.changedFiles(),
    );
  }

  protected selectSession(session: SessionDto): void {
    if (this.selectedId() === session.id) {
      return;
    }
    this.selectedId.set(session.id);
    this.detail.set(null);
    this.detailLoading.set(true);
    this.api.getSession(session.id).subscribe({
      next: (detail) => {
        this.detail.set(detail);
        this.detailLoading.set(false);
      },
      error: () => {
        this.detailLoading.set(false);
        this.snackBar.open('Could not load session detail.', 'Dismiss');
      },
    });
  }
}
