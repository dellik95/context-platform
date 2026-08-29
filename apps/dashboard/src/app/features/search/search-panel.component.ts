import { CommonModule } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSnackBar } from '@angular/material/snack-bar';
import { ApiService } from '../../core/api.service';
import { ContextPack } from '../../core/models';
import { ProjectContextService } from '../projects/project-context.service';

@Component({
  selector: 'app-search-panel',
  imports: [
    CommonModule,
    FormsModule,
    MatCardModule,
    MatButtonModule,
    MatIconModule,
    MatFormFieldModule,
    MatInputModule,
    MatProgressBarModule,
  ],
  templateUrl: './search-panel.component.html',
  styleUrl: './search-panel.component.scss',
})
export class SearchPanelComponent {
  private readonly api = inject(ApiService);
  private readonly ctx = inject(ProjectContextService);
  private readonly snackBar = inject(MatSnackBar);

  // Plain (non-signal) two-way-bound form fields — simple template-driven inputs, not part of
  // reactive local state that anything else needs to react to.
  protected query = '';
  protected budget: number | null = null;

  protected readonly advancedOpen = signal(false);
  protected readonly loading = signal(false);
  protected readonly hasSearched = signal(false);
  protected readonly result = signal<ContextPack | null>(null);
  protected readonly lastQuery = signal('');

  protected submitSearch(): void {
    const query = this.query.trim();
    if (!query || this.loading()) {
      return;
    }

    this.loading.set(true);
    const budget = this.budget && this.budget > 0 ? this.budget : undefined;

    this.api.search(this.ctx.projectId(), query, budget).subscribe({
      next: (pack) => {
        this.result.set(pack);
        this.lastQuery.set(query);
        this.hasSearched.set(true);
        this.loading.set(false);
      },
      error: () => {
        this.loading.set(false);
        this.snackBar.open('Search failed — is Context.Host running?', 'Dismiss');
      },
    });
  }
}
