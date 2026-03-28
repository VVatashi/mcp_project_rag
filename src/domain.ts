export type ProjectFileKind = "code" | "doc";

export interface ProjectRegistration {
  id: string;
  rootPath: string;
  description?: string;
  includeGlobs: string[];
  excludeGlobs: string[];
  createdAt: string;
  updatedAt: string;
  lastIndexedAt?: string;
  lastIndexStats?: IndexRunStats;
}

export interface IndexRunStats {
  projectId: string;
  mode?: "full" | "incremental";
  fileCount: number;
  chunkCount: number;
  indexedFileCount?: number;
  changedFileCount?: number;
  removedFileCount?: number;
  unchangedFileCount?: number;
  startedAt: string;
  finishedAt: string;
  durationMs: number;
}

export interface IndexedChunk {
  id: string;
  projectId: string;
  sourcePath: string;
  sourceName: string;
  kind: ProjectFileKind;
  language: string;
  chunkIndex: number;
  startLine?: number;
  endLine?: number;
  text: string;
  hash: string;
}

export interface SearchHit {
  id: string;
  projectId: string;
  sourcePath: string;
  sourceName: string;
  kind: ProjectFileKind;
  language: string;
  chunkIndex: number;
  startLine?: number;
  endLine?: number;
  text: string;
  distance?: number | null;
  retrievalScore: number;
  rerankScore: number;
  finalScore: number;
}

export interface ContextBundle {
  projectId: string;
  normalizedQuery: string;
  summary: string;
  contextText: string;
  hits: SearchHit[];
}

export interface IndexChunkPreview {
  id: string;
  sourcePath: string;
  sourceName: string;
  kind: ProjectFileKind;
  language: string;
  chunkIndex: number;
  startLine?: number;
  endLine?: number;
  text: string;
  hash: string;
}

export interface IndexedChunkRecord extends IndexChunkPreview {
  projectId: string;
}

export interface ProjectFileIndexState {
  relativePath: string;
  mtimeMs: number;
  size: number;
  contentHash: string;
  chunkIds: string[];
  chunkHashes: string[];
}

export interface ProjectIndexState {
  projectId: string;
  rootPath: string;
  updatedAt: string;
  files: Record<string, ProjectFileIndexState>;
}

export interface ProjectIndexSummary {
  projectId: string;
  collectionName: string;
  chunkCount: number;
  chunks: IndexChunkPreview[];
}
