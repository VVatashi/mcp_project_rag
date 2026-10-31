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
  includeGlobs: string[];
  excludeGlobs: string[];
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
  includeGlobs: string[] | null;
  excludeGlobs: string[] | null;
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
}

export interface SearchRequest {
  query: string;
  sources: string[] | null;
  limit: number | null;
  rerank: boolean | null;
}

export interface SearchHit {
  chunkId: number;
  documentId: number;
  source: string;
  path: string;
  title: string;
  heading: string;
  body: string;
  charStart: number;
  charEnd: number;
  vectorRank: number | null;
  textRank: number | null;
  score: number;
  rerankScore: number | null;
}

export interface SearchResult {
  mode: 'hybrid' | 'vector' | 'text';
  reranked: boolean;
  notes: string[];
  hits: SearchHit[];
}
