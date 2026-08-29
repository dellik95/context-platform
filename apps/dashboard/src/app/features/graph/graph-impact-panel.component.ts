import { CommonModule } from '@angular/common';
import { Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormControl, ReactiveFormsModule } from '@angular/forms';
import { MatAutocompleteModule, MatAutocompleteSelectedEvent } from '@angular/material/autocomplete';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatChipsModule } from '@angular/material/chips';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSnackBar } from '@angular/material/snack-bar';
import { debounceTime, distinctUntilChanged, switchMap } from 'rxjs';
import { ApiService } from '../../core/api.service';
import { ImpactSet, SymbolSummaryDto } from '../../core/models';
import { ProjectContextService } from '../projects/project-context.service';

/**
 * Search-then-inspect flow for a symbol's blast radius:
 * 1. Type-ahead over /api/symbols (debounced) rendered as a Material autocomplete.
 * 2. Selecting a match calls /api/graph/impact and renders the three ImpactSet buckets.
 */
@Component({
  selector: 'app-graph-impact-panel',
  imports: [
    CommonModule,
    ReactiveFormsModule,
    MatAutocompleteModule,
    MatButtonModule,
    MatCardModule,
    MatChipsModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatProgressBarModule,
  ],
  templateUrl: './graph-impact-panel.component.html',
  styleUrl: './graph-impact-panel.component.scss',
})
export class GraphImpactPanelComponent {
  private readonly api = inject(ApiService);
  private readonly ctx = inject(ProjectContextService);
  private readonly snackBar = inject(MatSnackBar);
  private readonly destroyRef = inject(DestroyRef);

  // Typed as string | SymbolSummaryDto: Material's autocomplete writes the selected option's
  // whole value back into this control on selection (see onSymbolSelected/displaySymbol), it's
  // not always the typed-in string.
  protected readonly searchControl = new FormControl<string | SymbolSummaryDto>('', { nonNullable: true });
  protected readonly matches = signal<SymbolSummaryDto[]>([]);
  protected readonly searching = signal(false);

  protected readonly maxHopsControl = new FormControl(3, { nonNullable: true });

  protected readonly selectedSymbol = signal<SymbolSummaryDto | null>(null);
  protected readonly impact = signal<ImpactSet | null>(null);
  protected readonly loadingImpact = signal(false);

  constructor() {
    this.searchControl.valueChanges
      .pipe(
        debounceTime(300),
        distinctUntilChanged(),
        switchMap((query) => {
          // Selecting an autocomplete option writes the whole SymbolSummaryDto back into this
          // control (see [displayWith]="displaySymbol" in the template) — that's not a new
          // search term, it's handled by onSymbolSelected, so skip re-searching for it here.
          if (typeof query !== 'string') {
            this.searching.set(false);
            return [[] as SymbolSummaryDto[]];
          }
          const projectId = this.ctx.projectId();
          const trimmed = query.trim();
          if (!projectId || trimmed.length === 0) {
            this.searching.set(false);
            return [[] as SymbolSummaryDto[]];
          }
          this.searching.set(true);
          return this.api.searchSymbols(projectId, trimmed);
        }),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe({
        next: (results) => {
          this.matches.set(results);
          this.searching.set(false);
        },
        error: () => {
          this.searching.set(false);
          this.matches.set([]);
          this.snackBar.open('Symbol search failed — is Context.Host running?', 'Dismiss');
        },
      });
  }

  protected displaySymbol(symbol: SymbolSummaryDto | string): string {
    return typeof symbol === 'string' ? symbol : symbol.name;
  }

  protected onSymbolSelected(event: MatAutocompleteSelectedEvent): void {
    const symbol = event.option.value as SymbolSummaryDto;
    this.selectedSymbol.set(symbol);
    this.loadImpact(symbol);
  }

  protected reloadImpact(): void {
    const symbol = this.selectedSymbol();
    if (symbol) {
      this.loadImpact(symbol);
    }
  }

  private loadImpact(symbol: SymbolSummaryDto): void {
    const maxHops = this.maxHopsControl.value || 3;
    this.loadingImpact.set(true);
    this.impact.set(null);
    this.api.graphImpact(symbol.id, maxHops).subscribe({
      next: (impactSet) => {
        this.impact.set(impactSet);
        this.loadingImpact.set(false);
      },
      error: () => {
        this.loadingImpact.set(false);
        this.snackBar.open('Failed to load impact graph for that symbol.', 'Dismiss');
      },
    });
  }
}
