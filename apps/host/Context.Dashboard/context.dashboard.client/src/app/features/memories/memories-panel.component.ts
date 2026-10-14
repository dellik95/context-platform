import { CommonModule } from '@angular/common';
import { Component, DestroyRef, Injector, computed, effect, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormField, form } from '@angular/forms/signals';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatCheckboxChange, MatCheckboxModule } from '@angular/material/checkbox';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTabChangeEvent, MatTabsModule } from '@angular/material/tabs';
import { MatTooltipModule } from '@angular/material/tooltip';
import { ApiService } from '../../core/api.service';
import { Memory, MemoryDetail } from '../../core/models';
import { SignalrService } from '../../core/signalr.service';
import { ProjectContextService } from '../projects/project-context.service';
import { ConsolidationDialogComponent } from './consolidation-dialog.component';

type DetailState = MemoryDetail | 'loading' | 'error';

@Component({
  selector: 'app-memories-panel',
  imports: [
    CommonModule,
    FormField,
    MatCardModule,
    MatButtonModule,
    MatIconModule,
    MatFormFieldModule,
    MatInputModule,
    MatCheckboxModule,
    MatDialogModule,
    MatProgressBarModule,
    MatProgressSpinnerModule,
    MatTabsModule,
    MatTooltipModule,
  ],
  templateUrl: './memories-panel.component.html',
  styleUrl: './memories-panel.component.scss',
})
export class MemoriesPanelComponent {
  private readonly api = inject(ApiService);
  private readonly snackBar = inject(MatSnackBar);
  private readonly signalr = inject(SignalrService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly dialog = inject(MatDialog);
  private readonly injector = inject(Injector);
  protected readonly ctx = inject(ProjectContextService);

  protected readonly searchModel = signal({ query: '', includeSuperseded: false });
  protected readonly searchForm = form(this.searchModel);

  private readonly committedQuery = signal<
    { projectId: string; query: string; includeSuperseded: boolean } | undefined
  >(undefined);
  private readonly resource = this.api.memoriesResource(this.committedQuery);
  protected readonly memories = computed(() => (this.resource.hasValue() ? this.resource.value() : []));
  protected readonly loading = this.resource.isLoading;
  // Gates the whole list being replaced by a spinner -- true only on the very first load for the
  // current query. The SignalR-triggered reload below (a memory just got extracted) keeps the
  // existing results visible while it refetches instead of blanking the panel.
  protected readonly initialLoading = computed(() => this.resource.status() === 'loading');
  protected readonly reloading = computed(() => this.resource.status() === 'reloading');
  protected readonly deletingId = signal<string | null>(null);
  protected readonly details = signal<Record<string, DetailState>>({});

  private lastSearchedProjectId = '';

  constructor() {
    // ProjectContextService.init(id) is itself fired from an effect in ProjectDetailComponent, so
    // projectId() can still be '' when this panel is first constructed — wait for a real id here
    // rather than searching immediately. The same guard also re-runs the search automatically if
    // the user jumps between two projects' Memories tabs (ProjectDetailComponent, and therefore
    // this ProjectContextService instance, is reused across that navigation).
    effect(() => {
      const id = this.ctx.projectId();
      if (id && id !== this.lastSearchedProjectId) {
        this.lastSearchedProjectId = id;
        this.commitSearch();
      }
    });

    // A memory extracted for this project (SummarizeSession -> ExtractMemory finishing, whether
    // from a session ending or a periodic mid-session summary) should show up here without the
    // user having to re-run the search themselves.
    this.signalr.projectActivity$.pipe(takeUntilDestroyed(this.destroyRef)).subscribe((event) => {
      if (event.kind === 'MemoryChanged' && event.projectId === this.ctx.projectId()) {
        this.resource.reload();
      }
    });

    effect(() => {
      if (this.resource.error()) {
        this.snackBar.open('Failed to search memories — is Context.Host running?', 'Dismiss');
      }
    });
  }

  protected openConsolidation(): void {
    this.dialog.open(ConsolidationDialogComponent, {
      injector: this.injector,
      width: '1100px',
      maxWidth: '95vw',
      maxHeight: '90vh',
    });
  }

  protected submit(): void {
    this.commitSearch();
  }

  protected setIncludeSuperseded(event: MatCheckboxChange): void {
    this.searchModel.update((current) => ({ ...current, includeSuperseded: event.checked }));
  }

  private commitSearch(): void {
    const { query, includeSuperseded } = this.searchForm().value();
    this.committedQuery.set({ projectId: this.ctx.projectId(), query, includeSuperseded });
  }

  protected onTabChange(memory: Memory, event: MatTabChangeEvent): void {
    const state = this.details()[memory.id];
    if (event.tab.textLabel !== 'Evidence' || (state && state !== 'error')) {
      return;
    }

    this.setDetail(memory.id, 'loading');
    this.api.memoryDetail(memory.id).subscribe({
      next: (detail) => this.setDetail(memory.id, detail),
      error: () => this.setDetail(memory.id, 'error'),
    });
  }

  protected detail(memory: Memory): MemoryDetail | null {
    const state = this.details()[memory.id];
    return typeof state === 'object' ? state : null;
  }

  private setDetail(id: string, state: DetailState): void {
    this.details.update((current) => ({ ...current, [id]: state }));
  }

  /** Parses Memory.factsJson (a JSON array of short bullet-point facts) — an LLM-produced field,
   *  so a parse failure is expected (not exceptional) and just hides the facts list. */
  protected facts(memory: Memory): string[] | null {
    return this.parseStringArray(memory.factsJson);
  }

  /** Memory.conceptsJson — fixed-taxonomy tags (see models.ts), same defensive-parse shape as facts(). */
  protected concepts(memory: Memory): string[] | null {
    return this.parseStringArray(memory.conceptsJson);
  }

  protected filesRead(memory: Memory): string[] | null {
    return this.parseStringArray(memory.filesReadJson);
  }

  protected filesModified(memory: Memory): string[] | null {
    return this.parseStringArray(memory.filesModifiedJson);
  }

  /** True when there's anything to show beyond the always-present Facts/Narrative tabs — controls
   *  whether the Details tab renders at all, so an ordinary memory with no concepts/files doesn't
   *  get an empty third tab. */
  protected hasDetails(memory: Memory): boolean {
    return this.concepts(memory) !== null || this.filesRead(memory) !== null || this.filesModified(memory) !== null;
  }

  private parseStringArray(json: string | null): string[] | null {
    if (!json) {
      return null;
    }
    try {
      const parsed = JSON.parse(json);
      return Array.isArray(parsed) && parsed.length > 0 ? (parsed as string[]) : null;
    } catch {
      return null;
    }
  }

  protected deleteMemory(memory: Memory): void {
    if (!confirm(`Delete this memory permanently?\n\n"${memory.title}"`)) {
      return;
    }

    this.deletingId.set(memory.id);
    this.api.deleteMemory(memory.id).subscribe({
      next: () => {
        this.resource.update((current) => (current ?? []).filter((m) => m.id !== memory.id));
        this.deletingId.set(null);
        this.snackBar.open('Memory deleted.', 'Dismiss', { duration: 3000 });
      },
      error: () => {
        this.deletingId.set(null);
        this.snackBar.open('Failed to delete memory.', 'Dismiss');
      },
    });
  }

  /** Small distinct chip styling for the handful of most common memory types; the rest fall back to a neutral chip. */
  protected chipClass(type: Memory['type']): string {
    switch (type) {
      case 'Decision':
      case 'Constraint':
      case 'Architecture':
        return 'chip--decision';
      case 'Issue':
        return 'chip--issue';
      case 'Discovery':
        return 'chip--discovery';
      default:
        return 'chip--default';
    }
  }
}
