import { Component, inject } from '@angular/core';
import { IconComponent, IconName } from './icon.component';
import { ToastService, ToastTone } from './toast.service';

@Component({
  selector: 'app-toast-container',
  standalone: true,
  imports: [IconComponent],
  template: `
    <div
      class="pointer-events-none fixed right-4 bottom-4 z-[60] flex max-w-md flex-col gap-2"
      aria-live="polite"
    >
      @for (toast of toastService.items(); track toast.id) {
        <div
          class="pointer-events-auto flex items-start gap-2.5 rounded-xl border px-4 py-3 text-sm shadow-lg"
          [class]="containerClass(toast.tone)"
          role="status"
        >
          <app-icon [name]="iconFor(toast.tone)" sizeClass="w-4 h-4 mt-0.5 shrink-0" />
          <span class="min-w-0 flex-1 break-words">{{ toast.message }}</span>
          <button
            type="button"
            class="-mr-1 rounded p-0.5 opacity-70 transition hover:opacity-100"
            (click)="toastService.dismiss(toast.id)"
            aria-label="Dismiss notification"
          >
            <app-icon name="close" sizeClass="w-3.5 h-3.5" />
          </button>
        </div>
      }
    </div>
  `,
})
export class ToastContainerComponent {
  protected readonly toastService = inject(ToastService);

  protected containerClass(tone: ToastTone): string {
    switch (tone) {
      case 'success':
        return 'border-emerald-200 bg-emerald-950 text-emerald-50';
      case 'warning':
        return 'border-amber-300 bg-amber-950 text-amber-50';
      case 'error':
        return 'border-rose-300 bg-rose-950 text-rose-50';
      default:
        return 'border-slate-700 bg-slate-900 text-slate-50';
    }
  }

  protected iconFor(tone: ToastTone): IconName {
    switch (tone) {
      case 'success':
        return 'check-circle';
      case 'warning':
        return 'warning';
      case 'error':
        return 'error';
      default:
        return 'info';
    }
  }
}
