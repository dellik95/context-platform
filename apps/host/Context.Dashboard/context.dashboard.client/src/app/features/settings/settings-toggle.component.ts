import { Component, input, output } from '@angular/core';

@Component({
  selector: 'app-settings-toggle',
  standalone: true,
  template: `
    <div class="settings-toggle flex items-center gap-3">
      <button
        type="button"
        role="switch"
        [attr.aria-checked]="checked()"
        (click)="checkedChange.emit(!checked())"
        class="relative inline-flex h-5 w-9 shrink-0 cursor-pointer items-center rounded-full border-2 border-transparent transition-colors focus:ring-2 focus:ring-indigo-500/30 focus:outline-none"
        [class.bg-indigo-600]="checked()"
        [class.bg-slate-300]="!checked()"
      >
        <span
          class="pointer-events-none inline-block h-4 w-4 rounded-full bg-white shadow-xs transition-transform"
          [class.translate-x-4]="checked()"
          [class.translate-x-0]="!checked()"
        ></span>
      </button>
      <span
        class="cursor-pointer text-sm text-slate-700 select-none"
        (click)="checkedChange.emit(!checked())"
      >
        <ng-content />
      </span>
    </div>
  `,
})
export class SettingsToggleComponent {
  readonly checked = input(false);
  readonly checkedChange = output<boolean>();
}
