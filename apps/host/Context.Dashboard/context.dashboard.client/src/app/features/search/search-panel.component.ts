import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, effect, inject, signal } from '@angular/core';
import { FormField, form, min } from '@angular/forms/signals';
import { ApiService } from '../../core/api.service';
import { BadgeComponent } from '../../shared/ui/badge.component';
import { EmptyStateComponent } from '../../shared/ui/empty-state.component';
import { IconComponent } from '../../shared/ui/icon.component';
import { ProgressBarComponent } from '../../shared/ui/progress-bar.component';
import { SpinnerComponent } from '../../shared/ui/spinner.component';
import { ToastService } from '../../shared/ui/toast.service';
import { ProjectContextService } from '../projects/project-context.service';

@Component({
  selector: 'app-search-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    CommonModule,
    FormField,
    IconComponent,
    SpinnerComponent,
    ProgressBarComponent,
    BadgeComponent,
    EmptyStateComponent,
  ],
  templateUrl: './search-panel.component.html',
})
export class SearchPanelComponent {
  private readonly api = inject(ApiService);
  private readonly ctx = inject(ProjectContextService);
  private readonly toast = inject(ToastService);

  protected readonly searchModel = signal({ query: '', budget: null as number | null });
  protected readonly searchForm = form(this.searchModel, (path) => {
    min(path.budget, 1);
  });

  protected readonly exampleQueries = [
    'hybrid search',
    'JobDispatcher',
    'MemoryConsolidator',
    'MCP tools',
  ] as const;

  protected readonly advancedOpen = signal(false);

  private readonly committedQuery = signal<{ projectId: string; query: string; budget?: number } | undefined>(undefined);
  private readonly resource = this.api.searchResource(this.committedQuery);

  protected readonly loading = this.resource.isLoading;
  protected readonly hasSearched = computed(() => this.committedQuery() !== undefined);
  protected readonly result = computed(() => (this.resource.hasValue() ? this.resource.value() : null));
  protected readonly lastQuery = computed(() => this.committedQuery()?.query ?? '');

  private lastProjectId = '';

  constructor() {
    effect(() => {
      const projectId = this.ctx.projectId();
      if (projectId !== this.lastProjectId) {
        this.lastProjectId = projectId;
        this.committedQuery.set(undefined);
      }
    });

    effect(() => {
      if (this.resource.error()) {
        this.toast.error('Search failed — is Context.Dashboard.Server running?');
      }
    });
  }

  protected runExampleQuery(example: string): void {
    this.searchModel.update((current) => ({ ...current, query: example }));
    this.committedQuery.set({
      projectId: this.ctx.projectId(),
      query: example,
      budget: this.searchForm.budget().value() ?? undefined,
    });
  }

  protected submitSearch(): void {
    const { query, budget } = this.searchForm().value();
    const trimmed = query.trim();
    if (!trimmed || this.loading()) {
      return;
    }
    this.committedQuery.set({
      projectId: this.ctx.projectId(),
      query: trimmed,
      budget: budget && budget > 0 ? budget : undefined,
    });
  }
}
