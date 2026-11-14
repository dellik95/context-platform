import { Component, inject, input, output } from '@angular/core';
import { IconComponent } from '../../shared/ui/icon.component';
import { ModalShellComponent } from '../../shared/ui/modal-shell.component';
import { ToastService } from '../../shared/ui/toast.service';

export interface ProjectTokenDialogData {
  projectName: string;
  link: string;
}

/**
 * Shown once right after a project is created: its bearer token (baked into `link`) can never be
 * recovered once dismissed.
 */
@Component({
  selector: 'app-project-token-dialog',
  imports: [ModalShellComponent, IconComponent],
  template: `
    <app-modal-shell
      title="Project access link"
      [dismissible]="false"
      [closeOnBackdrop]="false"
      widthClass="max-w-xl"
    >
      <div class="space-y-3">
        <p class="text-sm text-slate-600">
          Project <strong class="font-semibold text-slate-900">{{ data().projectName }}</strong> is ready. Save or
          bookmark this one-time access link — the bearer token cannot be retrieved again after closing this window.
        </p>

        <div class="rounded-xl border border-slate-200 bg-slate-50 p-3">
          <label class="mb-1 block text-xs font-medium text-slate-500">One-time deep link</label>
          <div class="flex items-center gap-2">
            <input
              type="text"
              readonly
              [value]="data().link"
              class="min-w-0 flex-1 rounded-lg border border-slate-300 bg-white px-2.5 py-1.5 font-mono text-xs text-slate-800 select-all focus:border-indigo-500 focus:outline-none"
            />
            <button
              type="button"
              class="inline-flex shrink-0 items-center gap-1.5 rounded-lg border border-slate-300 bg-white px-3 py-1.5 text-xs font-medium text-slate-700 transition hover:bg-slate-100"
              (click)="copyLink()"
            >
              <app-icon name="copy" sizeClass="w-3.5 h-3.5" />
              Copy
            </button>
          </div>
        </div>
      </div>

      <ng-container modal-footer>
        <button
          type="button"
          class="rounded-lg bg-indigo-600 px-4 py-2 text-sm font-medium text-white shadow-xs transition hover:bg-indigo-700"
          (click)="closed.emit()"
        >
          Done
        </button>
      </ng-container>
    </app-modal-shell>
  `,
})
export class ProjectTokenDialogComponent {
  readonly data = input.required<ProjectTokenDialogData>();
  readonly closed = output<void>();

  private readonly toast = inject(ToastService);

  protected async copyLink(): Promise<void> {
    try {
      await navigator.clipboard.writeText(this.data().link);
      this.toast.success('Link copied.', 3000);
    } catch {
      this.toast.warning('Could not copy automatically — select and copy the link manually.');
    }
  }
}
