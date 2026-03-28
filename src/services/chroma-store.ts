import { ChromaClient, Collection, IncludeEnum } from "chromadb";
import { AppConfig } from "../config.js";
import { IndexedChunk, IndexedChunkRecord, ProjectIndexSummary, SearchHit } from "../domain.js";
import { TransformersEmbeddingFunction } from "./embedder.js";

type ChunkMetadata = {
  projectId: string;
  sourcePath: string;
  sourceName: string;
  kind: string;
  language: string;
  chunkIndex: number;
  startLine?: number;
  endLine?: number;
  hash: string;
};

export class ChromaProjectStore {
  private readonly client: ChromaClient;

  public constructor(
    config: AppConfig,
    private readonly embedder: TransformersEmbeddingFunction,
  ) {
    const url = new URL(config.chromaUrl);
    this.client = new ChromaClient({
      host: url.hostname,
      port: Number(url.port || (url.protocol === "https:" ? 443 : 80)),
      ssl: url.protocol === "https:",
    });
  }

  public async replaceProjectChunks(
    projectId: string,
    chunks: IndexedChunk[],
  ): Promise<void> {
    const collectionName = this.collectionName(projectId);
    await this.safeDeleteCollection(collectionName);

    const collection = await this.client.getOrCreateCollection({
      name: collectionName,
      embeddingFunction: this.embedder,
    });

    for (let start = 0; start < chunks.length; start += 100) {
      const batch = chunks.slice(start, start + 100);
      await collection.add({
        ids: batch.map((chunk) => chunk.id),
        documents: batch.map((chunk) => chunk.text),
        metadatas: batch.map((chunk) => this.toMetadata(chunk)),
      });
    }
  }

  public async upsertProjectChunks(
    projectId: string,
    chunks: IndexedChunk[],
  ): Promise<void> {
    if (chunks.length === 0) {
      return;
    }

    const collection = await this.getCollection(projectId);
    for (let start = 0; start < chunks.length; start += 100) {
      const batch = chunks.slice(start, start + 100);
      await collection.upsert({
        ids: batch.map((chunk) => chunk.id),
        documents: batch.map((chunk) => chunk.text),
        metadatas: batch.map((chunk) => this.toMetadata(chunk)),
      });
    }
  }

  public async deleteProjectSourcePaths(
    projectId: string,
    sourcePaths: string[],
  ): Promise<void> {
    if (sourcePaths.length === 0) {
      return;
    }

    const ids = await this.getChunkIdsBySourcePaths(projectId, sourcePaths);
    if (ids.length === 0) {
      return;
    }

    const collection = await this.getCollection(projectId);
    for (let start = 0; start < ids.length; start += 250) {
      await collection.delete({
        ids: ids.slice(start, start + 250),
      });
    }
  }

  public async deleteProjectChunkIds(
    projectId: string,
    chunkIds: string[],
  ): Promise<void> {
    if (chunkIds.length === 0) {
      return;
    }

    const collection = await this.getCollection(projectId);
    for (let start = 0; start < chunkIds.length; start += 250) {
      await collection.delete({
        ids: chunkIds.slice(start, start + 250),
      });
    }
  }

  public async listProjectChunks(projectId: string): Promise<IndexedChunkRecord[]> {
    const collection = await this.getCollection(projectId);
    const snapshot = await collection.get({
      include: [IncludeEnum.documents, IncludeEnum.metadatas],
    });

    return (snapshot.ids ?? []).map((id, index) => {
      const metadata = snapshot.metadatas[index] as ChunkMetadata | null | undefined;
      return {
        id,
        projectId,
        sourcePath: metadata?.sourcePath ?? "unknown",
        sourceName: metadata?.sourceName ?? "unknown",
        kind: (metadata?.kind as "code" | "doc") ?? "doc",
        language: metadata?.language ?? "text",
        chunkIndex: metadata?.chunkIndex ?? index,
        startLine: metadata?.startLine,
        endLine: metadata?.endLine,
        text: snapshot.documents[index] ?? "",
        hash: metadata?.hash ?? "",
      } satisfies IndexedChunkRecord;
    });
  }

  public async queryProject(
    projectId: string,
    query: string,
    initialLimit: number,
  ): Promise<SearchHit[]> {
    const collection = await this.getCollection(projectId);
    const result = await collection.query<ChunkMetadata>({
      queryTexts: [query],
      nResults: initialLimit,
      include: [IncludeEnum.documents, IncludeEnum.metadatas, IncludeEnum.distances],
    });

    const ids = result.ids[0] ?? [];
    const docs = result.documents[0] ?? [];
    const metadatas = result.metadatas[0] ?? [];
    const distances = result.distances[0] ?? [];

    return ids.map((id, index) => {
      const metadata = metadatas[index];
      return {
        id,
        projectId,
        sourcePath: metadata?.sourcePath ?? "unknown",
        sourceName: metadata?.sourceName ?? "unknown",
        kind: (metadata?.kind as "code" | "doc") ?? "doc",
        language: metadata?.language ?? "text",
        chunkIndex: metadata?.chunkIndex ?? index,
        startLine: metadata?.startLine,
        endLine: metadata?.endLine,
        text: docs[index] ?? "",
        distance: distances[index],
        retrievalScore: this.distanceToScore(distances[index]),
        rerankScore: 0,
        finalScore: 0,
      } satisfies SearchHit;
    });
  }

  public async deleteProject(projectId: string): Promise<void> {
    await this.safeDeleteCollection(this.collectionName(projectId));
  }

  public async inspectProject(
    projectId: string,
    limit = 50,
  ): Promise<ProjectIndexSummary> {
    const collectionName = this.collectionName(projectId);
    const collection = await this.getCollection(projectId);
    const count = await collection.count();
    const peek = await collection.peek({ limit });

    return {
      projectId,
      collectionName,
      chunkCount: count,
      chunks: peek.ids.map((id, index) => {
        const metadata = peek.metadatas[index] as ChunkMetadata | null | undefined;
        return {
          id,
          sourcePath: metadata?.sourcePath ?? "unknown",
          sourceName: metadata?.sourceName ?? "unknown",
          kind: (metadata?.kind as "code" | "doc") ?? "doc",
          language: metadata?.language ?? "text",
          chunkIndex: metadata?.chunkIndex ?? index,
          startLine: metadata?.startLine,
          endLine: metadata?.endLine,
          text: peek.documents[index] ?? "",
          hash: metadata?.hash ?? "",
        };
      }),
    };
  }

  private async getCollection(projectId: string): Promise<Collection> {
    return this.client.getOrCreateCollection({
      name: this.collectionName(projectId),
      embeddingFunction: this.embedder,
    });
  }

  private collectionName(projectId: string): string {
    return `rag_project_${projectId}`;
  }

  private async safeDeleteCollection(name: string): Promise<void> {
    try {
      await this.client.deleteCollection({ name });
    } catch {
      // Ignore missing collection.
    }
  }

  private toMetadata(chunk: IndexedChunk): ChunkMetadata {
    return {
      projectId: chunk.projectId,
      sourcePath: chunk.sourcePath,
      sourceName: chunk.sourceName,
      kind: chunk.kind,
      language: chunk.language,
      chunkIndex: chunk.chunkIndex,
      startLine: chunk.startLine,
      endLine: chunk.endLine,
      hash: chunk.hash,
    };
  }

  private distanceToScore(distance?: number | null): number {
    if (distance === undefined || distance === null) {
      return 0;
    }

    return 1 / (1 + Math.max(distance, 0));
  }

  private async getChunkIdsBySourcePaths(
    projectId: string,
    sourcePaths: string[],
  ): Promise<string[]> {
    const chunkRecords = await this.listProjectChunks(projectId);
    const sourcePathSet = new Set(sourcePaths);
    return chunkRecords
      .filter((chunk) => sourcePathSet.has(chunk.sourcePath))
      .map((chunk) => chunk.id);
  }
}
