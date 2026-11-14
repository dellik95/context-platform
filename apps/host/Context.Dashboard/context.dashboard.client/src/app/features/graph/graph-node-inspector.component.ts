import { Component, computed, input, output } from '@angular/core';
import { GraphNode } from '../../core/models';
import { BadgeComponent } from '../../shared/ui/badge.component';
import { IconComponent } from '../../shared/ui/icon.component';
import { SpinnerComponent } from '../../shared/ui/spinner.component';
import { shortNodeLabel } from './graph-styles';

@Component({
  selector: 'app-graph-node-inspector',
  standalone: true,
  imports: [BadgeComponent, IconComponent, SpinnerComponent],
  template: `
    <aside
      class="flex w-full flex-col gap-3 rounded-xl border border-slate-200 bg-slate-50/60 p-4 lg:w-76 lg:shrink-0"
      [class.items-center]="!node()"
      [class.justify-center]="!node()"
      [class.text-center]="!node()"
    >
      @if (node(); as selected) {
        <div class="flex items-start gap-2">
          <app-icon name="account-tree" sizeClass="w-5 h-5 text-indigo-600 mt-0.5 shrink-0" />
          <div class="min-w-0 flex-1">
            <h3 class="truncate text-sm font-semibold text-slate-900" [title]="selected.label">
              {{ shortTitle() }}
            </h3>
            <div class="mt-1.5 flex flex-wrap gap-1.5">
              <app-badge tone="primary">{{ selected.kind }}</app-badge>
              @if (isCentered()) {
                <app-badge tone="success">Centered</app-badge>
              }
            </div>
          </div>
        </div>

        @if (qualifiedName(); as qName) {
          <p class="rounded-lg bg-white px-2.5 py-1.5 font-mono text-[11px] break-all text-slate-600 ring-1 ring-slate-200">
            {{ qName }}
          </p>
        }

        <dl class="space-y-2 text-xs">
          <div>
            <dt class="font-medium text-slate-500">Node ID</dt>
            <dd class="mt-0.5 font-mono break-all text-slate-700">{{ selected.id }}</dd>
          </div>
          @if (selected.refId) {
            <div>
              <dt class="font-medium text-slate-500">Ref ID</dt>
              <dd class="mt-0.5 font-mono break-all text-slate-700">{{ selected.refId }}</dd>
            </div>
          }
        </dl>

        <div class="mt-auto pt-2">
          @if (mode() === 'full') {
            <button
              type="button"
              class="inline-flex w-full items-center justify-center gap-1.5 rounded-lg border border-slate-200 bg-white px-3 py-2 text-xs font-medium text-slate-700 shadow-xs transition-colors hover:bg-slate-50"
              (click)="actionRequested.emit(selected)"
            >
              <app-icon name="account-tree" sizeClass="w-4 h-4 text-indigo-600" />
              Show blast radius
            </button>
          } @else {
            <button
              type="button"
              class="inline-flex w-full items-center justify-center gap-1.5 rounded-lg border border-slate-200 bg-white px-3 py-2 text-xs font-medium text-slate-700 shadow-xs transition-colors hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-50"
              [disabled]="loading() || isCentered()"
              (click)="actionRequested.emit(selected)"
            >
              @if (loading()) {
                <app-spinner sizeClass="w-4 h-4 text-indigo-600" />
              } @else {
                <app-icon name="hub" sizeClass="w-4 h-4 text-indigo-600" />
              }
              Re-center on this node
            </button>
          }
        </div>
      } @else {
        <p class="py-12 text-xs text-slate-500">Click a node in the graph to inspect it.</p>
      }
    </aside>
  `,
})
export class GraphNodeInspectorComponent {
  readonly node = input<GraphNode | null>(null);
  readonly qualifiedName = input<string | null>(null);
  readonly isCentered = input(false);
  readonly mode = input<'search' | 'full'>('search');
  readonly loading = input(false);

  readonly actionRequested = output<GraphNode>();

  protected readonly shortTitle = computed(() => {
    const selected = this.node();
    return selected ? shortNodeLabel(selected.label) : '';
  });
}
