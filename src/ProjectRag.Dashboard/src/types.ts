// The types of the web API. They follow the C# records of ProjectRag.Core in camelCase.

export interface KnowledgeStatus {
  database: string | null;
  embedding: { url: string; name: string; modelId: number | null; dims: number | null; problem: string | null };
  reranker: { url: string; enabled: boolean; healthy: boolean };
  counts: { sources: number; documents: number; chunks: number; embeddedChunks: number } | null;
  indexer: { current: IndexRun | null; queued: number[] };
}

export type SourceKind = 'docs' | 'code';

export interface Source {
  sourceId: number;
  kind: SourceKind;
  name: string;
  description: string;
  rootPath: string;
  urls: string[];
  includeGlobs: string[];
  excludeGlobs: string[];
  dotGlobs: string[];
  categoryRules: CategoryRule[];
  replacements: TextReplacement[];
  buildProperties: Record<string, string>;
  createdAt: string;
  indexedAt: string | null;
  documentCount: number;
  chunkCount: number;
  embeddedCount: number;
}

export interface SourceInput {
  kind: SourceKind | null;
  name: string;
  description: string;
  rootPath: string;
  urls: string[] | null;
  includeGlobs: string[] | null;
  excludeGlobs: string[] | null;
  dotGlobs: string[] | null;
  categoryRules: CategoryRule[] | null;
  replacements: TextReplacement[] | null;
  buildProperties: Record<string, string> | null;
}

export interface TextReplacement {
  pattern: string;
  find: string;
  replace: string;
}

export interface CategoryRule {
  pattern: string;
  category: string;
}

export interface Category {
  name: string;
  documentCount: number;
  chunkCount: number;
  embeddedCount: number;
}

export type RunStatus = 'running' | 'succeeded' | 'failed' | 'canceled';

export interface IndexRun {
  runId: number;
  sourceId: number;
  source: string;
  status: RunStatus;
  startedAt: string;
  finishedAt: string | null;
  filesSeen: number;
  documentsAdded: number;
  documentsUpdated: number;
  documentsRemoved: number;
  documentsUnchanged: number;
  documentsFailed: number;
  chunksReused: number;
  chunksEmbedded: number;
  warning: string | null;
  error: string | null;
}

export interface DocumentSummary {
  documentId: number;
  sourceId: number;
  source: string;
  category: string;
  path: string;
  title: string;
  sizeBytes: number;
  modifiedAt: string;
  indexedAt: string;
  contentLength: number;
  chunkCount: number;
  language: string | null;
}

export interface CodeMatch {
  documentId: number;
  sourceId: number;
  source: string;
  path: string;
  language: string | null;
  line: number;
  column: number;
  length: number;
  text: string;
  textStart: number;
}

export interface CodeGrepResult {
  matches: CodeMatch[];
  filesMatched: number;
  truncated: boolean;
  elapsedMs: number;
}

export interface CodeGrepParams {
  pattern: string;
  sourceId: number | null;
  regex: boolean;
  caseSensitive: boolean;
  path: string;
  language: string;
  limit: number;
}

export interface CodeLines {
  document: DocumentSummary;
  start: number;
  end: number;
  lineCount: number;
  lines: string[];
}

export interface DocumentPage {
  items: DocumentSummary[];
  total: number;
}

export interface DocumentContent {
  document: DocumentSummary;
  text: string;
  offset: number;
  totalLength: number;
}

export interface Chunk {
  chunkId: number;
  ordinal: number;
  heading: string;
  body: string;
  charStart: number;
  charEnd: number;
  hasEmbedding: boolean;
  sectionId: number | null;
  pageStart: number | null;
  pageEnd: number | null;
  overlapChars: number;
}

export interface ChunkPage {
  items: Chunk[];
  total: number;
  embeddedCount: number;
}

export interface Formula {
  latex: string;
  offset: number;
  page: number | null;
}

export interface Section {
  sectionId: number;
  parentId: number | null;
  ordinal: number;
  level: number;
  number: string | null;
  title: string;
  path: string;
  charStart: number;
  charEnd: number;
  pageStart: number | null;
  pageEnd: number | null;
  chunkCount: number;
}

export interface Anchor {
  name: string;
  charOffset: number;
  chunkId: number | null;
}

export interface ChunkLink {
  kind: 'link' | 'mention';
  label: string;
  targetPath: string | null;
  targetAnchor: string | null;
  targetDocumentId: number | null;
  targetChunkId: number | null;
  targetHeading: string | null;
  targetPage: number | null;
}

export interface ChunkBacklink {
  chunkId: number;
  documentId: number;
  path: string;
  heading: string;
  label: string;
  kind: 'link' | 'mention';
}

export interface ChunkDetail {
  chunkId: number;
  documentId: number;
  source: string;
  path: string;
  title: string;
  ordinal: number;
  heading: string;
  body: string;
  charStart: number;
  charEnd: number;
  pageStart: number | null;
  pageEnd: number | null;
  overlapChars: number;
  sectionId: number | null;
  sectionNumber: string | null;
  hasEmbedding: boolean;
  previousChunkId: number | null;
  nextChunkId: number | null;
  links: ChunkLink[];
  backlinks: ChunkBacklink[];
}

export interface SearchRequest {
  query: string;
  categories: string[] | null;
  sources: string[] | null;
  limit: number | null;
  rerank: boolean | null;
}

export interface SearchHit {
  chunkId: number;
  documentId: number;
  source: string;
  category: string;
  path: string;
  title: string;
  heading: string;
  body: string;
  charStart: number;
  charEnd: number;
  pageStart: number | null;
  pageEnd: number | null;
  sectionId: number | null;
  linkCount: number;
  vectorRank: number | null;
  textRank: number | null;
  score: number;
  rerankScore: number | null;
  copies: HitCopy[];
}

export interface HitCopy {
  chunkId: number;
  documentId: number;
  path: string;
  heading: string;
  similarity: number;
}

export interface SearchResult {
  mode: 'hybrid' | 'vector' | 'text';
  reranked: boolean;
  notes: string[];
  hits: SearchHit[];
}

export interface SymbolSummary {
  symbolId: number;
  sourceId: number;
  source: string;
  projectId: number;
  project: string;
  kind: string;
  name: string;
  fullName: string;
  docId: string;
  signature: string;
  accessibility: string;
  summary: string | null;
  documentId: number | null;
  path: string | null;
  line: number | null;
}

export type MatchClass = 'exact' | 'exact_ci' | 'prefix' | 'humps' | 'qualified' | 'substring' | 'fuzzy';

export interface SymbolMatch extends SymbolSummary {
  match: MatchClass;
}

export interface CodeSearchRequest {
  query: string;
  sourceId?: number | null;
  path?: string | null;
  language?: string | null;
  includeTests?: boolean;
  limit?: number | null;
  rerank?: boolean | null;
}

export interface CodeChunkHit {
  chunkId: number;
  documentId: number;
  sourceId: number;
  source: string;
  path: string;
  language: string;
  heading: string;
  lineStart: number;
  lineEnd: number;
  text: string;
  score: number;
  vectorRank: number | null;
  textRank: number | null;
  rerankScore: number | null;
  symbol: SymbolSummary | null;
}

export interface CodeSearchResult {
  query: string;
  symbols: SymbolMatch[];
  matches: CodeMatch[];
  chunks: CodeChunkHit[];
  mode: 'text' | 'vector' | 'hybrid';
  reranked: boolean;
  notes: string[];
  elapsedMs: number;
}

export interface UseCount {
  kind: string;
  count: number;
}

export interface EdgeSummary {
  symbol: SymbolSummary;
  kinds: UseCount[];
  total: number;
}

export interface ExternalUse {
  target: string;
  assembly: string;
  kinds: UseCount[];
  total: number;
}

export interface Declaration {
  documentId: number;
  path: string;
  startLine: number;
  endLine: number;
  nameLine: number;
  nameColumn: number;
}

export interface SymbolDetail {
  symbol: SymbolSummary;
  modifiers: string[];
  parent: SymbolSummary | null;
  declarations: Declaration[];
  members: SymbolSummary[];
  bases: SymbolSummary[];
  derived: SymbolSummary[];
  overrides: SymbolSummary[];
  overriddenBy: SymbolSummary[];
  uses: EdgeSummary[];
  usedBy: EdgeSummary[];
  externalUses: ExternalUse[];
  referenceCount: number;
}

export interface CodeReference {
  documentId: number;
  path: string;
  line: number;
  column: number;
  length: number;
  kind: string;
  text: string;
  textStart: number;
  from: SymbolSummary | null;
}

export interface CodeReferencePage {
  total: number;
  items: CodeReference[];
}

export interface OutlineNode {
  symbol: SymbolSummary;
  children: OutlineNode[];
}

export interface CodeProject {
  projectId: number;
  path: string;
  name: string;
  targetFramework: string | null;
  isTest: boolean;
  documentCount: number;
  errorCount: number;
  symbolCount: number;
  references: number[];
}

export interface CodeSolution {
  solutionId: number;
  path: string;
  projectIds: number[];
}

export interface CodeDiagnostic {
  project: string | null;
  severity: string;
  code: string;
  message: string;
  path: string | null;
  line: number | null;
}

export interface CodeGraphStatus {
  sourceId: number;
  source: string;
  builtAt: string | null;
  durationMs: number | null;
  sdkVersion: string | null;
  error: string | null;
  projectCount: number;
  symbolCount: number;
  referenceCount: number;
  projects: CodeProject[];
  solutions: CodeSolution[];
  diagnostics: CodeDiagnostic[];
}

export interface CodeSymbolParams {
  q: string;
  sourceId: number | null;
  kind: string;
  project: string;
  limit?: number;
}

export type DiagramView = 'projects' | 'namespaces' | 'namespace' | 'type';

export interface DiagramMember {
  kind: string;
  text: string;
  visibility: '+' | '-' | '#' | '~';
  isStatic: boolean;
  isAbstract: boolean;
}

export interface DiagramNode {
  id: string;
  kind: string;
  label: string;
  fullName: string | null;
  project: string | null;
  docId: string | null;
  isTest: boolean;
  isFocus: boolean;
  isAbstract: boolean;
  count: number | null;
  href: string | null;
  cluster: string | null;
  members: DiagramMember[];
  hiddenMembers: number;
}

export interface DiagramEdge {
  from: string;
  to: string;
  style: 'inherit' | 'implement' | 'dependency' | 'reference';
  kinds: UseCount[];
  total: number;
}

export interface DiagramCluster {
  id: string;
  label: string;
}

export interface Diagram {
  view: DiagramView;
  title: string;
  nodes: DiagramNode[];
  edges: DiagramEdge[];
  clusters: DiagramCluster[];
  truncated: boolean;
  notes: string[];
  dot: string;
}

export type DiagramParams = Record<string, string | number | boolean | null | undefined>;
export type DiagramFormat = 'dot' | 'plantuml';
