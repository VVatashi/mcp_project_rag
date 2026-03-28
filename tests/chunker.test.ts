import { describe, expect, it } from "vitest";
import { ChunkingService } from "../src/services/chunker.js";

describe("ChunkingService", () => {
  it("creates multiple code chunks with line ranges", () => {
    const chunker = new ChunkingService();
    const file = {
      absolutePath: "C:/repo/src/example.ts",
      relativePath: "src/example.ts",
      mtimeMs: 123,
      size: 1024,
      contentHash: "file-hash",
      content: Array.from(
        { length: 140 },
        (_, index) => `const line${index} = ${index};`,
      ).join("\n"),
    };

    const chunks = chunker.chunkProjectFile("demo", file);

    expect(chunks.length).toBeGreaterThan(1);
    expect(chunks[0]?.startLine).toBe(1);
    expect(chunks[0]?.endLine).toBe(80);
    expect(chunks[1]?.startLine).toBe(61);
  });
});
