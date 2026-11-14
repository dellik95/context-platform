import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { SystemStatusDto } from '../../core/models';
import { IconComponent } from '../../shared/ui/icon.component';
import { SpinnerComponent } from '../../shared/ui/spinner.component';

@Component({
  selector: 'app-job-queue-kpis',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [IconComponent, SpinnerComponent],
  template: `
    @if (status(); as hostStatus) {
      <div class="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
        <div class="flex items-start justify-between rounded-2xl border border-slate-200 bg-white p-4 shadow-2xs">
          <div>
            <p class="text-xs font-medium text-slate-500">Pending jobs</p>
            <p class="mt-1 text-2xl font-semibold text-slate-900 tabular-nums">{{ hostStatus.jobsPending }}</p>
          </div>
          @if (hostStatus.jobsPending > 0) {
            <button
              type="button"
              class="rounded-lg p-1.5 text-rose-600 transition hover:bg-rose-50 disabled:opacity-45"
              (click)="cleanupPending.emit()"
              [disabled]="cleaningUpPending()"
              title="Delete pending non-user-data jobs"
              aria-label="Delete pending non-user-data jobs"
            >
              @if (cleaningUpPending()) {
                <app-spinner sizeClass="w-4 h-4" />
              } @else {
                <app-icon name="delete-forever" sizeClass="w-4 h-4" />
              }
            </button>
          }
        </div>

        <div class="flex items-start justify-between rounded-2xl border border-slate-200 bg-white p-4 shadow-2xs">
          <div>
            <p class="text-xs font-medium text-slate-500">Running jobs</p>
            <p class="mt-1 text-2xl font-semibold text-indigo-700 tabular-nums">{{ hostStatus.jobsRunning }}</p>
          </div>
          @if (hostStatus.jobsRunning > 0) {
            <button
              type="button"
              class="rounded-lg p-1.5 text-amber-600 transition hover:bg-amber-50 disabled:opacity-45"
              (click)="forceAllRunning.emit()"
              [disabled]="forcingAll()"
              title="Cancel and restart every running job outside the per-project lane limit"
              aria-label="Force restart all running jobs"
            >
              @if (forcingAll()) {
                <app-spinner sizeClass="w-4 h-4" />
              } @else {
                <app-icon name="bolt" sizeClass="w-4 h-4" />
              }
            </button>
          }
        </div>

        <div
          class="flex items-start justify-between rounded-2xl border p-4 shadow-2xs"
          [class]="hostStatus.jobsFailed > 0 ? 'border-rose-200 bg-rose-50/60' : 'border-slate-200 bg-white'"
        >
          <div>
            <p class="text-xs font-medium" [class]="hostStatus.jobsFailed > 0 ? 'text-rose-700' : 'text-slate-500'">
              Failed jobs
            </p>
            <p
              class="mt-1 text-2xl font-semibold tabular-nums"
              [class]="hostStatus.jobsFailed > 0 ? 'text-rose-700' : 'text-slate-900'"
            >
              {{ hostStatus.jobsFailed }}
            </p>
          </div>
        </div>

        <div class="flex items-start justify-between rounded-2xl border border-slate-200 bg-white p-4 shadow-2xs">
          <div>
            <p class="text-xs font-medium text-slate-500">Completed jobs</p>
            <p class="mt-1 text-2xl font-semibold text-emerald-700 tabular-nums">{{ hostStatus.jobsDone }}</p>
          </div>
          @if (hostStatus.jobsDone > 0) {
            <button
              type="button"
              class="rounded-lg p-1.5 text-slate-500 transition hover:bg-slate-100 hover:text-slate-800 disabled:opacity-45"
              (click)="cleanupCompleted.emit()"
              [disabled]="cleaningUpDone()"
              title="Remove finished (Done) jobs from queue"
              aria-label="Remove finished jobs from queue"
            >
              @if (cleaningUpDone()) {
                <app-spinner sizeClass="w-4 h-4" />
              } @else {
                <app-icon name="delete" sizeClass="w-4 h-4" />
              }
            </button>
          }
        </div>
      </div>
    }
  `,
})
export class JobQueueKpisComponent {
  readonly status = input<SystemStatusDto | null>(null);
  readonly cleaningUpPending = input(false);
  readonly forcingAll = input(false);
  readonly cleaningUpDone = input(false);

  readonly cleanupPending = output<void>();
  readonly forceAllRunning = output<void>();
  readonly cleanupCompleted = output<void>();
}
