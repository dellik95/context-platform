import { Component, computed, input } from '@angular/core';
import { GraphNode, ImpactNode, ImpactSet } from '../../core/models';
import { BadgeComponent } from '../../shared/ui/badge.component';
import { ProgressBarComponent } from '../../shared/ui/progress-bar.component';

interface ImpactBucket {
  title: string;
  emptyMessage: string;
  items: readonly ImpactNode[];
}

@Component({
  selector: 'app-graph-impact-results',
  standalone: true,
  imports: [BadgeComponent, ProgressBarComponent],
  template: `
    <div class="space-y-4">
      @if (loading()) {
        <app-progress-bar />
      }

      <div class="grid grid-cols-1 gap-4 lg:grid-cols-2">
        <section class="rounded-xl border border-slate-200 bg-white p-4 shadow-xs">
          <div class="mb-3 flex items-center justify-between gap-2">
            <h2 class="text-sm font-semibold text-slate-900">Methods</h2>
            <span class="text-xs text-slate-500">{{ containedNodes().length }}</span>
          </div>
          @if (containedNodes().length === 0) {
            <p class="text-xs text-slate-500">No contained methods found.</p>
          } @else {
            <ul class="divide-y divide-slate-100">
              @for (child of containedNodes(); track child.id) {
                <li class="flex items-center justify-between gap-2 py-2 first:pt-0 last:pb-0">
                  <span class="min-w-0 truncate font-mono text-xs text-slate-800" [title]="child.label">
                    {{ child.label }}
                  </span>
                  <app-badge tone="neutral">{{ child.kind }}</app-badge>
                </li>
              }
            </ul>
          }
        </section>

        @for (bucket of buckets(); track bucket.title) {
          <section class="rounded-xl border border-slate-200 bg-white p-4 shadow-xs">
            <div class="mb-3 flex items-center justify-between gap-2">
              <h2 class="text-sm font-semibold text-slate-900">{{ bucket.title }}</h2>
              <span class="text-xs text-slate-500">{{ bucket.items.length }}</span>
            </div>
            @if (bucket.items.length === 0) {
              <p class="text-xs text-slate-500">{{ bucket.emptyMessage }}</p>
            } @else {
              <ul class="divide-y divide-slate-100">
                @for (item of bucket.items; track item.node.id) {
                  <li class="space-y-1 py-2.5 first:pt-0 last:pb-0">
                    <div class="flex flex-wrap items-center gap-2">
                      <span class="font-mono text-xs font-medium text-slate-900">{{ item.node.label }}</span>
                      <app-badge tone="primary">{{ item.node.kind }}</app-badge>
                      <span class="ml-auto text-[11px] text-slate-500">
                        {{ item.hops }} hop{{ item.hops === 1 ? '' : 's' }} away
                      </span>
                    </div>
                    <div class="truncate font-mono text-[11px] text-slate-500" [title]="item.path">{{ item.path }}</div>
                  </li>
                }
              </ul>
            }
          </section>
        }
      </div>
    </div>
  `,
})
export class GraphImpactResultsComponent {
  readonly containedNodes = input<readonly GraphNode[]>([]);
  readonly impact = input<ImpactSet | null>(null);
  readonly loading = input(false);

  protected readonly buckets = computed<readonly ImpactBucket[]>(() => {
    const impactSet = this.impact();
    if (!impactSet) {
      return [];
    }
    return [
      { title: 'Callers', emptyMessage: 'No callers found.', items: impactSet.callers },
      { title: 'Implementers', emptyMessage: 'No implementers found.', items: impactSet.implementers },
      { title: 'Covering tests', emptyMessage: 'No tests found.', items: impactSet.coveringTests },
    ];
  });
}
