import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ApiService } from '../../core/api.service';
import { HookInvocationDto } from '../../core/models';
import { SignalrService } from '../../core/signalr.service';
import { BadgeComponent } from '../../shared/ui/badge.component';
import { CollapsibleSectionComponent } from '../../shared/ui/collapsible-section.component';
import { IconComponent } from '../../shared/ui/icon.component';
import { ProgressBarComponent } from '../../shared/ui/progress-bar.component';
import { ProjectContextService } from '../projects/project-context.service';

@Component({
  selector: 'app-dashboard-hooks-card',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [CommonModule, CollapsibleSectionComponent, BadgeComponent, IconComponent, ProgressBarComponent],
  template: `
    <app-collapsible-section title="Claude Hook Invocations" [(expanded)]="expanded">
      <span section-badges>
        @if (hookCount() > 0) {
          <app-badge tone="primary">{{ hookCount() | number }}</app-badge>
        } @else {
          <app-badge tone="neutral">0</app-badge>
        }
      </span>

      <div class="mb-3 flex items-center justify-between gap-3">
        <button
          type="button"
          class="inline-flex items-center gap-1.5 rounded-lg border border-slate-300 bg-white px-3 py-1.5 text-xs font-medium text-slate-700 transition hover:bg-slate-50 disabled:opacity-45"
          [disabled]="loading()"
          (click)="reload()"
        >
          <app-icon name="refresh" sizeClass="w-3.5 h-3.5" />
          <span>Refresh</span>
        </button>

        @if (loading() && !initialLoading()) {
          <div class="w-28">
            <app-progress-bar />
          </div>
        }
      </div>

      @if (initialLoading()) {
        <app-progress-bar />
      } @else if (error()) {
        <p class="py-6 text-center text-xs text-rose-600">
          Could not load hook invocations — is Context.Dashboard.Server running?
        </p>
      } @else if (recentHooks().length === 0) {
        <p class="py-6 text-center text-xs text-slate-500">No hook invocations recorded yet.</p>
      } @else {
        <div class="space-y-2.5">
          @for (invocation of recentHooks(); track invocation.id) {
            <div class="rounded-xl border border-slate-200 bg-slate-50/70 p-3">
              <div class="mb-1.5 flex items-center justify-between gap-2">
                <app-badge tone="primary">{{ invocation.type }}</app-badge>
                <span class="text-xs text-slate-500">{{ invocation.receivedAt | date: 'short' }}</span>
              </div>
              <pre
                class="max-h-36 overflow-auto rounded-lg border border-slate-200 bg-white p-2.5 font-mono text-[11px] text-slate-700"
              >{{ invocation.payloadJson }}</pre>
            </div>
          }
        </div>
      }
    </app-collapsible-section>
  `,
})
export class DashboardHooksCardComponent {
  private readonly api = inject(ApiService);
  private readonly signalr = inject(SignalrService);
  private readonly destroyRef = inject(DestroyRef);
  protected readonly ctx = inject(ProjectContextService);

  protected readonly expanded = signal(false);

  private readonly hooksStatusResource = this.api.hooksStatusResource(this.ctx.projectId);
  protected readonly hookCount = computed(() => (this.hooksStatusResource.hasValue() ? this.hooksStatusResource.value()?.count ?? 0 : 0));
  protected readonly recentHooks = computed(() => (this.hooksStatusResource.hasValue() ? this.hooksStatusResource.value()?.recent ?? [] : []));
  protected readonly loading = this.hooksStatusResource.isLoading;
  protected readonly initialLoading = computed(() => this.hooksStatusResource.status() === 'loading');
  protected readonly error = computed(() => Boolean(this.hooksStatusResource.error()));

  constructor() {
    this.signalr.hookInvoked$.pipe(takeUntilDestroyed(this.destroyRef)).subscribe((evt) => {
      if (!this.hooksStatusResource.hasValue()) {
        return;
      }

      this.hooksStatusResource.update((current) => {
        if (!current) {
          return current;
        }

        const invocation: HookInvocationDto = {
          id: crypto.randomUUID(),
          type: evt.type,
          payloadJson: evt.payloadJson,
          receivedAt: evt.receivedAt,
        };

        return { count: evt.totalCount, recent: [invocation, ...current.recent].slice(0, 10) };
      });
    });
  }

  protected reload(): void {
    this.hooksStatusResource.reload();
  }
}
