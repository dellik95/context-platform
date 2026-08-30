import { CommonModule } from '@angular/common';
import { Component, DestroyRef, effect, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormBuilder, ReactiveFormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSnackBar } from '@angular/material/snack-bar';
import { ApiService } from '../../core/api.service';
import { Memory } from '../../core/models';
import { SignalrService } from '../../core/signalr.service';
import { ProjectContextService } from '../projects/project-context.service';

@Component({
  selector: 'app-memories-panel',
  imports: [
    CommonModule,
    ReactiveFormsModule,
    MatCardModule,
    MatButtonModule,
    MatIconModule,
    MatFormFieldModule,
    MatInputModule,
    MatCheckboxModule,
    MatProgressBarModule,
  ],
  templateUrl: './memories-panel.component.html',
  styleUrl: './memories-panel.component.scss',
})
export class MemoriesPanelComponent {
  private readonly api = inject(ApiService);
  private readonly fb = inject(FormBuilder);
  private readonly snackBar = inject(MatSnackBar);
  private readonly signalr = inject(SignalrService);
  private readonly destroyRef = inject(DestroyRef);
  protected readonly ctx = inject(ProjectContextService);

  protected readonly memories = signal<Memory[]>([]);
  protected readonly loading = signal(true);

  protected readonly form = this.fb.nonNullable.group({
    query: [''],
    includeSuperseded: [false],
  });

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
        this.runSearch();
      }
    });

    // A memory extracted for this project (SummarizeSession -> ExtractMemory finishing, whether
    // from a session ending or a periodic mid-session summary) should show up here without the
    // user having to re-run the search themselves.
    this.signalr.projectActivity$.pipe(takeUntilDestroyed(this.destroyRef)).subscribe((event) => {
      if (event.kind === 'MemoryCreated' && event.projectId === this.ctx.projectId()) {
        this.runSearch();
      }
    });
  }

  protected submit(): void {
    this.runSearch();
  }

  private runSearch(): void {
    const { query, includeSuperseded } = this.form.getRawValue();
    this.loading.set(true);
    this.api.searchMemories(this.ctx.projectId(), query, includeSuperseded).subscribe({
      next: (memories) => {
        this.memories.set(memories);
        this.loading.set(false);
      },
      error: () => {
        this.loading.set(false);
        this.snackBar.open('Failed to search memories — is Context.Host running?', 'Dismiss');
      },
    });
  }

  /** Parses Memory.factsJson (a JSON array of short bullet-point facts) — an LLM-produced field,
   *  so a parse failure is expected (not exceptional) and just hides the facts list. */
  protected facts(memory: Memory): string[] | null {
    if (!memory.factsJson) {
      return null;
    }
    try {
      const parsed = JSON.parse(memory.factsJson);
      return Array.isArray(parsed) && parsed.length > 0 ? (parsed as string[]) : null;
    } catch {
      return null;
    }
  }

  /** Small distinct chip styling for the handful of most common memory types; the rest fall back to a neutral chip. */
  protected chipClass(type: Memory['type']): string {
    switch (type) {
      case 'Decision':
      case 'Architecture':
        return 'chip--decision';
      case 'BugFix':
      case 'Issue':
        return 'chip--bugfix';
      case 'Discovery':
        return 'chip--discovery';
      default:
        return 'chip--default';
    }
  }
}
