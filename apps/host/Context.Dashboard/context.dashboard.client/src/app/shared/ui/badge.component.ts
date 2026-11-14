import { Component, computed, input } from '@angular/core';

export type BadgeTone = 'neutral' | 'primary' | 'success' | 'warning' | 'danger' | 'purple' | 'sky';

const TONE_CLASSES: Record<BadgeTone, string> = {
  neutral: 'bg-slate-100 text-slate-700 ring-slate-200',
  primary: 'bg-indigo-50 text-indigo-700 ring-indigo-200',
  success: 'bg-emerald-50 text-emerald-700 ring-emerald-200',
  warning: 'bg-amber-50 text-amber-800 ring-amber-200',
  danger: 'bg-rose-50 text-rose-700 ring-rose-200',
  purple: 'bg-violet-50 text-violet-700 ring-violet-200',
  sky: 'bg-sky-50 text-sky-700 ring-sky-200',
};

@Component({
  selector: 'app-badge',
  standalone: true,
  template: `
    <span
      [class]="
        'inline-flex items-center gap-1 rounded-full px-2.5 py-0.5 text-xs font-medium ring-1 ring-inset ' +
        toneClass()
      "
    >
      <ng-content />
    </span>
  `,
})
export class BadgeComponent {
  readonly tone = input<BadgeTone>('neutral');
  protected readonly toneClass = computed(() => TONE_CLASSES[this.tone()]);
}
