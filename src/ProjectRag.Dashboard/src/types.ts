// The types of the web API. They follow the C# records of ProjectRag.Core in camelCase.

export interface KnowledgeStatus {
  database: string | null;
  embedding: { url: string; name: string; modelId: number | null; dims: number | null; problem: string | null };
  reranker: { url: string; enabled: boolean; healthy: boolean };
  counts: { sources: number; documents: number; chunks: number; embeddedChunks: number } | null;
  indexer: { current: IndexRun | null; queued: number[] };
}

export interface Source {
  sourceId: number;
  name: string;
  description: string;
  rootPath: string;
  urls: string[];
  includeGlobs: string[];
  excludeGlobs: string[];
  categoryRules: CategoryRule[];
  replacements: TextReplacement[];
  createdAt: string;
  indexedAt: string | null;
  documentCount: number;
  chunkCount: number;
  embeddedCount: number;
}

export interface SourceInput {
  name: string;
  description: string;
  rootPath: string;
  urls: string[] | null;
  includeGlobs: string[] | null;
  excludeGlobs: string[] | null;
  categoryRules: CategoryRule[] | null;
  replacements: TextReplacement[] | null;
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
}

export interface SearchResult {
  mode: 'hybrid' | 'vector' | 'text';
  reranked: boolean;
  notes: string[];
  hits: SearchHit[];
}
