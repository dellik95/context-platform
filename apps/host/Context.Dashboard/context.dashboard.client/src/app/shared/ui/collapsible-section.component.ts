import { Component, input, model } from '@angular/core';
import { IconComponent } from './icon.component';

@Component({
  selector: 'app-collapsible-section',
  standalone: true,
  imports: [IconComponent],
  template: `
    <div class="overflow-hidden rounded-xl border border-slate-200 bg-white">
      <button
        type="button"
        class="flex w-full items-center justify-between gap-3 px-4 py-3 text-left transition-colors hover:bg-slate-50"
        [attr.aria-expanded]="expanded()"
        (click)="expanded.set(!expanded())"
      >
        <div class="min-w-0 flex-1">
          <div class="flex flex-wrap items-center gap-2">
            <span class="text-sm font-medium text-slate-900">{{ title() }}</span>
            <ng-content select="[section-badges]" />
          </div>
          @if (subtitle()) {
            <p class="mt-0.5 truncate text-xs text-slate-500">{{ subtitle() }}</p>
          }
        </div>
        <app-icon
          [name]="expanded() ? 'chevron-down' : 'chevron-right'"
          sizeClass="w-4 h-4 text-slate-400 shrink-0"
        />
      </button>

      @if (expanded()) {
        <div class="border-t border-slate-100 px-4 py-3">
          <ng-content />
        </div>
      }
    </div>
  `,
})
export class CollapsibleSectionComponent {
  readonly title = input.required<string>();
  readonly subtitle = input<string | null>(null);
  readonly expanded = model(false);
}
