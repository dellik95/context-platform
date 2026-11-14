import { Component, computed, input } from '@angular/core';

export type ProgressTone = 'primary' | 'success' | 'warning' | 'danger';

@Component({
  selector: 'app-progress-bar',
  standalone: true,
  template: `
    <div
      class="w-full overflow-hidden rounded-full bg-slate-200"
      [class.h-1.5]="height() === 'sm'"
      [class.h-2]="height() === 'md'"
      role="progressbar"
      [attr.aria-valuenow]="value() ?? null"
      aria-valuemin="0"
      aria-valuemax="100"
    >
      @if (value() === null) {
        <div class="h-full w-1/2 rounded-full animate-indeterminate" [class]="barClass()"></div>
      } @else {
        <div
          class="h-full rounded-full transition-all duration-300"
          [class]="barClass()"
          [style.width.%]="clamped()"
        ></div>
      }
    </div>
  `,
})
export class ProgressBarComponent {
  readonly value = input<number | null>(null);
  readonly height = input<'sm' | 'md'>('sm');
  readonly tone = input<ProgressTone>('primary');

  protected readonly clamped = computed(() => Math.max(0, Math.min(100, this.value() ?? 0)));

  protected readonly barClass = computed(() => {
    switch (this.tone()) {
      case 'success':
        return 'bg-emerald-600';
      case 'warning':
        return 'bg-amber-500';
      case 'danger':
        return 'bg-rose-600';
      default:
        return 'bg-indigo-600';
    }
  });
}
