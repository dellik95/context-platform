import { CommonModule } from '@angular/common';
import { Component, effect, inject, signal } from '@angular/core';
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
