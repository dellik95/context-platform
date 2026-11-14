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
    <div class="flex flex-1 flex-col gap-2">
      <div class="relative h-[520px] w-full overflow-hidden rounded-xl border border-slate-200 bg-slate-50/40">
        <div #canvasEl class="h-full w-full"></div>
        <button
          type="button"
          class="absolute top-3 right-3 rounded-lg border border-slate-200 bg-white/95 px-2.5 py-1 text-xs font-medium text-slate-700 shadow-xs backdrop-blur-xs transition-colors hover:bg-slate-50"
          (click)="fit()"
        >
          Fit graph
        </button>
      </div>

      <div class="flex flex-wrap items-center gap-x-4 gap-y-1 px-1 text-[11px] text-slate-500">
        @for (item of legendItems; track item.label) {
          <span class="inline-flex items-center gap-1.5">
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
