import {
  IndexRunStats,
  IndexedChunk,
  ProjectIndexState,
  ProjectFileIndexState,
} from "../domain.js";
import { ChunkingService } from "./chunker.js";
import { ChromaProjectStore } from "./chroma-store.js";
import { FileDiscoveryService } from "./file-discovery.js";
import { ProjectIndexStateCache } from "./project-index-state-cache.js";
import { ProjectRegistry } from "./project-registry.js";

export class IndexingService {
  public constructor(
    private readonly registry: ProjectRegistry,
    private readonly discovery: FileDiscoveryService,
    private readonly chunker: ChunkingService,
    private readonly store: ChromaProjectStore,
    private readonly stateCache: ProjectIndexStateCache,
  ) {}

  public async run(projectId: string): Promise<IndexRunStats> {
    const project = this.registry.get(projectId);
    if (!project) {
      throw new Error(`Project ${projectId} is not registered`);
    }

    const startedAt = new Date();
    const fileEntries = this.discovery.discoverEntries(project);
    const state = this.stateCache.get(projectId);

    if (!this.hasUsableState(state, project.rootPath)) {
      const files = fileEntries.map((file) => this.discovery.readFile(file));
      const chunks: IndexedChunk[] = files.flatMap((file) =>
        this.chunker.chunkProjectFile(projectId, file),
      );
      await this.store.replaceProjectChunks(projectId, chunks);

      const finishedAt = new Date();
      const stats: IndexRunStats = {
        projectId,
        mode: "full",
        fileCount: files.length,
        chunkCount: chunks.length,
        indexedFileCount: files.length,
        changedFileCount: files.length,
        removedFileCount: 0,
        unchangedFileCount: 0,
        startedAt: startedAt.toISOString(),
        finishedAt: finishedAt.toISOString(),
        durationMs: finishedAt.getTime() - startedAt.getTime(),
      };

      this.stateCache.set(
        projectId,
        this.createStateSnapshot(projectId, project.rootPath, files, {
          changedChunks: chunks,
        }),
      );
      this.registry.updateIndexStats(projectId, stats);
      return stats;
    }

    const previousState = state;
    const sourcePaths = new Set(fileEntries.map((file) => file.relativePath));
    const removedSourcePaths = Object.keys(previousState.files).filter(
      (sourcePath) => !sourcePaths.has(sourcePath),
    );

    const hydratedFiles = fileEntries.map((file) => this.discovery.readFile(file));
    const changedFiles = hydratedFiles.filter((file) => {
      const cached = previousState.files[file.relativePath];
      return !cached || cached.contentHash !== file.contentHash;
    });

    const changedSourcePaths = changedFiles.map((file) => file.relativePath);
    const chunksToUpsert: IndexedChunk[] = changedFiles.flatMap((file) =>
      this.chunker.chunkProjectFile(projectId, file),
    );
    const deletedChunkIds = [
      ...removedSourcePaths.flatMap(
        (sourcePath) => previousState.files[sourcePath]?.chunkIds ?? [],
      ),
      ...changedSourcePaths.flatMap(
        (sourcePath) => previousState.files[sourcePath]?.chunkIds ?? [],
      ),
    ];
    const uniqueDeletedChunkIds = [...new Set(deletedChunkIds)];
    if (uniqueDeletedChunkIds.length > 0) {
      await this.store.deleteProjectChunkIds(projectId, uniqueDeletedChunkIds);
    }
    if (chunksToUpsert.length > 0) {
      await this.store.upsertProjectChunks(projectId, chunksToUpsert);
    }
    const nextState = this.createStateSnapshot(
      projectId,
      project.rootPath,
      hydratedFiles,
      {
        previousState,
        changedChunks: chunksToUpsert,
      },
    );
    const previousChunkCount = Object.values(previousState.files).reduce(
      (total, file) => total + file.chunkIds.length,
      0,
    );
    const removedChunkCount = uniqueDeletedChunkIds.length;
    const nextChunkCount = previousChunkCount - removedChunkCount + chunksToUpsert.length;

    const finishedAt = new Date();
    const stats: IndexRunStats = {
      projectId,
      mode: "incremental",
      fileCount: fileEntries.length,
      chunkCount: Math.max(nextChunkCount, 0),
      indexedFileCount: changedFiles.length,
      changedFileCount: changedFiles.length,
      removedFileCount: removedSourcePaths.length,
      unchangedFileCount: Math.max(fileEntries.length - changedFiles.length, 0),
      startedAt: startedAt.toISOString(),
      finishedAt: finishedAt.toISOString(),
      durationMs: finishedAt.getTime() - startedAt.getTime(),
    };

    this.stateCache.set(projectId, nextState);
    this.registry.updateIndexStats(projectId, stats);
    return stats;
  }

  private hasUsableState(
    state: ProjectIndexState | undefined,
    rootPath: string,
  ): state is ProjectIndexState {
    if (!state || state.rootPath !== rootPath) {
      return false;
    }

    return Object.values(state.files).every(
      (file) =>
        typeof file.contentHash === "string" &&
        Array.isArray(file.chunkIds) &&
        Array.isArray(file.chunkHashes),
    );
  }

  private createStateSnapshot(
    projectId: string,
    rootPath: string,
    files: Array<{ relativePath: string; mtimeMs: number; size: number; contentHash: string }>,
    options?: {
      previousState?: ProjectIndexState;
      changedChunks?: IndexedChunk[];
    },
  ): ProjectIndexState {
    const changedChunksByPath = new Map<string, IndexedChunk[]>();
    for (const chunk of options?.changedChunks ?? []) {
      const bucket = changedChunksByPath.get(chunk.sourcePath);
      if (bucket) {
        bucket.push(chunk);
      } else {
        changedChunksByPath.set(chunk.sourcePath, [chunk]);
      }
    }

    const entries = files.map((file) => [
      file.relativePath,
      this.createFileState(file, options?.previousState, changedChunksByPath),
    ]);

    return {
      projectId,
      rootPath,
      updatedAt: new Date().toISOString(),
      files: Object.fromEntries(entries),
    };
  }

  private createFileState(
    file: { relativePath: string; mtimeMs: number; size: number; contentHash: string },
    previousState: ProjectIndexState | undefined,
    changedChunksByPath: Map<string, IndexedChunk[]>,
  ): ProjectFileIndexState {
    const changedChunks = changedChunksByPath.get(file.relativePath);
    if (changedChunks) {
      const sortedChunks = [...changedChunks].sort(
        (left, right) => left.chunkIndex - right.chunkIndex,
      );
      return {
        relativePath: file.relativePath,
        mtimeMs: file.mtimeMs,
        size: file.size,
        contentHash: file.contentHash,
        chunkIds: sortedChunks.map((chunk) => chunk.id),
        chunkHashes: sortedChunks.map((chunk) => chunk.hash),
      };
    }

    const previousFile = previousState?.files[file.relativePath];
    return {
      relativePath: file.relativePath,
      mtimeMs: file.mtimeMs,
      size: file.size,
      contentHash: file.contentHash,
      chunkIds: previousFile?.chunkIds ?? [],
      chunkHashes: previousFile?.chunkHashes ?? [],
    };
  }
}
