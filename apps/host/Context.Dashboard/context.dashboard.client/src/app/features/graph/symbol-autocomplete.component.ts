import { Component, DestroyRef, ElementRef, HostListener, effect, inject, input, output, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Subject, debounceTime, distinctUntilChanged } from 'rxjs';
import { SymbolSummaryDto } from '../../core/models';
import { BadgeComponent } from '../../shared/ui/badge.component';
import { IconComponent } from '../../shared/ui/icon.component';
import { SpinnerComponent } from '../../shared/ui/spinner.component';

@Component({
  selector: 'app-symbol-autocomplete',
  standalone: true,
  imports: [BadgeComponent, IconComponent, SpinnerComponent],
  template: `
    <div class="relative">
      <label class="block">
        <span class="mb-1 block text-xs font-medium text-slate-700">Search for a symbol</span>
        <div class="relative">
          <span class="pointer-events-none absolute inset-y-0 left-3 flex items-center text-slate-400">
            <app-icon name="search" sizeClass="w-4 h-4" />
          </span>
          <input
            type="text"
            role="combobox"
            aria-autocomplete="list"
            [attr.aria-expanded]="dropdownOpen()"
            [value]="inputValue()"
            [placeholder]="placeholder()"
            class="w-full rounded-lg border border-slate-300 bg-white py-2 pr-9 pl-9 text-sm text-slate-900 placeholder:text-slate-400 focus:border-indigo-500 focus:ring-2 focus:ring-indigo-500/20 focus:outline-none"
            (input)="onInput($event)"
            (focus)="open.set(true)"
            (keydown)="onKeyDown($event)"
          />
          @if (searching()) {
            <span class="pointer-events-none absolute inset-y-0 right-3 flex items-center">
              <app-spinner sizeClass="w-4 h-4 text-indigo-600" />
            </span>
          }
        </div>
      </label>

      @if (dropdownOpen() && matches().length > 0) {
        <ul
          role="listbox"
          class="absolute z-30 mt-1 max-h-72 w-full overflow-y-auto rounded-xl border border-slate-200 bg-white py-1 shadow-lg"
        >
          @for (match of matches(); track match.id; let idx = $index) {
            <li role="option" [attr.aria-selected]="activeIndex() === idx">
              <button
                type="button"
                class="flex w-full items-center gap-2.5 px-3 py-2 text-left text-sm transition-colors"
                [class.bg-indigo-50]="activeIndex() === idx"
                [class.hover:bg-slate-50]="activeIndex() !== idx"
                (mousedown)="$event.preventDefault(); select(match)"
              >
                <span class="shrink-0 font-medium text-slate-900">{{ match.name }}</span>
                <app-badge tone="primary">{{ match.kind }}</app-badge>
                <span class="min-w-0 flex-1 truncate font-mono text-xs text-slate-500" [title]="match.qualifiedName">
                  {{ match.qualifiedName }}
                </span>
              </button>
            </li>
          }
        </ul>
      }
    </div>
  `,
})
export class SymbolAutocompleteComponent {
  private readonly host = inject(ElementRef<HTMLElement>);
  private readonly destroyRef = inject(DestroyRef);
  private readonly inputTerms$ = new Subject<string>();

  readonly matches = input<readonly SymbolSummaryDto[]>([]);
  readonly searching = input(false);
  readonly selectedLabel = input('');
  readonly placeholder = input('e.g. ProjectContextService');

  readonly queryChanged = output<string>();
  readonly symbolSelected = output<SymbolSummaryDto>();

  protected readonly inputValue = signal('');
  protected readonly open = signal(false);
  protected readonly activeIndex = signal(-1);

  constructor() {
    this.inputTerms$
      .pipe(debounceTime(300), distinctUntilChanged(), takeUntilDestroyed(this.destroyRef))
      .subscribe((term) => this.queryChanged.emit(term));

    effect(() => {
      const label = this.selectedLabel();
      if (label) {
        this.inputValue.set(label);
      }
    });
  }

  protected dropdownOpen(): boolean {
    return this.open() && this.inputValue().trim().length > 0;
  }

  protected onInput(event: Event): void {
    const value = (event.target as HTMLInputElement).value;
    this.inputValue.set(value);
    this.activeIndex.set(-1);
    this.open.set(true);
    this.inputTerms$.next(value.trim());
  }

  protected onKeyDown(event: KeyboardEvent): void {
    const items = this.matches();
    if (!this.dropdownOpen() || items.length === 0) {
      return;
    }

    switch (event.key) {
      case 'ArrowDown':
        event.preventDefault();
        this.activeIndex.update((i) => Math.min(i + 1, items.length - 1));
        break;
      case 'ArrowUp':
        event.preventDefault();
        this.activeIndex.update((i) => Math.max(i - 1, 0));
        break;
      case 'Enter': {
        const idx = this.activeIndex() >= 0 ? this.activeIndex() : 0;
        if (items[idx]) {
          event.preventDefault();
          this.select(items[idx]);
        }
        break;
      }
      case 'Escape':
        this.open.set(false);
        break;
    }
  }

  protected select(symbol: SymbolSummaryDto): void {
    this.inputValue.set(symbol.name);
    this.open.set(false);
    this.activeIndex.set(-1);
    this.symbolSelected.emit(symbol);
  }

  @HostListener('document:click', ['$event'])
  protected onDocumentClick(event: MouseEvent): void {
    if (!this.host.nativeElement.contains(event.target as Node)) {
      this.open.set(false);
    }
  }
}
