import { Component, input } from '@angular/core';
import { IconComponent, IconName } from './icon.component';

@Component({
  selector: 'app-empty-state',
  standalone: true,
  imports: [IconComponent],
  template: `
    <div
      class="flex flex-col items-center justify-center rounded-xl border border-dashed border-slate-300 bg-white/70 px-6 py-10 text-center"
    >
      @if (icon()) {
        <div class="mb-3 flex h-11 w-11 items-center justify-center rounded-full bg-slate-100 text-slate-500">
          <app-icon [name]="icon()!" sizeClass="w-5 h-5" />
        </div>
      }
      <p class="text-sm font-medium text-slate-800">{{ title() }}</p>
      @if (description()) {
        <p class="mt-1 max-w-md text-xs text-slate-500">{{ description() }}</p>
      }
      <div class="mt-4 empty:hidden">
        <ng-content />
      </div>
    </div>
  `,
})
export class EmptyStateComponent {
  readonly icon = input<IconName | null>(null);
  readonly title = input.required<string>();
  readonly description = input<string | null>(null);
}
