import { Component, inject } from '@angular/core';
import { IconComponent } from '../ui/icon.component';
import { ModalShellComponent } from '../ui/modal-shell.component';
import { ConfirmDialogService } from './confirm-dialog.service';

@Component({
  selector: 'app-confirm-dialog-host',
  standalone: true,
  imports: [ModalShellComponent, IconComponent],
  template: `
    @if (dialogService.pending(); as current) {
      <app-modal-shell
        [title]="current.data.title"
        widthClass="max-w-md"
        (closed)="dialogService.respond(false)"
      >
        <div class="flex items-start gap-3">
          @if (current.data.confirmTone === 'danger') {
            <div class="flex h-9 w-9 shrink-0 items-center justify-center rounded-full bg-rose-100 text-rose-600">
              <app-icon name="warning" sizeClass="w-5 h-5" />
            </div>
          } @else if (current.data.confirmTone === 'warning') {
            <div class="flex h-9 w-9 shrink-0 items-center justify-center rounded-full bg-amber-100 text-amber-600">
              <app-icon name="warning" sizeClass="w-5 h-5" />
            </div>
          }
          <p class="text-sm leading-relaxed text-slate-600 whitespace-pre-line">
            {{ current.data.message }}
          </p>
        </div>

        <ng-container modal-footer>
          <button
            type="button"
            class="rounded-lg border border-slate-300 bg-white px-3.5 py-2 text-sm font-medium text-slate-700 transition hover:bg-slate-50"
            (click)="dialogService.respond(false)"
          >
            {{ current.data.cancelLabel || 'Cancel' }}
          </button>
          <button
            type="button"
            [class]="confirmButtonClass(current.data.confirmTone)"
            (click)="dialogService.respond(true)"
          >
            {{ current.data.confirmLabel || 'Confirm' }}
          </button>
        </ng-container>
      </app-modal-shell>
    }
  `,
})
export class ConfirmDialogHostComponent {
  protected readonly dialogService = inject(ConfirmDialogService);

  protected confirmButtonClass(tone?: 'primary' | 'danger' | 'warning'): string {
    const base = 'rounded-lg px-3.5 py-2 text-sm font-medium text-white shadow-xs transition';
    switch (tone) {
      case 'danger':
        return `${base} bg-rose-600 hover:bg-rose-700`;
      case 'warning':
        return `${base} bg-amber-600 hover:bg-amber-700`;
      default:
        return `${base} bg-indigo-600 hover:bg-indigo-700`;
    }
  }
}
