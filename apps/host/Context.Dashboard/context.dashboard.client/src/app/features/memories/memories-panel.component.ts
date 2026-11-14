import { CommonModule } from '@angular/common';
import { Component, DestroyRef, computed, effect, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormField, form } from '@angular/forms/signals';
import { ApiService } from '../../core/api.service';
import { Memory } from '../../core/models';
import { SignalrService } from '../../core/signalr.service';
import { ConfirmDialogService } from '../../shared/confirm-dialog/confirm-dialog.service';
import { EmptyStateComponent } from '../../shared/ui/empty-state.component';
import { IconComponent } from '../../shared/ui/icon.component';
import { ProgressBarComponent } from '../../shared/ui/progress-bar.component';
import { SpinnerComponent } from '../../shared/ui/spinner.component';
import { ToastService } from '../../shared/ui/toast.service';
import { ProjectContextService } from '../projects/project-context.service';
import { ConsolidationDialogComponent } from './consolidation-dialog.component';
import { MemoryCardComponent, MemoryDetailState } from './memory-card.component';

@Component({
  selector: 'app-memories-panel',
  standalone: true,
  imports: [
    CommonModule,
    FormField,
    IconComponent,
    SpinnerComponent,
    ProgressBarComponent,
    EmptyStateComponent,
    MemoryCardComponent,
    ConsolidationDialogComponent,
  ],
  templateUrl: './memories-panel.component.html',
})
export class MemoriesPanelComponent {
  private readonly api = inject(ApiService);
  private readonly toast = inject(ToastService);
  private readonly confirmDialog = inject(ConfirmDialogService);
  private readonly signalr = inject(SignalrService);
  private readonly destroyRef = inject(DestroyRef);
  protected readonly ctx = inject(ProjectContextService);

  protected readonly searchModel = signal({ query: '', includeSuperseded: false });
  protected readonly searchForm = form(this.searchModel);

  private readonly committedQuery = signal<
    { projectId: string; query: string; includeSuperseded: boolean } | undefined
  >(undefined);
  private readonly resource = this.api.memoriesResource(this.committedQuery);
  protected readonly memories = computed(() => (this.resource.hasValue() ? this.resource.value() : []));
  protected readonly loading = this.resource.isLoading;
  protected readonly initialLoading = computed(() => this.resource.status() === 'loading');
  protected readonly reloading = computed(() => this.resource.status() === 'reloading');
  protected readonly deletingId = signal<string | null>(null);
  protected readonly details = signal<Record<string, MemoryDetailState>>({});
  protected readonly consolidationOpen = signal(false);

  private lastSearchedProjectId = '';

  constructor() {
    effect(() => {
      const id = this.ctx.projectId();
      if (id && id !== this.lastSearchedProjectId) {
        this.lastSearchedProjectId = id;
        this.commitSearch();
      }
    });

    this.signalr.projectActivity$.pipe(takeUntilDestroyed(this.destroyRef)).subscribe((event) => {
      if (event.kind === 'MemoryChanged' && event.projectId === this.ctx.projectId()) {
        this.resource.reload();
      }
    });

    effect(() => {
      if (this.resource.error()) {
        this.toast.error('Failed to search memories — is Context Dashboard running?');
      }
    });
  }

  protected submit(): void {
    this.commitSearch();
  }

  protected setIncludeSuperseded(event: Event): void {
    const checked = (event.target as HTMLInputElement).checked;
    this.searchModel.update((current) => ({ ...current, includeSuperseded: checked }));
  }

  private commitSearch(): void {
    const { query, includeSuperseded } = this.searchForm().value();
    this.committedQuery.set({ projectId: this.ctx.projectId(), query, includeSuperseded });
  }

  protected loadEvidence(memory: Memory): void {
    const state = this.details()[memory.id];
    if (state && state !== 'error') {
      return;
    }

    this.setDetail(memory.id, 'loading');
    this.api.memoryDetail(memory.id).subscribe({
      next: (detail) => this.setDetail(memory.id, detail),
      error: () => this.setDetail(memory.id, 'error'),
    });
  }

  private setDetail(id: string, state: MemoryDetailState): void {
    this.details.update((current) => ({ ...current, [id]: state }));
  }

  protected async deleteMemory(memory: Memory): Promise<void> {
    const confirmed = await this.confirmDialog.open({
      title: 'Delete Memory',
      message: `Delete this memory permanently?\n\n"${memory.title}"`,
      confirmLabel: 'Delete',
      confirmTone: 'danger',
    });
    if (!confirmed) {
      return;
    }

    this.deletingId.set(memory.id);
    this.api.deleteMemory(memory.id).subscribe({
      next: () => {
        this.resource.update((current) => (current ?? []).filter((m) => m.id !== memory.id));
        this.deletingId.set(null);
        this.toast.success('Memory is being deleted.', 3000);
      },
      error: () => {
        this.deletingId.set(null);
        this.toast.error('Failed to delete memory.');
      },
    });
  }
}
