import { CommonModule } from '@angular/common';
import { Component, DestroyRef, ElementRef, OnDestroy, computed, inject, signal, viewChild } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormControl, ReactiveFormsModule } from '@angular/forms';
import { MatAutocompleteModule, MatAutocompleteSelectedEvent } from '@angular/material/autocomplete';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule, MatButtonToggleChange } from '@angular/material/button-toggle';
import { MatCardModule } from '@angular/material/card';
import { MatChipsModule } from '@angular/material/chips';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSnackBar } from '@angular/material/snack-bar';
import { debounceTime, distinctUntilChanged, switchMap } from 'rxjs';
import { Network, type Edge as VisEdge, type Node as VisNode, type Options as VisOptions } from 'vis-network/standalone';
import { ApiService } from '../../core/api.service';
import {
  GraphEdgeDto,
  GraphEdgeKind,
  GraphNeighborhoodDto,
  GraphNode,
  GraphNodeKind,
  ImpactSet,
  SymbolSummaryDto,
} from '../../core/models';
import { ProjectContextService } from '../projects/project-context.service';

interface NodeKindStyle {
  color: string;
  shape: string;
}

interface EdgeKindStyle {
  color: string;
  dashes: boolean;
}

// This project's dataviz skill's validated 8-hue categorical palette (light-mode steps only --
// styles.scss hardcodes `color-scheme: light`, this dashboard has no dark theme yet). Structural/
// container kinds (Project/Directory/Module/Package) share a muted ink color -- they're rarely the
// point of a symbol's blast radius -- and lean on shape alone to stay distinct from each other; the
// symbol kinds a search is actually about each get their own hue. No two kinds share both a color
// AND a shape.
const NODE_KIND_STYLES: Record<GraphNodeKind, NodeKindStyle> = {
  Project: { color: '#898781', shape: 'hexagon' },
  Directory: { color: '#898781', shape: 'square' },
  Module: { color: '#898781', shape: 'diamond' },
  Package: { color: '#898781', shape: 'database' },
  File: { color: '#2a78d6', shape: 'box' },
  Class: { color: '#4a3aa7', shape: 'ellipse' },
  Interface: { color: '#1baf7a', shape: 'hexagon' },
  Function: { color: '#eb6834', shape: 'triangle' },
  Method: { color: '#eb6834', shape: 'dot' },
  Type: { color: '#eda100', shape: 'diamond' },
  Variable: { color: '#e87ba4', shape: 'star' },
  Test: { color: '#008300', shape: 'square' },
  UnresolvedExternal: { color: '#e34948', shape: 'triangleDown' },
};

// Extends/Implements and Imports/Exports are inverse pairs of the same relationship family, so
// each pair shares a hue and differs only by dash style -- every other kind gets its own hue.
const EDGE_KIND_STYLES: Record<GraphEdgeKind, EdgeKindStyle> = {
  Contains: { color: '#898781', dashes: false },
  Imports: { color: '#2a78d6', dashes: false },
  Exports: { color: '#2a78d6', dashes: true },
  Calls: { color: '#eb6834', dashes: false },
  References: { color: '#1baf7a', dashes: false },
  Extends: { color: '#4a3aa7', dashes: false },
  Implements: { color: '#4a3aa7', dashes: true },
  Uses: { color: '#eda100', dashes: false },
  Tests: { color: '#008300', dashes: false },
  DependsOn: { color: '#e34948', dashes: false },
};

const DEFAULT_NODE_SIZE = 16;
const CENTER_NODE_SIZE = 24;

// Above this many nodes (a whole-project full graph easily hits thousands, vs. a handful-to-
// dozens for a hop-bounded neighborhood), per-edge labels/arrows turn into unreadable clutter and
// meaningfully slow down rendering -- Obsidian's own graph view drops edge chrome at this scale
// too, relying on position/proximity instead of reading every connection's label.
const DENSE_GRAPH_NODE_THRESHOLD = 300;

/**
 * Node.label is a full qualified name (Namespace.Class.Method) or, for File/Directory-kind
 * nodes, a relative path (Api/MemoryEndpoints.cs) -- both too wide to draw on a node. A path's
 * last segment is after the last slash (its filename, extension included); everything else's is
 * after the last dot (slicing a path on '.' would only strip its extension).
 */
function shortNodeLabel(label: string): string {
  const lastSlash = Math.max(label.lastIndexOf('/'), label.lastIndexOf('\\'));
  if (lastSlash !== -1) {
    return label.slice(lastSlash + 1);
  }
  const lastDot = label.lastIndexOf('.');
  return lastDot === -1 ? label : label.slice(lastDot + 1);
}

/**
 * Search-then-inspect flow for a symbol's blast radius:
 * 1. Type-ahead over /api/symbols (debounced) rendered as a Material autocomplete.
 * 2. Selecting a match calls /api/graph/impact (the three ImpactSet buckets) AND
 *    /api/graph/neighborhood (the visual node/edge graph rendered below via vis-network).
 * 3. Clicking a node in the graph shows it in the info panel; the panel's "Re-center" button
 *    re-fetches the neighborhood for that node's id and re-renders around it.
 */
@Component({
  selector: 'app-graph-impact-panel',
  imports: [
    CommonModule,
    ReactiveFormsModule,
    MatAutocompleteModule,
    MatButtonModule,
    MatButtonToggleModule,
    MatCardModule,
    MatChipsModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatProgressBarModule,
  ],
  templateUrl: './graph-impact-panel.component.html',
  styleUrl: './graph-impact-panel.component.scss',
})
export class GraphImpactPanelComponent implements OnDestroy {
  private readonly api = inject(ApiService);
  private readonly ctx = inject(ProjectContextService);
  private readonly snackBar = inject(MatSnackBar);
  private readonly destroyRef = inject(DestroyRef);

  // Typed as string | SymbolSummaryDto: Material's autocomplete writes the selected option's
  // whole value back into this control on selection (see onSymbolSelected/displaySymbol), it's
  // not always the typed-in string.
  protected readonly searchControl = new FormControl<string | SymbolSummaryDto>('', { nonNullable: true });
  protected readonly matches = signal<SymbolSummaryDto[]>([]);
  protected readonly searching = signal(false);

  protected readonly maxHopsControl = new FormControl(3, { nonNullable: true });

  // 'search': type a symbol, see its blast radius + a neighborhood graph centered on it (the
  // original behavior). 'full': an Obsidian-style whole-project graph -- every node and edge,
  // nothing hop-bounded. Clicking a node in 'full' mode can jump into 'search' mode centered on
  // it (focusBlastRadius) -- the bridge between "see everything" and "inspect one thing".
  protected readonly mode = signal<'search' | 'full'>('search');
  protected readonly loadingFullGraph = signal(false);

  protected readonly selectedSymbol = signal<SymbolSummaryDto | null>(null);
  protected readonly impact = signal<ImpactSet | null>(null);
  protected readonly loadingImpact = signal(false);

  // vis-network's container div -- unconditionally present whenever mode() is 'full' or a symbol
  // is selected (never behind its own @if), so this query is populated before the first
  // neighborhood/full-graph response can arrive and try to render into it.
  private readonly graphCanvas = viewChild<ElementRef<HTMLDivElement>>('graphCanvas');

  protected readonly loadingNeighborhood = signal(false);
  protected readonly graphRootId = signal<string | null>(null);
  protected readonly selectedNode = signal<GraphNode | null>(null);

  // Only the originally-searched symbol has a known qualified name (SymbolSummaryDto) -- other
  // GraphNodes reached by panning/re-centering the graph are plain nodes with no such field.
  protected readonly selectedNodeQualifiedName = computed(() => {
    const node = this.selectedNode();
    const symbol = this.selectedSymbol();
    return node && symbol && node.id === symbol.id ? symbol.qualifiedName : null;
  });

  private network: Network | null = null;
  private nodesById = new Map<string, GraphNode>();

  constructor() {
    this.searchControl.valueChanges
      .pipe(
        debounceTime(300),
        distinctUntilChanged(),
        switchMap((query) => {
          // Selecting an autocomplete option writes the whole SymbolSummaryDto back into this
          // control (see [displayWith]="displaySymbol" in the template) — that's not a new
          // search term, it's handled by onSymbolSelected, so skip re-searching for it here.
          if (typeof query !== 'string') {
            this.searching.set(false);
            return [[] as SymbolSummaryDto[]];
          }
          const projectId = this.ctx.projectId();
          const trimmed = query.trim();
          if (!projectId || trimmed.length === 0) {
            this.searching.set(false);
            return [[] as SymbolSummaryDto[]];
          }
          this.searching.set(true);
          return this.api.searchSymbols(projectId, trimmed);
        }),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe({
        next: (results) => {
          this.matches.set(results);
          this.searching.set(false);
        },
        error: () => {
          this.searching.set(false);
          this.matches.set([]);
          this.snackBar.open('Symbol search failed — is Context.Host running?', 'Dismiss');
        },
      });
  }

  ngOnDestroy(): void {
    this.network?.destroy();
    this.network = null;
  }

  protected displaySymbol(symbol: SymbolSummaryDto | string): string {
    return typeof symbol === 'string' ? symbol : symbol.name;
  }

  protected onSymbolSelected(event: MatAutocompleteSelectedEvent): void {
    const symbol = event.option.value as SymbolSummaryDto;
    this.switchMode('search');
    this.selectedSymbol.set(symbol);
    this.loadImpact(symbol);
    this.loadNeighborhood(symbol.id);
  }

  protected onModeChange(event: MatButtonToggleChange): void {
    this.switchMode(event.value);
    if (event.value === 'full') {
      this.loadFullGraph();
    }
  }

  /** search/full each render their own `#graphCanvas` div in a mutually-exclusive @if branch, so
   *  switching modes destroys the DOM node the current vis-network `Network` is bound to. Reusing
   *  that stale instance (setData on a detached canvas) would silently render nothing, so every
   *  mode transition destroys it first and lets the next render*() call build a fresh one bound to
   *  whichever canvas is now in the DOM. */
  private switchMode(next: 'search' | 'full'): void {
    if (this.mode() === next) {
      return;
    }
    this.network?.destroy();
    this.network = null;
    this.mode.set(next);
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
        this.nodesById = new Map(fullGraph.nodes.map((node) => [node.id, node]));
        this.selectedNode.set(null);
        this.renderGraph(null, fullGraph);
      },
      error: () => {
        this.loadingFullGraph.set(false);
        this.snackBar.open('Failed to load the full project graph.', 'Dismiss');
      },
    });
  }

  /** The info panel's "Show blast radius" button in full-graph mode: jumps into search mode
   *  centered on the clicked node, reusing the same impact+neighborhood plumbing a real symbol
   *  search uses. GraphNode carries no qualifiedName/fileId/line range of its own, so this fills
   *  a placeholder SymbolSummaryDto with just enough fields for display (loadImpact/
   *  loadNeighborhood only ever read `.id`; the template only ever reads `.name`/`.kind`/
   *  `.qualifiedName`). */
  protected focusBlastRadius(node: GraphNode): void {
    const placeholder: SymbolSummaryDto = {
      id: node.id,
      name: node.label,
      qualifiedName: node.label,
      kind: node.kind,
      fileId: node.owningFileId ?? '',
      startLine: 0,
      endLine: 0,
    };
    this.switchMode('search');
    this.selectedSymbol.set(placeholder);
    this.loadImpact(placeholder);
    this.loadNeighborhood(node.id);
  }

  protected reloadImpact(): void {
    const symbol = this.selectedSymbol();
    if (symbol) {
      this.loadImpact(symbol);
      // Recompute re-centers on whatever node the graph is currently showing, not necessarily the
      // originally-searched symbol, so a changed max-hops value also applies after a re-center.
      this.loadNeighborhood(this.graphRootId() ?? symbol.id);
    }
  }

  /** The info panel's "Re-center" button: re-fetch+re-render the graph around the clicked node. */
  protected recenterOn(node: GraphNode): void {
    this.loadNeighborhood(node.id);
  }

  private loadImpact(symbol: SymbolSummaryDto): void {
    const maxHops = this.maxHopsControl.value || 3;
    this.loadingImpact.set(true);
    this.impact.set(null);
    this.api.graphImpact(symbol.id, maxHops).subscribe({
      next: (impactSet) => {
        this.impact.set(impactSet);
        this.loadingImpact.set(false);
      },
      error: () => {
        this.loadingImpact.set(false);
        this.snackBar.open('Failed to load impact graph for that symbol.', 'Dismiss');
      },
    });
  }

  private loadNeighborhood(nodeId: string): void {
    const hops = this.maxHopsControl.value || 3;
    this.loadingNeighborhood.set(true);
    this.api.getGraphNeighborhood(nodeId, hops).subscribe({
      next: (neighborhood) => {
        this.loadingNeighborhood.set(false);
        this.graphRootId.set(nodeId);
        this.nodesById = new Map(neighborhood.nodes.map((node) => [node.id, node]));
        this.selectedNode.set(this.nodesById.get(nodeId) ?? null);
        this.renderGraph(nodeId, neighborhood);
      },
      error: () => {
        this.loadingNeighborhood.set(false);
        this.snackBar.open('Failed to load the graph view for that node.', 'Dismiss');
      },
    });
  }

  private renderGraph(rootId: string | null, neighborhood: GraphNeighborhoodDto): void {
    const container = this.graphCanvas()?.nativeElement;
    if (!container) {
      return;
    }

    const dense = neighborhood.nodes.length > DENSE_GRAPH_NODE_THRESHOLD;
    const data = {
      nodes: neighborhood.nodes.map((node) => this.toVisNode(node, rootId)),
      edges: neighborhood.edges.map((edge, index) => this.toVisEdge(edge, index, dense)),
    };

    if (!this.network) {
      this.network = new Network(container, data, this.buildNetworkOptions(dense));
      this.network.on('click', (params: { nodes: string[] }) => this.onNetworkNodeClick(params));
    } else {
      this.network.setOptions(this.buildNetworkOptions(dense));
      this.network.setData(data);
    }

    // Physics-based layout settles asynchronously -- fit immediately (covers a tiny neighborhood
    // that never needs to stabilize) and again once stabilization actually finishes.
    this.network.fit();
    this.network.once('stabilizationIterationsDone', () => this.network?.fit());
  }

  private onNetworkNodeClick(params: { nodes: string[] }): void {
    const nodeId = params.nodes[0];
    if (!nodeId) {
      return;
    }
    const node = this.nodesById.get(nodeId);
    if (node) {
      this.selectedNode.set(node);
    }
  }

  private toVisNode(node: GraphNode, rootId: string | null): VisNode {
    const style = NODE_KIND_STYLES[node.kind];
    const isRoot = node.id === rootId;
    return {
      id: node.id,
      // Labels are qualified names (Namespace.Class.Method) -- drawn in full they're wider than
      // the space physics gives a node, so they overlap neighbors and clip past the canvas edge.
      // The short form goes on the node; the full label is still in the hover title and the info
      // panel (selectedNode() keeps the untouched GraphNode).
      label: shortNodeLabel(node.label),
      title: `${node.kind}: ${node.label}`,
      color: style.color,
      shape: style.shape,
      size: isRoot ? CENTER_NODE_SIZE : DEFAULT_NODE_SIZE,
      borderWidth: isRoot ? 4 : 1,
      font: { color: '#0b0b0b', size: 13 },
    };
  }

  private toVisEdge(edge: GraphEdgeDto, index: number, dense: boolean): VisEdge {
    const style = EDGE_KIND_STYLES[edge.kind];
    return {
      // sourceId/targetId alone aren't unique when two edge kinds connect the same pair of nodes.
      id: `${edge.sourceId}-${edge.kind}-${edge.targetId}-${index}`,
      from: edge.sourceId,
      to: edge.targetId,
      // A dense (whole-project) graph has thousands of these -- per-edge labels/arrowheads would
      // be unreadable clutter and materially slow down rendering, so only a focused neighborhood
      // (a few dozen edges at most) draws them.
      label: dense ? undefined : edge.kind,
      color: { color: style.color, highlight: style.color },
      dashes: style.dashes,
      arrows: dense ? undefined : 'to',
      width: dense ? 0.5 : 1.5,
      font: dense ? undefined : { size: 9, color: '#52514e', align: 'top', strokeWidth: 0 },
    };
  }

  private buildNetworkOptions(dense: boolean): VisOptions {
    return {
      autoResize: true,
      height: '100%',
      width: '100%',
      interaction: { hover: true, tooltipDelay: 150, navigationButtons: true, keyboard: false },
      physics: {
        // Fewer iterations for a dense graph: barnesHut is O(n log n) per iteration, and at
        // thousands of nodes 150 iterations would noticeably stall the tab for little visual gain
        // over ~60 (interaction.hover already loses precision at this density regardless).
        stabilization: { iterations: dense ? 60 : 150, fit: true },
        barnesHut: dense
          ? { gravitationalConstant: -4000, springLength: 100, springConstant: 0.02, avoidOverlap: 0.1 }
          : { gravitationalConstant: -2200, springLength: 160, springConstant: 0.03, avoidOverlap: 0.6 },
      },
      // A fixed seed makes the same neighborhood lay out the same way every time it's rendered,
      // rather than jittering to a different arrangement on every recompute/re-center.
      layout: { improvedLayout: true, randomSeed: 42 },
      nodes: { borderWidthSelected: 3 },
      edges: { width: 1.5, smooth: !dense },
    };
  }
}
