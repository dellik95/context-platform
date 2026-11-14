import { Component, ElementRef, OnDestroy, effect, input, output, viewChild } from '@angular/core';
import { Network } from 'vis-network/standalone';
import { GraphNeighborhoodDto, GraphNode } from '../../core/models';
import {
  DENSE_GRAPH_NODE_THRESHOLD,
  GRAPH_LEGEND_ITEMS,
  buildNetworkOptions,
  toVisEdge,
  toVisNode,
} from './graph-styles';

@Component({
  selector: 'app-graph-canvas',
  standalone: true,
  template: `
    <div class="flex min-w-0 flex-1 flex-col gap-2.5">
      <div class="relative h-[380px] w-full overflow-hidden rounded-xl border border-slate-200 bg-gradient-to-br from-slate-50/70 to-indigo-50/20 sm:h-[460px] lg:h-[520px]">
        <div #canvasEl class="h-full w-full"></div>
        <div
          class="absolute top-3 right-3 inline-flex items-center divide-x divide-slate-200 overflow-hidden rounded-xl border border-slate-200 bg-white/95 shadow-xs backdrop-blur-xs"
          role="group"
          aria-label="Graph viewport controls"
        >
          <button
            type="button"
            class="px-2.5 py-1.5 text-xs font-semibold text-slate-700 transition hover:bg-slate-50"
            title="Zoom in"
            aria-label="Zoom in"
            (click)="zoom(1.25)"
          >
            +
          </button>
          <button
            type="button"
            class="px-2.5 py-1.5 text-xs font-semibold text-slate-700 transition hover:bg-slate-50"
            title="Zoom out"
            aria-label="Zoom out"
            (click)="zoom(0.8)"
          >
            −
          </button>
          <button
            type="button"
            class="px-3 py-1.5 text-xs font-medium text-indigo-700 transition hover:bg-indigo-50"
            (click)="fit()"
          >
            Fit graph
          </button>
        </div>
      </div>

      <div class="flex flex-wrap items-center gap-1.5 px-0.5">
        @for (item of legendItems; track item.label) {
          <span class="inline-flex items-center gap-1.5 rounded-full border border-slate-200/80 bg-white px-2.5 py-0.5 text-[11px] font-medium text-slate-600">
            <span class="h-2.5 w-2.5 rounded-full" [style.background-color]="item.color"></span>
            {{ item.label }}
          </span>
        }
      </div>
    </div>
  `,
})
export class GraphCanvasComponent implements OnDestroy {
  readonly graph = input<GraphNeighborhoodDto | null>(null);
  readonly rootId = input<string | null>(null);

  readonly nodeSelected = output<GraphNode>();

  protected readonly legendItems = GRAPH_LEGEND_ITEMS;
  private readonly canvasEl = viewChild<ElementRef<HTMLDivElement>>('canvasEl');

  private network: Network | null = null;
  private nodesById = new Map<string, GraphNode>();

  constructor() {
    effect(() => {
      const neighborhood = this.graph();
      const root = this.rootId();
      const container = this.canvasEl()?.nativeElement;
      if (!container || !neighborhood) {
        return;
      }
      this.render(container, root, neighborhood);
    });
  }

  ngOnDestroy(): void {
    this.network?.destroy();
    this.network = null;
  }

  protected fit(): void {
    this.network?.fit({ animation: { duration: 250, easingFunction: 'easeInOutQuad' } });
  }

  protected zoom(factor: number): void {
    if (!this.network) {
      return;
    }
    const current = this.network.getScale();
    const next = Math.min(4, Math.max(0.1, current * factor));
    this.network.moveTo({ scale: next, animation: { duration: 180, easingFunction: 'easeInOutQuad' } });
  }

  private render(container: HTMLDivElement, rootId: string | null, neighborhood: GraphNeighborhoodDto): void {
    this.nodesById = new Map(neighborhood.nodes.map((node) => [node.id, node]));
    const dense = neighborhood.nodes.length > DENSE_GRAPH_NODE_THRESHOLD;
    const data = {
      nodes: neighborhood.nodes.map((node) => toVisNode(node, rootId)),
      edges: neighborhood.edges.map((edge, index) => toVisEdge(edge, index, dense)),
    };

    if (!this.network) {
      this.network = new Network(container, data, buildNetworkOptions(dense));
      this.network.on('click', (params: { nodes: string[] }) => this.onNodeClick(params));
      this.network.on('dragStart', () => this.network?.setOptions({ physics: true }));
      this.network.on('dragEnd', () => this.network?.setOptions({ physics: false }));
    } else {
      this.network.setOptions(buildNetworkOptions(dense));
      this.network.setData(data);
    }

    this.network.fit();
    this.network.once('stabilizationIterationsDone', () => {
      this.network?.fit();
      this.network?.setOptions({ physics: false });
    });
  }

  private onNodeClick(params: { nodes: string[] }): void {
    const nodeId = params.nodes[0];
    if (!nodeId) {
      return;
    }
    const node = this.nodesById.get(nodeId);
    if (node) {
      this.nodeSelected.emit(node);
    }
  }
}
