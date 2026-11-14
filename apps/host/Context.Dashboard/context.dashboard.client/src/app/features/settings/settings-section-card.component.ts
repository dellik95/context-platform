import { Component, input, signal } from '@angular/core';
import { IconComponent } from '../../shared/ui/icon.component';

@Component({
  selector: 'app-settings-section-card',
  standalone: true,
  imports: [IconComponent],
  template: `
    <div class="overflow-hidden rounded-xl border border-slate-200 bg-white shadow-xs">
      <button
        type="button"
        class="flex w-full items-center justify-between gap-4 px-5 py-3.5 text-left transition-colors hover:bg-slate-50"
        [attr.aria-expanded]="expanded()"
        (click)="expanded.set(!expanded())"
      >
        <div class="flex min-w-0 flex-1 flex-wrap items-baseline justify-between gap-x-4 gap-y-1">
          <span class="text-sm font-semibold text-slate-900">{{ title() }}</span>
          <span class="truncate text-xs text-slate-500">{{ summary() }}</span>
        </div>
        <app-icon
          [name]="expanded() ? 'chevron-down' : 'chevron-right'"
          sizeClass="w-4 h-4 shrink-0 text-slate-400"
        />
      </button>

      <!-- Kept mounted in DOM via [class.hidden] so Signal Forms controls remain live across collapsed sections. -->
      <div [class.hidden]="!expanded()" class="space-y-4 border-t border-slate-100 px-5 py-4">
        <ng-content />
      </div>
    </div>
  `,
})
export class SettingsSectionCardComponent {
  readonly title = input.required<string>();
  readonly summary = input.required<string>();

  protected readonly expanded = signal(false);
}
