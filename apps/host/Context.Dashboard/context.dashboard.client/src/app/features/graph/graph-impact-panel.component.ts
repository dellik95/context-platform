import { Component, computed, effect, inject, signal } from '@angular/core';
import { FormField, form, max, min } from '@angular/forms/signals';
import { ApiService } from '../../core/api.service';
import { GraphNeighborhoodDto, GraphNode, ImpactSet, SymbolSummaryDto } from '../../core/models';
import { BadgeComponent } from '../../shared/ui/badge.component';
import { EmptyStateComponent } from '../../shared/ui/empty-state.component';
import { IconComponent } from '../../shared/ui/icon.component';
import { ProgressBarComponent } from '../../shared/ui/progress-bar.component';
import { SpinnerComponent } from '../../shared/ui/spinner.component';
import { ToastService } from '../../shared/ui/toast.service';
import { ProjectContextService } from '../projects/project-context.service';
import { GraphCanvasComponent } from './graph-canvas.component';
import { GraphImpactResultsComponent } from './graph-impact-results.component';
import { GraphNodeInspectorComponent } from './graph-node-inspector.component';
import { SymbolAutocompleteComponent } from './symbol-autocomplete.component';

function toSymbolPlaceholder(node: GraphNode): SymbolSummaryDto {
  return {
    id: node.id,
    name: node.label,
    qualifiedName: node.label,
    kind: node.kind,
    fileId: node.owningFileId ?? '',
    startLine: 0,
    endLine: 0,
  };
}

@Component({
  selector: 'app-graph-impact-panel',
  standalone: true,
  imports: [
    FormField,
    BadgeComponent,
    IconComponent,
    SpinnerComponent,
    ProgressBarComponent,
    EmptyStateComponent,
    SymbolAutocompleteComponent,
    GraphCanvasComponent,
    GraphNodeInspectorComponent,
    GraphImpactResultsComponent,
  ],
  templateUrl: './graph-impact-panel.component.html',
})
export class GraphImpactPanelComponent {
  private readonly api = inject(ApiService);
  private readonly ctx = inject(ProjectContextService);
  private readonly toast = inject(ToastService);

  private readonly searchQuery = signal('');
  private readonly searchResource = this.api.searchSymbolsResource(
    computed(() => {
      const projectId = this.ctx.projectId();
      const query = this.searchQuery();
      return projectId && query ? { projectId, query } : undefined;
    }),
  );
  protected readonly matches = computed(() => (this.searchResource.hasValue() ? this.searchResource.value() : []));
  protected readonly searching = this.searchResource.isLoading;

  protected readonly maxHopsModel = signal(3);
  protected readonly maxHopsForm = form(this.maxHopsModel, (path) => {
    min(path, 1);
    max(path, 10);
  });

  protected readonly mode = signal<'search' | 'full'>('search');
  protected readonly loadingFullGraph = signal(false);

  protected readonly selectedSymbol = signal<SymbolSummaryDto | null>(null);
  protected readonly impact = signal<ImpactSet | null>(null);
  protected readonly loadingImpact = signal(false);

  protected readonly graphData = signal<GraphNeighborhoodDto | null>(null);
  protected readonly loadingNeighborhood = signal(false);
  protected readonly graphRootId = signal<string | null>(null);
  protected readonly selectedNode = signal<GraphNode | null>(null);

  protected readonly containedNodes = computed<GraphNode[]>(() => {
    const node = this.selectedNode();
    const graph = this.graphData();
    if (!node || !graph) {
      return [];
    }
    const byId = new Map(graph.nodes.map((n) => [n.id, n]));
    return graph.edges
      .filter((edge) => edge.kind === 'Contains' && edge.sourceId === node.id)
      .map((edge) => byId.get(edge.targetId))
      .filter((n): n is GraphNode => !!n);
  });

  protected readonly selectedNodeQualifiedName = computed(() => {
    const node = this.selectedNode();
    const symbol = this.selectedSymbol();
    return node && symbol && node.id === symbol.id ? symbol.qualifiedName : null;
  });

  constructor() {
    effect(() => {
      if (this.searchResource.error()) {
        this.toast.error('Symbol search failed — is Context Dashboard running?');
      }
    });
  }

  protected onQueryChanged(query: string): void {
    this.searchQuery.set(query);
  }

  protected onSymbolSelected(symbol: SymbolSummaryDto): void {
    this.mode.set('search');
    this.selectedSymbol.set(symbol);
    this.loadImpact(symbol);
    this.loadNeighborhood(symbol.id);
  }

  protected setMode(next: 'search' | 'full'): void {
    if (this.mode() === next) {
      return;
    }
    this.mode.set(next);
    if (next === 'full') {
      this.loadFullGraph();
    } else if (!this.selectedSymbol()) {
      this.graphData.set(null);
      this.graphRootId.set(null);
      this.selectedNode.set(null);
    }
  }

  protected loadFullGraph(): void {
    const projectId = this.ctx.projectId();
    if (!projectId) {
      return;
    }
    this.loadingFullGraph.set(true);
    this.api.getFullGraph(projectId).subscribe({
      next: (fullGraph) => {
        this.loadingFullGraph.set(false);
        this.graphRootId.set(null);
        this.selectedNode.set(null);
        this.graphData.set(fullGraph);
      },
      error: () => {
        this.loadingFullGraph.set(false);
        this.toast.error('Failed to load the full project graph.');
      },
    });
  }

  protected onNodeSelected(node: GraphNode): void {
    this.selectedNode.set(node);
    this.loadImpact(toSymbolPlaceholder(node));
  }

  protected onInspectorAction(node: GraphNode): void {
    if (this.mode() === 'full') {
      const placeholder = toSymbolPlaceholder(node);
      this.mode.set('search');
      this.selectedSymbol.set(placeholder);
      this.loadImpact(placeholder);
      this.loadNeighborhood(node.id);
    } else {
      this.loadNeighborhood(node.id);
    }
  }

  protected reloadImpact(): void {
    const symbol = this.selectedSymbol();
    if (symbol) {
      this.loadImpact(symbol);
      this.loadNeighborhood(this.graphRootId() ?? symbol.id);
    }
  }

  private loadImpact(symbol: SymbolSummaryDto): void {
    const maxHops = this.maxHopsModel() || 3;
    this.loadingImpact.set(true);
    this.impact.set(null);
    this.api.graphImpact(symbol.id, maxHops).subscribe({
      next: (impactSet) => {
        this.impact.set(impactSet);
        this.loadingImpact.set(false);
      },
      error: () => {
        this.loadingImpact.set(false);
        this.toast.error('Failed to load impact graph for that symbol.');
      },
    });
  }

  private loadNeighborhood(nodeId: string): void {
    const hops = this.maxHopsModel() || 3;
    this.loadingNeighborhood.set(true);
    this.api.getGraphNeighborhood(nodeId, hops).subscribe({
      next: (neighborhood) => {
        this.loadingNeighborhood.set(false);
        this.graphRootId.set(nodeId);
        this.graphData.set(neighborhood);
        this.selectedNode.set(neighborhood.nodes.find((n) => n.id === nodeId) ?? null);
      },
      error: () => {
        this.loadingNeighborhood.set(false);
        this.toast.error('Failed to load the graph view for that node.');
      },
    });
  }
}
