import { describe, expect, it } from "vitest";
import { ContextBuilderService } from "../src/services/context-builder.js";
import { SearchHit } from "../src/domain.js";

describe("ContextBuilderService", () => {
  it("builds a compact context bundle from reranked hits", async () => {
    const store = {
      queryProject: async (): Promise<SearchHit[]> => [
        {
          id: "1",
          projectId: "demo",
          sourcePath: "src/index.ts",
          sourceName: "index.ts",
          kind: "code",
          language: "typescript",
          chunkIndex: 0,
          startLine: 1,
          endLine: 4,
          text: "export const value = 1;",
          retrievalScore: 0.9,
          rerankScore: 0,
          finalScore: 0,
        },
      ],
    };

    const reranker = {
      rerank: async (_query: string, hits: SearchHit[]) =>
        hits.map((hit) => ({ ...hit, rerankScore: 0.95, finalScore: 0.93 })),
    };

    const service = new ContextBuilderService(
      {
        transport: "stdio",
        httpHost: "127.0.0.1",
        httpPort: 3123,
        httpPath: "/mcp",
        dataDir: ".",
        registryFile: "projects.json",
        indexStateDir: "index-state",
        modelCacheDir: "models",
        chromaUrl: "http://127.0.0.1:8000",
        embeddingModel: "demo",
        rerankModel: "demo",
        maxContextChars: 5000,
      },
      store as never,
      reranker as never,
    );

    const bundle = await service.build("demo", "where is value declared");

    expect(bundle.summary).toContain("Project: demo");
    expect(bundle.contextText).toContain("src/index.ts:1-4");
    expect(bundle.hits).toHaveLength(1);
  });

  it("keeps top results diverse across files when many neighbor chunks compete", async () => {
    const store = {
      queryProject: async (): Promise<SearchHit[]> =>
        Array.from({ length: 5 }, (_, index) => ({
          id: String(index + 1),
          projectId: "demo",
          sourcePath: index < 4 ? "src/alpha.ts" : "docs/beta.md",
          sourceName: index < 4 ? "alpha.ts" : "beta.md",
          kind: index < 4 ? ("code" as const) : ("doc" as const),
          language: index < 4 ? "typescript" : "markdown",
          chunkIndex: index < 4 ? index : 0,
          startLine: index < 4 ? index * 20 + 1 : undefined,
          endLine: index < 4 ? index * 20 + 20 : undefined,
          text: `chunk-${index}`,
          retrievalScore: 0.95 - index * 0.05,
          rerankScore: 0,
          finalScore: 0,
        })),
    };

    const reranker = {
      rerank: async (_query: string, hits: SearchHit[]) =>
        hits.map((hit, index) => ({
          ...hit,
          rerankScore: 0.99 - index * 0.01,
          finalScore: 0.99 - index * 0.01,
        })),
    };

    const service = new ContextBuilderService(
      {
        transport: "stdio",
        httpHost: "127.0.0.1",
        httpPort: 3123,
        httpPath: "/mcp",
        dataDir: ".",
        registryFile: "projects.json",
        indexStateDir: "index-state",
        modelCacheDir: "models",
        chromaUrl: "http://127.0.0.1:8000",
        embeddingModel: "demo",
        rerankModel: "demo",
        maxContextChars: 5000,
      },
      store as never,
      reranker as never,
    );

    const hits = await service.search("demo", "alpha chunk", { limit: 3, initialLimit: 5 });

    expect(hits).toHaveLength(3);
    expect(hits.some((hit) => hit.sourcePath === "docs/beta.md")).toBe(true);
    expect(new Set(hits.map((hit) => hit.sourcePath)).size).toBeGreaterThan(1);
    expect(hits[0]?.sourcePath).toBe("src/alpha.ts");
  });

  it("prioritizes documentation hits for architecture-style queries", async () => {
    const store = {
      queryProject: async (): Promise<SearchHit[]> => [
        {
          id: "1",
          projectId: "demo",
          sourcePath: "src/index.ts",
          sourceName: "index.ts",
          kind: "code",
          language: "typescript",
          chunkIndex: 0,
          startLine: 1,
          endLine: 12,
          text: "export async function main() { return startHttpServer(); }",
          retrievalScore: 0.95,
          rerankScore: 0,
          finalScore: 0,
        },
        {
          id: "2",
          projectId: "demo",
          sourcePath: "docs/01-overview.md",
          sourceName: "01-overview.md",
          kind: "doc",
          language: "markdown",
          chunkIndex: 0,
          text: "Architecture overview of the runtime, browser API, and RAG pipeline.",
          retrievalScore: 0.8,
          rerankScore: 0,
          finalScore: 0,
        },
      ],
    };

    const reranker = {
      rerank: async (query: string, hits: SearchHit[]) => {
        const { NeuralReranker } = await import("../src/services/reranker.js");
        const embedder = {
          generateForQueries: async () => [[1, 0]],
          generate: async () => [[1, 0], [1, 0]],
        };
        return new NeuralReranker(embedder as never).rerank(query, hits);
      },
    };

    const service = new ContextBuilderService(
      {
        transport: "stdio",
        httpHost: "127.0.0.1",
        httpPort: 3123,
        httpPath: "/mcp",
        dataDir: ".",
        registryFile: "projects.json",
        indexStateDir: "index-state",
        modelCacheDir: "models",
        chromaUrl: "http://127.0.0.1:8000",
        embeddingModel: "demo",
        rerankModel: "demo",
        maxContextChars: 5000,
      },
      store as never,
      reranker as never,
    );

    const hits = await service.search("demo", "architecture overview of browser api docs", {
      limit: 2,
      initialLimit: 2,
    });

    expect(hits[0]?.kind).toBe("doc");
    expect(hits[0]?.sourcePath).toBe("docs/01-overview.md");
  });
});
