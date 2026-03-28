import { describe, expect, it, vi } from "vitest";
import { IndexingService } from "../src/services/indexer.js";
import { IndexedChunk, ProjectRegistration } from "../src/domain.js";

function createProject(rootPath = "/repo"): ProjectRegistration {
  return {
    id: "demo",
    rootPath,
    includeGlobs: ["**/*"],
    excludeGlobs: [],
    excludePaths: [],
    excludeExtensions: [],
    maxFileSizeBytes: 8 * 1024 * 1024,
    createdAt: "2026-01-01T00:00:00.000Z",
    updatedAt: "2026-01-01T00:00:00.000Z",
  };
}

describe("IndexingService", () => {
  it("discovers files, chunks them, stores them, and updates registry stats", async () => {
    const project = createProject();
    const registry = {
      get: vi.fn(() => project),
      updateIndexStats: vi.fn(),
    };
    const discovery = {
      discoverEntries: vi.fn(() => [
        {
          absolutePath: "/repo/src/index.ts",
          relativePath: "src/index.ts",
          mtimeMs: 100,
          size: 23,
        },
      ]),
      readFile: vi.fn(() => ({
        absolutePath: "/repo/src/index.ts",
        relativePath: "src/index.ts",
        mtimeMs: 100,
        size: 23,
        contentHash: "file-hash-1",
        content: "export const value = 1;",
      })),
    };
    const chunks: IndexedChunk[] = [
      {
        id: "chunk-1",
        projectId: "demo",
        sourcePath: "src/index.ts",
        sourceName: "index.ts",
        kind: "code",
        language: "typescript",
        chunkIndex: 0,
        startLine: 1,
        endLine: 1,
        text: "export const value = 1;",
        hash: "hash-1",
      },
    ];
    const chunker = {
      chunkProjectFile: vi.fn(() => chunks),
    };
    const store = {
      replaceProjectChunks: vi.fn(async () => {}),
    };
    const stateCache = {
      get: vi.fn(() => undefined),
      set: vi.fn(),
    };

    const stats = await new IndexingService(
      registry as never,
      discovery as never,
      chunker as never,
      store as never,
      stateCache as never,
    ).run("demo");

    expect(discovery.discoverEntries).toHaveBeenCalledWith(project);
    expect(discovery.readFile).toHaveBeenCalledTimes(1);
    expect(chunker.chunkProjectFile).toHaveBeenCalledTimes(1);
    expect(store.replaceProjectChunks).toHaveBeenCalledWith("demo", chunks);
    expect(registry.updateIndexStats).toHaveBeenCalledTimes(1);
    expect(stateCache.set).toHaveBeenCalledTimes(1);
    expect(stateCache.set).toHaveBeenCalledWith("demo", {
      projectId: "demo",
      rootPath: "/repo",
      updatedAt: expect.any(String),
      files: {
        "src/index.ts": {
          relativePath: "src/index.ts",
          mtimeMs: 100,
          size: 23,
          contentHash: "file-hash-1",
          chunkIds: ["chunk-1"],
          chunkHashes: ["hash-1"],
        },
      },
    });
    expect(stats.projectId).toBe("demo");
    expect(stats.mode).toBe("full");
    expect(stats.fileCount).toBe(1);
    expect(stats.chunkCount).toBe(1);
  });

  it("incrementally updates only changed and removed files", async () => {
    const project = createProject();
    const registry = {
      get: vi.fn(() => project),
      updateIndexStats: vi.fn(),
    };
    const discovery = {
      discoverEntries: vi.fn(() => [
        {
          absolutePath: "/repo/src/changed.ts",
          relativePath: "src/changed.ts",
          mtimeMs: 200,
          size: 25,
        },
        {
          absolutePath: "/repo/src/unchanged.ts",
          relativePath: "src/unchanged.ts",
          mtimeMs: 100,
          size: 24,
        },
      ]),
      readFile: vi.fn((file: { relativePath: string }) => {
        if (file.relativePath === "src/changed.ts") {
          return {
            absolutePath: "/repo/src/changed.ts",
            relativePath: "src/changed.ts",
            mtimeMs: 200,
            size: 25,
            contentHash: "file-hash-changed-next",
            content: "export const changed = 2;",
          };
        }

        return {
          absolutePath: "/repo/src/unchanged.ts",
          relativePath: "src/unchanged.ts",
          mtimeMs: 100,
          size: 24,
          contentHash: "file-hash-stable",
          content: "export const stable = 1;",
        };
      }),
    };
    const chunker = {
      chunkProjectFile: vi.fn((_projectId: string, file: { relativePath: string }) => {
        if (file.relativePath === "src/changed.ts") {
          return [
            {
              id: "chunk-changed-next",
              projectId: "demo",
              sourcePath: "src/changed.ts",
              sourceName: "changed.ts",
              kind: "code" as const,
              language: "typescript",
              chunkIndex: 0,
              startLine: 1,
              endLine: 1,
              text: "export const changed = 2;",
              hash: "hash-changed-next",
            },
          ];
        }

        return [
          {
            id: "chunk-stable",
            projectId: "demo",
            sourcePath: "src/unchanged.ts",
            sourceName: "unchanged.ts",
            kind: "code" as const,
            language: "typescript",
            chunkIndex: 0,
            startLine: 1,
            endLine: 1,
            text: "export const stable = 1;",
            hash: "hash-stable",
          },
        ];
      }),
    };
    const store = {
      deleteProjectChunkIds: vi.fn(async () => {}),
      upsertProjectChunks: vi.fn(async () => {}),
    };
    const stateCache = {
      get: vi.fn(() => ({
        projectId: "demo",
        rootPath: "/repo",
        updatedAt: "2026-01-01T00:00:00.000Z",
        files: {
          "src/changed.ts": {
            relativePath: "src/changed.ts",
            mtimeMs: 100,
            size: 25,
            contentHash: "file-hash-changed-prev",
            chunkIds: ["chunk-changed-prev"],
            chunkHashes: ["hash-changed-prev"],
          },
          "src/unchanged.ts": {
            relativePath: "src/unchanged.ts",
            mtimeMs: 100,
            size: 24,
            contentHash: "file-hash-stable",
            chunkIds: ["chunk-stable"],
            chunkHashes: ["hash-stable"],
          },
          "src/removed.ts": {
            relativePath: "src/removed.ts",
            mtimeMs: 100,
            size: 30,
            contentHash: "file-hash-removed",
            chunkIds: ["chunk-removed"],
            chunkHashes: ["hash-removed"],
          },
        },
      })),
      set: vi.fn(),
    };

    const stats = await new IndexingService(
      registry as never,
      discovery as never,
      chunker as never,
      store as never,
      stateCache as never,
    ).run("demo");

    expect(discovery.readFile).toHaveBeenCalledTimes(2);
    expect(store.deleteProjectChunkIds).toHaveBeenCalledWith("demo", [
      "chunk-removed",
      "chunk-changed-prev",
    ]);
    expect(store.upsertProjectChunks).toHaveBeenCalledWith("demo", [
      {
        id: "chunk-changed-next",
        projectId: "demo",
        sourcePath: "src/changed.ts",
        sourceName: "changed.ts",
        kind: "code",
        language: "typescript",
        chunkIndex: 0,
        startLine: 1,
        endLine: 1,
        text: "export const changed = 2;",
        hash: "hash-changed-next",
      },
    ]);
    expect(stats.mode).toBe("incremental");
    expect(stats.changedFileCount).toBe(1);
    expect(stats.removedFileCount).toBe(1);
    expect(stats.unchangedFileCount).toBe(1);
    expect(stats.chunkCount).toBe(2);
    expect(stateCache.set).toHaveBeenCalledTimes(1);
    expect(stateCache.set).toHaveBeenCalledWith("demo", {
      projectId: "demo",
      rootPath: "/repo",
      updatedAt: expect.any(String),
      files: {
        "src/changed.ts": {
          relativePath: "src/changed.ts",
          mtimeMs: 200,
          size: 25,
          contentHash: "file-hash-changed-next",
          chunkIds: ["chunk-changed-next"],
          chunkHashes: ["hash-changed-next"],
        },
        "src/unchanged.ts": {
          relativePath: "src/unchanged.ts",
          mtimeMs: 100,
          size: 24,
          contentHash: "file-hash-stable",
          chunkIds: ["chunk-stable"],
          chunkHashes: ["hash-stable"],
        },
      },
    });
  });

  it("falls back to full rebuild when persisted state has no chunk manifest", async () => {
    const project = createProject();
    const registry = {
      get: vi.fn(() => project),
      updateIndexStats: vi.fn(),
    };
    const discovery = {
      discoverEntries: vi.fn(() => [
        {
          absolutePath: "/repo/src/index.ts",
          relativePath: "src/index.ts",
          mtimeMs: 100,
          size: 23,
        },
      ]),
      readFile: vi.fn(() => ({
        absolutePath: "/repo/src/index.ts",
        relativePath: "src/index.ts",
        mtimeMs: 100,
        size: 23,
        contentHash: "file-hash-1",
        content: "export const value = 1;",
      })),
    };
    const chunker = {
      chunkProjectFile: vi.fn(() => [
        {
          id: "chunk-1",
          projectId: "demo",
          sourcePath: "src/index.ts",
          sourceName: "index.ts",
          kind: "code" as const,
          language: "typescript",
          chunkIndex: 0,
          startLine: 1,
          endLine: 1,
          text: "export const value = 1;",
          hash: "hash-1",
        },
      ]),
    };
    const store = {
      replaceProjectChunks: vi.fn(async () => {}),
    };
    const stateCache = {
      get: vi.fn(() => ({
        projectId: "demo",
        rootPath: "/repo",
        updatedAt: "2026-01-01T00:00:00.000Z",
        files: {
          "src/index.ts": {
            relativePath: "src/index.ts",
            mtimeMs: 100,
            size: 23,
          },
        },
      })),
      set: vi.fn(),
    };

    const stats = await new IndexingService(
      registry as never,
      discovery as never,
      chunker as never,
      store as never,
      stateCache as never,
    ).run("demo");

    expect(store.replaceProjectChunks).toHaveBeenCalledTimes(1);
    expect(stats.mode).toBe("full");
  });

  it("reindexes when content hash changed even if mtime and size stayed the same", async () => {
    const project = createProject();
    const registry = {
      get: vi.fn(() => project),
      updateIndexStats: vi.fn(),
    };
    const discovery = {
      discoverEntries: vi.fn(() => [
        {
          absolutePath: "/repo/src/index.ts",
          relativePath: "src/index.ts",
          mtimeMs: 100,
          size: 23,
        },
      ]),
      readFile: vi.fn(() => ({
        absolutePath: "/repo/src/index.ts",
        relativePath: "src/index.ts",
        mtimeMs: 100,
        size: 23,
        contentHash: "file-hash-next",
        content: "export const value = 2;",
      })),
    };
    const chunker = {
      chunkProjectFile: vi.fn(() => [
        {
          id: "chunk-2",
          projectId: "demo",
          sourcePath: "src/index.ts",
          sourceName: "index.ts",
          kind: "code" as const,
          language: "typescript",
          chunkIndex: 0,
          startLine: 1,
          endLine: 1,
          text: "export const value = 2;",
          hash: "hash-2",
        },
      ]),
    };
    const store = {
      deleteProjectChunkIds: vi.fn(async () => {}),
      upsertProjectChunks: vi.fn(async () => {}),
    };
    const stateCache = {
      get: vi.fn(() => ({
        projectId: "demo",
        rootPath: "/repo",
        updatedAt: "2026-01-01T00:00:00.000Z",
        files: {
          "src/index.ts": {
            relativePath: "src/index.ts",
            mtimeMs: 100,
            size: 23,
            contentHash: "file-hash-prev",
            chunkIds: ["chunk-1"],
            chunkHashes: ["hash-1"],
          },
        },
      })),
      set: vi.fn(),
    };

    const stats = await new IndexingService(
      registry as never,
      discovery as never,
      chunker as never,
      store as never,
      stateCache as never,
    ).run("demo");

    expect(store.deleteProjectChunkIds).toHaveBeenCalledWith("demo", ["chunk-1"]);
    expect(store.upsertProjectChunks).toHaveBeenCalledTimes(1);
    expect(stats.mode).toBe("incremental");
    expect(stats.changedFileCount).toBe(1);
    expect(stats.unchangedFileCount).toBe(0);
  });

  it("recomputes content hashes for unchanged files during incremental indexing", async () => {
    const project = createProject();
    const registry = {
      get: vi.fn(() => project),
      updateIndexStats: vi.fn(),
    };
    const discovery = {
      discoverEntries: vi.fn(() => [
        {
          absolutePath: "/repo/src/unchanged.ts",
          relativePath: "src/unchanged.ts",
          mtimeMs: 100,
          size: 24,
        },
      ]),
      readFile: vi.fn(() => ({
        absolutePath: "/repo/src/unchanged.ts",
        relativePath: "src/unchanged.ts",
        mtimeMs: 100,
        size: 24,
        contentHash: "file-hash-stable",
        content: "export const stable = 1;",
      })),
    };
    const store = {
      deleteProjectChunkIds: vi.fn(async () => {}),
      upsertProjectChunks: vi.fn(async () => {}),
    };
    const stateCache = {
      get: vi.fn(() => ({
        projectId: "demo",
        rootPath: "/repo",
        updatedAt: "2026-01-01T00:00:00.000Z",
        files: {
          "src/unchanged.ts": {
            relativePath: "src/unchanged.ts",
            mtimeMs: 100,
            size: 24,
            contentHash: "file-hash-stable",
            chunkIds: ["chunk-stable"],
            chunkHashes: ["hash-stable"],
          },
        },
      })),
      set: vi.fn(),
    };

    const stats = await new IndexingService(
      registry as never,
      discovery as never,
      {} as never,
      store as never,
      stateCache as never,
    ).run("demo");

    expect(discovery.readFile).toHaveBeenCalledTimes(1);
    expect(store.deleteProjectChunkIds).not.toHaveBeenCalled();
    expect(store.upsertProjectChunks).not.toHaveBeenCalled();
    expect(stats.changedFileCount).toBe(0);
    expect(stats.unchangedFileCount).toBe(1);
  });

  it("throws when trying to index an unknown project", async () => {
    const registry = {
      get: vi.fn(() => undefined),
    };

    const service = new IndexingService(
      registry as never,
      {} as never,
      {} as never,
      {} as never,
      {} as never,
    );

    await expect(service.run("missing")).rejects.toThrow(
      "Project missing is not registered",
    );
  });
});
