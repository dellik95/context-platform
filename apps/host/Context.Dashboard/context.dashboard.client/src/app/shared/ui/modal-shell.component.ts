import { ChangeDetectionStrategy, Component, HostListener, input, output } from '@angular/core';
import { IconComponent } from './icon.component';

let nextModalTitleId = 0;

@Component({
  selector: 'app-modal-shell',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [IconComponent],
  template: `
    <div
      class="fixed inset-0 z-50 flex items-center justify-center bg-slate-950/55 p-4 backdrop-blur-[1px]"
      role="dialog"
      aria-modal="true"
      [attr.aria-labelledby]="titleId"
      (click)="onBackdropClick($event)"
    >
      <div
        [class]="
          'flex max-h-[90vh] w-full flex-col overflow-hidden rounded-2xl border border-slate-200 bg-white shadow-2xl ' +
          widthClass()
        "
        (click)="$event.stopPropagation()"
      >
        <div class="flex items-start justify-between gap-4 border-b border-slate-200 px-5 py-4">
          <div class="min-w-0">
            <h2 [id]="titleId" class="text-base font-semibold text-slate-900">{{ title() }}</h2>
            @if (subtitle()) {
              <p class="mt-0.5 truncate text-xs text-slate-500" [title]="subtitle()">{{ subtitle() }}</p>
            }
          </div>
          <div class="flex shrink-0 items-center gap-1.5">
            <ng-content select="[modal-header-actions]" />
            @if (dismissible()) {
              <button
                type="button"
                class="rounded-lg p-1.5 text-slate-400 transition-colors hover:bg-slate-100 hover:text-slate-700"
                (click)="closed.emit()"
                aria-label="Close dialog"
              >
                <app-icon name="close" sizeClass="w-4 h-4" />
              </button>
            }
          </div>
        </div>

        <div class="min-h-0 flex-1 overflow-y-auto px-5 py-4">
          <ng-content />
        </div>

        <div class="flex items-center justify-end gap-2 border-t border-slate-200 bg-slate-50/70 px-5 py-3 empty:hidden">
          <ng-content select="[modal-footer]" />
        </div>
      </div>
    </div>
  `,
})
export class ModalShellComponent {
  protected readonly titleId = `modal-title-${++nextModalTitleId}`;

  readonly title = input.required<string>();
  readonly subtitle = input<string | null>(null);
  readonly widthClass = input('max-w-lg');
  readonly dismissible = input(true);
  readonly closeOnBackdrop = input(true);

  readonly closed = output<void>();

  @HostListener('document:keydown.escape')
  protected onEscape(): void {
    if (this.dismissible()) {
      this.closed.emit();
    }
  }

  protected onBackdropClick(event: MouseEvent): void {
    if (this.dismissible() && this.closeOnBackdrop() && event.target === event.currentTarget) {
      this.closed.emit();
    }
  }
}
