import { CommonModule } from '@angular/common';
import { Component, computed, effect, inject, signal } from '@angular/core';
import { MatCardModule } from '@angular/material/card';
import { MatChipsModule } from '@angular/material/chips';
import { MatIconModule } from '@angular/material/icon';
import { MatListModule } from '@angular/material/list';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSnackBar } from '@angular/material/snack-bar';
import { ApiService } from '../../core/api.service';
import { SessionDetailDto, SessionDto } from '../../core/models';
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
    MatChipsModule,
    MatIconModule,
    MatProgressBarModule,
  ],
  templateUrl: './sessions-panel.component.html',
  styleUrl: './sessions-panel.component.scss',
})
export class SessionsPanelComponent {
  private readonly api = inject(ApiService);
  private readonly snackBar = inject(MatSnackBar);
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
