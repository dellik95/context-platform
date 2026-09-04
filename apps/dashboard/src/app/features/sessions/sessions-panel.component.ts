import { CommonModule } from '@angular/common';
import { Component, DestroyRef, computed, effect, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';
import { MatListModule } from '@angular/material/list';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSnackBar } from '@angular/material/snack-bar';
import { ApiService } from '../../core/api.service';
import { SessionDetailDto, SessionListItemDto } from '../../core/models';
import { SignalrService } from '../../core/signalr.service';
import { ProjectContextService } from '../projects/project-context.service';

/** Parsed result of SessionDetailDto.changedFilesJson — a free-form field the CLI writes, so a
 *  parse failure is expected (not exceptional) and falls back to showing the raw string. */
type ChangedFiles = { kind: 'list'; items: string[] } | { kind: 'raw'; raw: string };

interface SessionGroup {
  label: string;
  sessions: SessionListItemDto[];
}

/** Buckets sessions by calendar day (in the viewer's local time zone), newest group first —
 *  a flat list gets unreadable fast once a project has more than a handful of sessions. */
function groupSessionsByDay(sessions: SessionListItemDto[]): SessionGroup[] {
  const today = new Date();
  today.setHours(0, 0, 0, 0);
  const yesterday = new Date(today);
  yesterday.setDate(yesterday.getDate() - 1);

  const groups = new Map<string, SessionGroup>();
  for (const entry of sessions) {
    const started = new Date(entry.session.startedAt);
    const dayStart = new Date(started.getFullYear(), started.getMonth(), started.getDate());
    const key = dayStart.toISOString();

    let label: string;
    if (dayStart.getTime() === today.getTime()) {
      label = 'Today';
    } else if (dayStart.getTime() === yesterday.getTime()) {
      label = 'Yesterday';
    } else {
      label = dayStart.toLocaleDateString(undefined, { month: 'long', day: 'numeric', year: 'numeric' });
    }

    let group = groups.get(key);
    if (!group) {
      group = { label, sessions: [] };
      groups.set(key, group);
    }
    group.sessions.push(entry);
  }

  // Map preserves insertion order; sessions() is already newest-first, so the first session of
  // each new day is encountered in newest-day-first order too — no separate sort needed.
  return [...groups.values()];
}

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

  protected readonly selectedId = signal<string | null>(null);

  private readonly sessionsResource = this.api.sessionsResource(this.ctx.projectId);
  protected readonly sessions = computed(() => (this.sessionsResource.hasValue() ? this.sessionsResource.value() : []));
  protected readonly loading = this.sessionsResource.isLoading;
  // Gates the whole list being replaced by a spinner -- true only on the very first load. The
  // SignalR-triggered reload below (a new/updated session summary) keeps the existing list visible
  // while it refetches instead of blanking the panel, since sessionsResource already has a value.
  protected readonly initialLoading = computed(() => this.loading() && !this.sessionsResource.hasValue());

  private readonly detailResource = this.api.sessionDetailResource(this.selectedId);
  protected readonly detail = computed(() => (this.detailResource.hasValue() ? this.detailResource.value() : null));
  protected readonly detailLoading = this.detailResource.isLoading;
  protected readonly initialDetailLoading = computed(() => this.detailLoading() && !this.detailResource.hasValue());

  protected readonly sessionGroups = computed(() => groupSessionsByDay(this.sessions()));

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
    // Switching projects invalidates whatever session was selected for the previous one.
    // sessionsResource itself already refetches when ctx.projectId() changes.
    effect(() => {
      this.ctx.projectId();
      this.selectedId.set(null);
    });

    // A new/updated summary for this project (session end, or a periodic mid-session summary)
    // should refresh the list — and the open detail pane, if it's the session that just changed.
    this.signalr.projectActivity$.pipe(takeUntilDestroyed(this.destroyRef)).subscribe((event) => {
      if (event.kind !== 'SessionSummaryUpdated' || event.projectId !== this.ctx.projectId()) {
        return;
      }
      this.sessionsResource.reload();
      if (this.selectedId()) {
        this.detailResource.reload();
      }
    });

    effect(() => {
      if (this.sessionsResource.error()) {
        this.snackBar.open('Could not load sessions — is Context.Host running?', 'Dismiss');
      }
    });

    effect(() => {
      if (this.detailResource.error()) {
        this.snackBar.open('Could not load session detail.', 'Dismiss');
      }
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

  protected selectSession(entry: SessionListItemDto): void {
    this.selectedId.set(entry.session.id);
  }
}
