import { AppConfig } from "../config.js";
import { ContextBundle, SearchHit } from "../domain.js";
import { normalizeQuery } from "../utils.js";
import { ChromaProjectStore } from "./chroma-store.js";
import { NeuralReranker } from "./reranker.js";

export class ContextBuilderService {
  public constructor(
    private readonly config: AppConfig,
    private readonly store: ChromaProjectStore,
    private readonly reranker: NeuralReranker,
  ) {}

  public async search(
    projectId: string,
    query: string,
    options: { limit?: number; initialLimit?: number } = {},
  ): Promise<SearchHit[]> {
    const limit = options.limit ?? 6;
    const initialLimit = options.initialLimit ?? Math.min(Math.max(limit * 4, 16), 40);
    const retrievalHits = await this.store.queryProject(projectId, query, initialLimit);
    const reranked = await this.reranker.rerank(query, retrievalHits);
    return this.diversifyHits(reranked, limit);
  }

  public async build(projectId: string, query: string): Promise<ContextBundle> {
    const normalizedQuery = normalizeQuery(query);
    const hits = await this.search(projectId, normalizedQuery, {
      initialLimit: 12,
      limit: 6,
    });

    const blocks: string[] = [];
    let currentLength = 0;

    for (const hit of hits) {
      const location =
        hit.startLine && hit.endLine
          ? `${hit.sourcePath}:${hit.startLine}-${hit.endLine}`
          : hit.sourcePath;
      const block = [
        `### ${location} [${hit.kind}/${hit.language}] score=${hit.finalScore.toFixed(3)}`,
        hit.text.trim(),
      ].join("\n");

      if (currentLength + block.length > this.config.maxContextChars) {
        break;
      }

      blocks.push(block);
      currentLength += block.length;
    }

    const summary = this.summarize(projectId, normalizedQuery, hits);

    return {
      projectId,
      normalizedQuery,
      summary,
      contextText: [summary, ...blocks].join("\n\n"),
      hits,
    };
  }

  private summarize(projectId: string, query: string, hits: SearchHit[]): string {
    if (hits.length === 0) {
      return [
        `Project: ${projectId}`,
        `Intent: ${query}`,
        "No relevant indexed fragments were found. Reindex the project or reformulate the query.",
      ].join("\n");
    }

    const topFiles = [...new Set(hits.map((hit) => hit.sourcePath))].slice(0, 4);
    return [
      `Project: ${projectId}`,
      `Intent: ${query}`,
      `Top files: ${topFiles.join(", ")}`,
      "Selected fragments are already reranked and trimmed for direct use in the agent context.",
    ].join("\n");
  }

  private diversifyHits(hits: SearchHit[], limit: number): SearchHit[] {
    if (hits.length <= limit) {
      return hits;
    }

    const selected: SearchHit[] = [];
    const perFileCount = new Map<string, number>();
    const maxPerFile = Math.max(1, Math.ceil(limit / 3));

    for (const hit of hits) {
      const existingCount = perFileCount.get(hit.sourcePath) ?? 0;
      if (existingCount >= maxPerFile) {
        continue;
      }

      const alreadyHasNeighbor = selected.some(
        (selectedHit) =>
          selectedHit.sourcePath === hit.sourcePath &&
          Math.abs(selectedHit.chunkIndex - hit.chunkIndex) <= 1,
      );

      if (alreadyHasNeighbor && existingCount >= 1) {
        continue;
      }

      selected.push(hit);
      perFileCount.set(hit.sourcePath, existingCount + 1);

      if (selected.length === limit) {
        return selected;
      }
    }

    for (const hit of hits) {
      if (selected.some((selectedHit) => selectedHit.id === hit.id)) {
        continue;
      }

      selected.push(hit);
      if (selected.length === limit) {
        break;
      }
    }

    return selected;
  }
}
