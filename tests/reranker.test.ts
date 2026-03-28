import { describe, expect, it } from "vitest";
import { SearchHit } from "../src/domain.js";
import { NeuralReranker } from "../src/services/reranker.js";

describe("NeuralReranker", () => {
  it("boosts hits whose path and file name match the query intent", async () => {
    const embedder = {
      generateForQueries: async () => [[1, 0]],
      generate: async () => [
        [0.8, 0.2],
        [0.82, 0.18],
      ],
    };

    const reranker = new NeuralReranker(embedder as never);
    const hits: SearchHit[] = [
      {
        id: "1",
        projectId: "demo",
        sourcePath: "src/mcp/search-query.ts",
        sourceName: "search-query.ts",
        kind: "code",
        language: "typescript",
        chunkIndex: 0,
        text: "export function runSearchQueryTool() { return hybridResults; }",
        retrievalScore: 0.61,
        rerankScore: 0,
        finalScore: 0,
      },
      {
        id: "2",
        projectId: "demo",
        sourcePath: "src/utils/math.ts",
        sourceName: "math.ts",
        kind: "code",
        language: "typescript",
        chunkIndex: 0,
        text: "export function runSearchQueryTool() { return hybridResults; }",
        retrievalScore: 0.65,
        rerankScore: 0,
        finalScore: 0,
      },
    ];

    const results = await reranker.rerank("search query tool", hits);

    expect(results[0]?.id).toBe("1");
    expect(results[0]?.finalScore).toBeGreaterThan(results[1]?.finalScore ?? 0);
  });
});
