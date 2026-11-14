import type { Edge as VisEdge, Node as VisNode, Options as VisOptions } from 'vis-network/standalone';
import { GraphEdgeDto, GraphEdgeKind, GraphNode, GraphNodeKind } from '../../core/models';

export interface NodeKindStyle {
  color: string;
  shape: string;
}

export interface EdgeKindStyle {
  color: string;
  dashes: boolean;
}

// Categorized palette: container kinds share muted stone and differentiate by shape;
// code symbol kinds use distinct hues. No two kinds share both color and shape.
export const NODE_KIND_STYLES: Record<GraphNodeKind, NodeKindStyle> = {
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

export const EDGE_KIND_STYLES: Record<GraphEdgeKind, EdgeKindStyle> = {
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

export const GRAPH_LEGEND_ITEMS: readonly { label: string; color: string }[] = [
  { label: 'Class / Type', color: '#4a3aa7' },
  { label: 'Method / Fn', color: '#eb6834' },
  { label: 'Interface', color: '#1baf7a' },
  { label: 'File', color: '#2a78d6' },
  { label: 'Test', color: '#008300' },
  { label: 'Structural', color: '#898781' },
];

export const DEFAULT_NODE_SIZE = 16;
export const CENTER_NODE_SIZE = 24;
export const DENSE_GRAPH_NODE_THRESHOLD = 300;

/**
 * Extracts short display segment from a qualified symbol name or relative path.
 */
export function shortNodeLabel(label: string): string {
  const lastSlash = Math.max(label.lastIndexOf('/'), label.lastIndexOf('\\'));
  if (lastSlash !== -1) {
    return label.slice(lastSlash + 1);
  }
  const lastDot = label.lastIndexOf('.');
  return lastDot === -1 ? label : label.slice(lastDot + 1);
}

export function toVisNode(node: GraphNode, rootId: string | null): VisNode {
  const style = NODE_KIND_STYLES[node.kind];
  const isRoot = node.id === rootId;
  return {
    id: node.id,
    label: shortNodeLabel(node.label),
    title: `${node.kind}: ${node.label}`,
    color: style.color,
    shape: style.shape,
    size: isRoot ? CENTER_NODE_SIZE : DEFAULT_NODE_SIZE,
    borderWidth: isRoot ? 4 : 1,
    font: { color: '#0f172a', size: 13 },
  };
}

export function toVisEdge(edge: GraphEdgeDto, index: number, dense: boolean): VisEdge {
  const style = EDGE_KIND_STYLES[edge.kind];
  return {
    id: `${edge.sourceId}-${edge.kind}-${edge.targetId}-${index}`,
    from: edge.sourceId,
    to: edge.targetId,
    label: dense ? undefined : edge.kind,
    color: { color: style.color, highlight: style.color },
    dashes: style.dashes,
    arrows: dense ? undefined : 'to',
    width: dense ? 0.5 : 1.5,
    font: dense ? undefined : { size: 9, color: '#475569', align: 'top', strokeWidth: 0 },
  };
}

export function buildNetworkOptions(dense: boolean): VisOptions {
  return {
    autoResize: true,
    height: '100%',
    width: '100%',
    interaction: { hover: true, tooltipDelay: 150, navigationButtons: true, keyboard: false },
    physics: {
      stabilization: { iterations: dense ? 60 : 150, fit: true },
      barnesHut: dense
        ? { gravitationalConstant: -4000, springLength: 100, springConstant: 0.02, avoidOverlap: 0.1 }
        : { gravitationalConstant: -2200, springLength: 160, springConstant: 0.03, avoidOverlap: 0.6 },
    },
    layout: { improvedLayout: true, randomSeed: 42 },
    nodes: { borderWidthSelected: 3 },
    edges: { width: 1.5, smooth: !dense },
  };
}
