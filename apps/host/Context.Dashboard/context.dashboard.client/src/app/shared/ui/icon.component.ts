import { Component, input } from '@angular/core';

export type IconName =
  | 'hub'
  | 'folder'
  | 'folder-open'
  | 'folder-plus'
  | 'search'
  | 'psychology'
  | 'history'
  | 'account-tree'
  | 'dashboard'
  | 'settings'
  | 'monitoring'
  | 'key'
  | 'refresh'
  | 'delete'
  | 'delete-forever'
  | 'check'
  | 'check-circle'
  | 'cancel'
  | 'error'
  | 'warning'
  | 'info'
  | 'close'
  | 'chevron-right'
  | 'chevron-down'
  | 'arrow-back'
  | 'arrow-upward'
  | 'play'
  | 'stop'
  | 'tune'
  | 'merge'
  | 'terminal'
  | 'code'
  | 'bolt'
  | 'copy'
  | 'open-in-new'
  | 'sparkles'
  | 'database'
  | 'link'
  | 'shield'
  | 'bookmark'
  | 'clock'
  | 'layers'
  | 'arrow-forward'
  | 'beaker';

@Component({
  selector: 'app-icon',
  standalone: true,
  template: `
    <svg
      [class]="sizeClass() + ' shrink-0'"
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      stroke-width="1.85"
      stroke-linecap="round"
      stroke-linejoin="round"
      aria-hidden="true"
    >
      @switch (name()) {
        @case ('hub') {
          <circle cx="12" cy="12" r="3" />
          <circle cx="19" cy="5" r="2" />
          <circle cx="5" cy="6" r="2" />
          <circle cx="6" cy="19" r="2" />
          <circle cx="18" cy="18" r="2" />
          <path d="M14.2 9.8 17.5 6.5M9.8 10 6.7 7.3M9.8 14l-2.5 3.4M14.2 14.2l2.4 2.4" />
        }
        @case ('folder') {
          <path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2Z" />
        }
        @case ('folder-open') {
          <path d="m6 14 1.5-2.9A2 2 0 0 1 9.24 10H20a2 2 0 0 1 1.94 2.5l-1.54 6a2 2 0 0 1-1.95 1.5H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h3.9a2 2 0 0 1 1.69.9l.81 1.2a2 2 0 0 0 1.67.9H18a2 2 0 0 1 2 2v2" />
        }
        @case ('folder-plus') {
          <path d="M12 10v6M9 13h6" />
          <path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2Z" />
        }
        @case ('search') {
          <circle cx="11" cy="11" r="7" />
          <path d="m20 20-3.5-3.5" />
        }
        @case ('psychology') {
          <path d="M12 4.5a2.5 2.5 0 0 0-4.96-.46 2.5 2.5 0 0 0-1.98 3 2.5 2.5 0 0 0-1.32 4.24 3 3 0 0 0 .34 5.58 2.5 2.5 0 0 0 2.96 3.08A2.5 2.5 0 0 0 12 19.5V4.5Z" />
          <path d="M12 4.5a2.5 2.5 0 0 1 4.96-.46 2.5 2.5 0 0 1 1.98 3 2.5 2.5 0 0 1 1.32 4.24 3 3 0 0 1-.34 5.58 2.5 2.5 0 0 1-2.96 3.08A2.5 2.5 0 0 1 12 19.5" />
        }
        @case ('history') {
          <path d="M3 12a9 9 0 1 0 3-6.7L3 8" />
          <path d="M3 3v5h5" />
          <path d="M12 7v5l3 2" />
        }
        @case ('account-tree') {
          <rect x="3" y="3" width="6" height="5" rx="1" />
          <rect x="15" y="3" width="6" height="5" rx="1" />
          <rect x="15" y="16" width="6" height="5" rx="1" />
          <path d="M6 8v10h9M12 5.5h3" />
        }
        @case ('dashboard') {
          <rect x="3" y="3" width="7" height="9" rx="1" />
          <rect x="14" y="3" width="7" height="5" rx="1" />
          <rect x="14" y="12" width="7" height="9" rx="1" />
          <rect x="3" y="16" width="7" height="5" rx="1" />
        }
        @case ('settings') {
          <circle cx="12" cy="12" r="3" />
          <path d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 1 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 1 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06a1.65 1.65 0 0 0 1.82.33H9a1.65 1.65 0 0 0 1-1.51V3a2 2 0 1 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82V9c.26.6.78 1 1.51 1H21a2 2 0 1 1 0 4h-.09c-.73 0-1.25.4-1.51 1Z" />
        }
        @case ('monitoring') {
          <path d="M3 3v18h18" />
          <path d="m7 14 4-4 4 3 5-6" />
        }
        @case ('key') {
          <circle cx="7.5" cy="15.5" r="4.5" />
          <path d="m11 12 8-8M16 7l3 3" />
        }
        @case ('refresh') {
          <path d="M21 12a9 9 0 0 0-15-6.7L3 8" />
          <path d="M3 3v5h5" />
          <path d="M3 12a9 9 0 0 0 15 6.7l3-2.7" />
          <path d="M16 16h5v5" />
        }
        @case ('delete') {
          <path d="M3 6h18M8 6V4h8v2M19 6l-1 14H6L5 6M10 11v6M14 11v6" />
        }
        @case ('delete-forever') {
          <path d="M3 6h18M8 6V4h8v2M19 6l-1 14H6L5 6M9.5 11.5l5 5M14.5 11.5l-5 5" />
        }
        @case ('check') {
          <path d="m5 12 5 5L20 7" />
        }
        @case ('check-circle') {
          <circle cx="12" cy="12" r="9" />
          <path d="m9 12 2 2 4-4" />
        }
        @case ('cancel') {
          <circle cx="12" cy="12" r="9" />
          <path d="m15 9-6 6M9 9l6 6" />
        }
        @case ('error') {
          <circle cx="12" cy="12" r="9" />
          <path d="M12 8v4M12 16h.01" />
        }
        @case ('warning') {
          <path d="M10.3 3.9 1.8 18a2 2 0 0 0 1.7 3h17a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0Z" />
          <path d="M12 9v4M12 17h.01" />
        }
        @case ('info') {
          <circle cx="12" cy="12" r="9" />
          <path d="M12 16v-4M12 8h.01" />
        }
        @case ('close') {
          <path d="M18 6 6 18M6 6l12 12" />
        }
        @case ('chevron-right') {
          <path d="m9 18 6-6-6-6" />
        }
        @case ('chevron-down') {
          <path d="m6 9 6 6 6-6" />
        }
        @case ('arrow-back') {
          <path d="M19 12H5M12 19l-7-7 7-7" />
        }
        @case ('arrow-upward') {
          <path d="M12 19V5M5 12l7-7 7 7" />
        }
        @case ('play') {
          <polygon points="6 4 20 12 6 20 6 4" />
        }
        @case ('stop') {
          <rect x="6" y="6" width="12" height="12" rx="1.5" />
        }
        @case ('tune') {
          <path d="M4 7h16M4 17h16M8 4v6M16 14v6" />
        }
        @case ('merge') {
          <circle cx="18" cy="18" r="2.5" />
          <circle cx="6" cy="6" r="2.5" />
          <circle cx="6" cy="18" r="2.5" />
          <path d="M6 8.5v7M8.5 6H12a4 4 0 0 1 4 4v5.5" />
        }
        @case ('terminal') {
          <path d="m4 17 6-6-6-6M12 19h8" />
        }
        @case ('code') {
          <path d="m8 16-4-4 4-4M16 8l4 4-4 4M13 4l-2 16" />
        }
        @case ('bolt') {
          <path d="M13 2 3 14h8l-1 8 11-12h-8l0-8Z" />
        }
        @case ('copy') {
          <rect x="9" y="9" width="11" height="11" rx="2" />
          <path d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1" />
        }
        @case ('open-in-new') {
          <path d="M15 3h6v6M10 14 21 3M18 13v6a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h6" />
        }
        @case ('sparkles') {
          <path d="m12 3-1.9 5.8a2 2 0 0 1-1.3 1.3L3 12l5.8 1.9a2 2 0 0 1 1.3 1.3L12 21l1.9-5.8a2 2 0 0 1 1.3-1.3L21 12l-5.8-1.9a2 2 0 0 1-1.3-1.3L12 3Z" />
        }
        @case ('database') {
          <ellipse cx="12" cy="5" rx="8" ry="3" />
          <path d="M4 5v14c0 1.66 3.58 3 8 3s8-1.34 8-3V5M4 12c0 1.66 3.58 3 8 3s8-1.34 8-3" />
        }
        @case ('link') {
          <path d="M10 13a5 5 0 0 0 7.54.54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71" />
          <path d="M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.71-1.71" />
        }
        @case ('shield') {
          <path d="M12 2 4 5v6.09c0 5.05 3.41 9.76 8 10.91 4.59-1.15 8-5.86 8-10.91V5l-8-3Z" />
        }
        @case ('bookmark') {
          <path d="m19 21-7-5-7 5V5a2 2 0 0 1 2-2h10a2 2 0 0 1 2 2Z" />
        }
        @case ('clock') {
          <circle cx="12" cy="12" r="9" />
          <path d="M12 7v5l3 3" />
        }
        @case ('layers') {
          <path d="m12 2 9 5-9 5-9-5 9-5Z" />
          <path d="m3 12 9 5 9-5M3 17l9 5 9-5" />
        }
        @case ('arrow-forward') {
          <path d="M5 12h14m-6-6 6 6-6 6" />
        }
        @case ('beaker') {
          <path d="M9 3h6M10 3v5.4L5.2 17.6A2 2 0 0 0 7 20.5h10a2 2 0 0 0 1.8-2.9L14 8.4V3" />
          <path d="M7.5 14.5h9" />
        }
      }
    </svg>
  `,
})
export class IconComponent {
  readonly name = input.required<IconName>();
  readonly sizeClass = input('w-4 h-4');
}
