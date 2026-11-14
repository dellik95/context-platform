import { Component, computed, effect, inject, output, signal } from '@angular/core';
import { ApiService } from '../../core/api.service';
import { DirectoryEntryDto } from '../../core/models';
import { IconComponent } from '../ui/icon.component';
import { ModalShellComponent } from '../ui/modal-shell.component';
import { ProgressBarComponent } from '../ui/progress-bar.component';
import { ToastService } from '../ui/toast.service';

/**
 * Local-filesystem folder browser for "create project" — replaces having to type an absolute
 * path by hand. Backed by GET /api/fs/browse (loopback-only host).
 */
@Component({
  selector: 'app-folder-picker-dialog',
  imports: [ModalShellComponent, IconComponent, ProgressBarComponent],
  template: `
    <app-modal-shell title="Choose project folder" widthClass="max-w-xl" (closed)="cancelled.emit()">
      <div class="space-y-3">
        <div class="flex items-center gap-2 rounded-xl border border-slate-200 bg-slate-50 px-3 py-2">
          <button
            type="button"
            class="inline-flex items-center justify-center rounded-lg border border-slate-200 bg-white p-1.5 text-slate-600 transition hover:bg-slate-100 disabled:cursor-not-allowed disabled:opacity-40"
            [disabled]="isRoot() || loading()"
            (click)="navigateUp()"
            title="Up one folder"
            aria-label="Up one folder"
          >
            <app-icon name="arrow-upward" sizeClass="w-4 h-4" />
          </button>
          <span class="min-w-0 flex-1 truncate font-mono text-xs text-slate-700" [title]="currentPath() || 'Drives'">
            {{ currentPath() || 'Available drives' }}
          </span>
        </div>

        @if (loading()) {
          <app-progress-bar />
        }

        <div class="max-h-80 divide-y divide-slate-100 overflow-y-auto rounded-xl border border-slate-200">
          @if (!loading() && directories().length === 0) {
            <p class="px-4 py-8 text-center text-xs text-slate-500">No subfolders inside this folder.</p>
          } @else {
            @for (dir of directories(); track dir.fullPath) {
              <button
                type="button"
                class="flex w-full items-center gap-2.5 px-3.5 py-2.5 text-left text-sm text-slate-700 transition hover:bg-indigo-50/60 hover:text-indigo-900"
                (click)="navigateInto(dir)"
              >
                <app-icon name="folder" sizeClass="w-4 h-4 text-amber-500" />
                <span class="min-w-0 flex-1 truncate font-medium">{{ dir.name }}</span>
                <app-icon name="chevron-right" sizeClass="w-4 h-4 text-slate-300" />
              </button>
            }
          }
        </div>
      </div>

      <ng-container modal-footer>
        <button
          type="button"
          class="rounded-lg border border-slate-300 bg-white px-3.5 py-2 text-sm font-medium text-slate-700 transition hover:bg-slate-50"
          (click)="cancelled.emit()"
        >
          Cancel
        </button>
        <button
          type="button"
          class="rounded-lg bg-indigo-600 px-3.5 py-2 text-sm font-medium text-white shadow-xs transition hover:bg-indigo-700 disabled:cursor-not-allowed disabled:opacity-45"
          [disabled]="!currentPath()"
          (click)="selectCurrent()"
        >
          Select this folder
        </button>
      </ng-container>
    </app-modal-shell>
  `,
})
export class FolderPickerDialogComponent {
  private readonly api = inject(ApiService);
  private readonly toast = inject(ToastService);

  readonly selected = output<string>();
  readonly cancelled = output<void>();

  private readonly requestedPath = signal<string | undefined>(undefined);
  private readonly resource = this.api.browseDirectoryResource(this.requestedPath);

  protected readonly currentPath = computed(() => (this.resource.hasValue() ? this.resource.value().path : ''));
  protected readonly parentPath = computed(() => (this.resource.hasValue() ? this.resource.value().parentPath : null));
  protected readonly directories = computed(() => (this.resource.hasValue() ? this.resource.value().directories : []));
  protected readonly isRoot = computed(() => !this.requestedPath());
  protected readonly loading = this.resource.isLoading;

  constructor() {
    effect(() => {
      if (this.resource.error()) {
        this.toast.error('Could not list that directory.');
      }
    });
  }

  protected navigateInto(dir: DirectoryEntryDto): void {
    this.requestedPath.set(dir.fullPath);
  }

  protected navigateUp(): void {
    this.requestedPath.set(this.parentPath() ?? undefined);
  }

  protected selectCurrent(): void {
    if (this.currentPath()) {
      this.selected.emit(this.currentPath());
    }
  }
}
