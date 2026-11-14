import { Component, computed, input } from '@angular/core';
import { IconComponent, IconName } from './icon.component';

export type AlertTone = 'info' | 'success' | 'warning' | 'danger';

const STYLES: Record<AlertTone, { container: string; icon: IconName }> = {
  info: {
    container: 'border-sky-200 bg-sky-50 text-sky-900',
    icon: 'info',
  },
  success: {
    container: 'border-emerald-200 bg-emerald-50 text-emerald-900',
    icon: 'check-circle',
  },
  warning: {
    container: 'border-amber-200 bg-amber-50 text-amber-900',
    icon: 'warning',
  },
  danger: {
    container: 'border-rose-200 bg-rose-50 text-rose-900',
    icon: 'error',
  },
};

@Component({
  selector: 'app-alert-banner',
  standalone: true,
  imports: [IconComponent],
  template: `
    <div [class]="'flex items-start gap-2.5 rounded-xl border px-4 py-3 text-sm ' + meta().container" role="alert">
      <app-icon [name]="meta().icon" sizeClass="w-4 h-4 mt-0.5 shrink-0" />
      <div class="min-w-0 flex-1">
        <ng-content />
      </div>
    </div>
  `,
})
export class AlertBannerComponent {
  readonly tone = input<AlertTone>('info');
  protected readonly meta = computed(() => STYLES[this.tone()]);
}
