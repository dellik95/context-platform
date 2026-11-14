import { Component, computed, effect, inject, output } from '@angular/core';
import { ApiService } from '../../core/api.service';
import { IconComponent } from '../../shared/ui/icon.component';
import { ModalShellComponent } from '../../shared/ui/modal-shell.component';
import { ProgressBarComponent } from '../../shared/ui/progress-bar.component';
import { ToastService } from '../../shared/ui/toast.service';

/** Host-wide log tail (GET /api/logs/tail), fetched when opened. */
@Component({
  selector: 'app-logs-dialog',
  imports: [ModalShellComponent, IconComponent, ProgressBarComponent],
  template: `
    <app-modal-shell
      title="Host Logs"
      [subtitle]="filePath()"
      widthClass="max-w-5xl"
      (closed)="closed.emit()"
    >
      <div modal-header-actions>
        <button
          type="button"
          class="inline-flex items-center gap-1.5 rounded-lg border border-slate-200 bg-white px-2.5 py-1.5 text-xs font-medium text-slate-700 transition hover:bg-slate-50 disabled:opacity-45"
          [disabled]="loading()"
          (click)="refresh()"
        >
          <app-icon name="refresh" sizeClass="w-3.5 h-3.5" />
          <span>Refresh</span>
        </button>
      </div>

      @if (initialLoading()) {
        <app-progress-bar />
      } @else {
        @if (loading()) {
          <div class="mb-2">
            <app-progress-bar />
          </div>
        }

        @if (!lines() || lines()!.length === 0) {
          <p class="py-10 text-center text-sm text-slate-500">No log lines available.</p>
        } @else {
          <pre
            class="max-h-[65vh] overflow-auto rounded-xl border border-slate-800 bg-slate-950 p-4 font-mono text-xs leading-relaxed text-slate-100"
          >{{ lines()!.join('\n') }}</pre>
        }
      }

      <ng-container modal-footer>
        <button
          type="button"
          class="rounded-lg border border-slate-300 bg-white px-3.5 py-2 text-sm font-medium text-slate-700 transition hover:bg-slate-50"
          (click)="closed.emit()"
        >
          Close
        </button>
      </ng-container>
    </app-modal-shell>
  `,
})
export class LogsDialogComponent {
  private readonly api = inject(ApiService);
  private readonly toast = inject(ToastService);

  readonly closed = output<void>();

  private readonly resource = this.api.logTailResource();
  protected readonly lines = computed(() => (this.resource.hasValue() ? this.resource.value().lines : null));
  protected readonly filePath = computed(() => (this.resource.hasValue() ? this.resource.value().filePath : null));
  protected readonly loading = this.resource.isLoading;
  protected readonly initialLoading = computed(() => this.loading() && !this.resource.hasValue());

  constructor() {
    effect(() => {
      if (this.resource.error()) {
        this.toast.error('Failed to load log tail — is Context.Dashboard.Server running?');
      }
    });
  }

  protected refresh(): void {
    this.resource.reload();
  }
}
