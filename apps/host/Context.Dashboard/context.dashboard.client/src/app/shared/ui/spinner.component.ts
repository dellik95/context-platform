import { Component, input } from '@angular/core';

@Component({
  selector: 'app-spinner',
  standalone: true,
  template: `
    <svg
      [class]="sizeClass() + ' animate-spin text-current shrink-0'"
      viewBox="0 0 24 24"
      fill="none"
      aria-hidden="true"
    >
      <circle class="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" stroke-width="3" />
      <path
        class="opacity-90"
        fill="currentColor"
        d="M4 12a8 8 0 0 1 8-8V1C5.925 1 1 5.925 1 12h3Z"
      />
    </svg>
  `,
})
export class SpinnerComponent {
  readonly sizeClass = input('w-4 h-4');
}
