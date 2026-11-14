import { CommonModule } from '@angular/common';
import { Component, DestroyRef, computed, effect, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ApiService } from '../../core/api.service';
import { SessionDetailDto, SessionListItemDto } from '../../core/models';
import { SignalrService } from '../../core/signalr.service';
import { BadgeComponent } from '../../shared/ui/badge.component';
import { CollapsibleSectionComponent } from '../../shared/ui/collapsible-section.component';
import { EmptyStateComponent } from '../../shared/ui/empty-state.component';
import { IconComponent } from '../../shared/ui/icon.component';
import { ProgressBarComponent } from '../../shared/ui/progress-bar.component';
import { ToastService } from '../../shared/ui/toast.service';
import { ProjectContextService } from '../projects/project-context.service';
import { SessionSummaryFieldsComponent, parseChangedFilesJson } from './session-summary-fields.component';

interface SessionGroup {
  label: string;
  sessions: SessionListItemDto[];
}

function dedupeSessions(sessions: SessionListItemDto[]): SessionListItemDto[] {
  const seen = new Set<string>();
  const deduped: SessionListItemDto[] = [];
  for (const entry of sessions) {
    const key = entry.session.claudeSessionId ?? entry.session.id;
    if (seen.has(key)) {
      continue;
    }
    seen.add(key);
    deduped.push(entry);
  }
  return deduped;
}

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

  return [...groups.values()];
}

@Component({
  selector: 'app-sessions-panel',
  imports: [
    CommonModule,
    IconComponent,
    ProgressBarComponent,
    BadgeComponent,
    EmptyStateComponent,
    CollapsibleSectionComponent,
    SessionSummaryFieldsComponent,
  ],
  templateUrl: './sessions-panel.component.html',
})
export class SessionsPanelComponent {
  private readonly api = inject(ApiService);
  private readonly toast = inject(ToastService);
  private readonly signalr = inject(SignalrService);
  private readonly destroyRef = inject(DestroyRef);
  protected readonly ctx = inject(ProjectContextService);

  protected readonly selectedId = signal<string | null>(null);
  protected readonly historyExpanded = signal(false);

  private readonly sessionsResource = this.api.sessionsResource(this.ctx.projectId);
  protected readonly sessions = computed(() => (this.sessionsResource.hasValue() ? this.sessionsResource.value() : []));
  protected readonly initialLoading = computed(() => this.sessionsResource.status() === 'loading');

  private readonly detailResource = this.api.sessionDetailResource(this.selectedId);
  protected readonly detail = computed(() => (this.detailResource.hasValue() ? this.detailResource.value() : null));
  protected readonly detailLoading = this.detailResource.isLoading;
  protected readonly initialDetailLoading = computed(() => this.detailLoading() && !this.detailResource.hasValue());

  private readonly summaryChunksResource = this.api.sessionSummaryChunksResource(this.selectedId);
  protected readonly summaryChunks = computed(() =>
    this.summaryChunksResource.hasValue() ? this.summaryChunksResource.value() : [],
  );

  protected readonly backgroundLoading = computed(
    () =>
      this.sessionsResource.status() === 'reloading' ||
      this.detailResource.status() === 'reloading' ||
      (this.summaryChunksResource.isLoading() && !this.initialDetailLoading()),
  );

  protected readonly sessionGroups = computed(() => groupSessionsByDay(dedupeSessions(this.sessions())));

  constructor() {
    effect(() => {
      this.ctx.projectId();
      this.selectedId.set(null);
    });

    this.signalr.projectActivity$.pipe(takeUntilDestroyed(this.destroyRef)).subscribe((event) => {
      if (event.kind !== 'SessionSummaryUpdated' || event.projectId !== this.ctx.projectId()) {
        return;
      }
      this.sessionsResource.reload();
      if (this.selectedId()) {
        this.detailResource.reload();
        this.summaryChunksResource.reload();
      }
    });

    effect(() => {
      if (this.sessionsResource.error()) {
        this.toast.error('Could not load sessions — is Context.Dashboard.Server running?');
      }
    });

    effect(() => {
      if (this.detailResource.error()) {
        this.toast.error('Could not load session detail.');
      }
    });

    effect(() => {
      if (this.summaryChunksResource.error()) {
        this.toast.error('Could not load session history.');
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
        parseChangedFilesJson(detail.changedFilesJson),
    );
  }

  protected selectSession(entry: SessionListItemDto): void {
    this.selectedId.set(entry.session.id);
    this.historyExpanded.set(false);
  }
}
