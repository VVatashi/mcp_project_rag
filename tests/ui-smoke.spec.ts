import { expect, test } from "@playwright/test";
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import type { AddressInfo } from "node:net";
import type { AppConfig } from "../src/config.js";
import { startHttpServer } from "../src/http/start-http-server.js";
import { IndexDiagnosticsService } from "../src/services/index-diagnostics.js";

type ProjectRecord = {
  id: string;
  rootPath: string;
  description?: string;
  includeGlobs: string[];
  excludeGlobs: string[];
  createdAt: string;
  updatedAt: string;
  lastIndexedAt?: string;
  lastIndexStats?: {
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
  };
};

test("browser ui smoke flow", async ({ page }) => {
  const tempDir = mkdtempSync(join(tmpdir(), "mcp-rag-ui-smoke-"));
  const now = "2026-01-01T00:00:00.000Z";
  const projects = new Map<string, ProjectRecord>([
    [
      "demo-project",
      {
        id: "demo-project",
        rootPath: "/workspace/demo",
        description: "demo project",
        includeGlobs: ["**/*"],
        excludeGlobs: [],
        createdAt: now,
        updatedAt: now,
      },
    ],
  ]);
  const chunkCounts = new Map<string, number>([["demo-project", 0]]);
  const previewChunks = new Map<string, Array<Record<string, unknown>>>([
    ["demo-project", []],
  ]);

  const config: AppConfig = {
    transport: "http",
    httpHost: "127.0.0.1",
    httpPort: 0,
    httpPath: "/mcp",
    dataDir: tempDir,
    registryFile: join(tempDir, "projects.json"),
    indexStateDir: join(tempDir, "index-state"),
    modelCacheDir: join(tempDir, "models"),
    chromaUrl: "http://127.0.0.1:8000",
    embeddingModel: "demo",
    rerankModel: "demo",
    maxContextChars: 5000,
  };

  const registry = {
    get: (projectId: string) => projects.get(projectId),
    list: () => [...projects.values()],
    register: (input: {
      id: string;
      rootPath: string;
      description?: string;
      includeGlobs?: string[];
      excludeGlobs?: string[];
    }) => {
      const existing = projects.get(input.id);
      const project: ProjectRecord = {
        id: input.id,
        rootPath: input.rootPath,
        description: input.description,
        includeGlobs: input.includeGlobs ?? existing?.includeGlobs ?? ["**/*"],
        excludeGlobs: input.excludeGlobs ?? existing?.excludeGlobs ?? [],
        createdAt: existing?.createdAt ?? now,
        updatedAt: "2026-01-01T00:00:01.000Z",
        lastIndexedAt: existing?.lastIndexedAt,
        lastIndexStats: existing?.lastIndexStats,
      };
      projects.set(input.id, project);
      chunkCounts.set(input.id, chunkCounts.get(input.id) ?? 0);
      previewChunks.set(input.id, previewChunks.get(input.id) ?? []);
      return project;
    },
    update: (projectId: string, input: { rootPath: string; description?: string }) => {
      const existing = projects.get(projectId);
      if (!existing) {
        return undefined;
      }

      const updated: ProjectRecord = {
        ...existing,
        rootPath: input.rootPath,
        description: input.description,
        updatedAt: "2026-01-01T00:00:02.000Z",
      };
      projects.set(projectId, updated);
      return updated;
    },
    unregister: (projectId: string) => {
      chunkCounts.delete(projectId);
      previewChunks.delete(projectId);
      return projects.delete(projectId);
    },
    updateIndexStats: (projectId: string, stats: ProjectRecord["lastIndexStats"]) => {
      const existing = projects.get(projectId);
      if (!existing) {
        throw new Error(`Project ${projectId} is not registered`);
      }

      projects.set(projectId, {
        ...existing,
        updatedAt: stats?.finishedAt ?? existing.updatedAt,
        lastIndexedAt: stats?.finishedAt,
        lastIndexStats: stats,
      });
    },
  };

  const deps = {
    registry,
    indexer: {
      run: async (projectId: string) => {
        const previousChunkCount = chunkCounts.get(projectId) ?? 0;
        const mode = previousChunkCount === 0 ? "full" : "incremental";
        const stats = {
          projectId,
          mode,
          fileCount: 2,
          chunkCount: 5,
          indexedFileCount: mode === "full" ? 2 : 1,
          changedFileCount: mode === "full" ? 2 : 1,
          removedFileCount: 0,
          unchangedFileCount: mode === "full" ? 0 : 1,
          startedAt: "2026-01-01T00:00:00.000Z",
          finishedAt: mode === "full" ? "2026-01-01T00:00:03.000Z" : "2026-01-01T00:00:05.000Z",
          durationMs: mode === "full" ? 3000 : 1200,
        } as const;

        chunkCounts.set(projectId, 5);
        previewChunks.set(projectId, [
          {
            id: "chunk-1",
            sourcePath: "src/index.ts",
            sourceName: "index.ts",
            kind: "code",
            language: "typescript",
            chunkIndex: 0,
            startLine: 1,
            endLine: 1,
            text: "export const answer = 42;",
            hash: "hash-1",
          },
        ]);
        registry.updateIndexStats(projectId, stats);
        return stats;
      },
    },
    contextBuilder: {
      search: async (projectId: string, query: string) => [
        {
          projectId,
          id: "hit-1",
          sourcePath: "src/index.ts",
          sourceName: "index.ts",
          language: "typescript",
          kind: "code" as const,
          chunkIndex: 0,
          startLine: 1,
          endLine: 1,
          text: `search hit for ${query}`,
          retrievalScore: 0.9,
          rerankScore: 0.95,
          finalScore: 0.98,
        },
      ],
      build: async (projectId: string, query: string) => ({
        projectId,
        normalizedQuery: query,
        summary: "summary",
        contextText: "context",
        hits: [],
      }),
    },
    store: {
      deleteProject: async (projectId: string) => {
        chunkCounts.set(projectId, 0);
        previewChunks.set(projectId, []);
      },
      inspectProject: async (projectId: string) => ({
        projectId,
        collectionName: `rag_project_${projectId}`,
        chunkCount: chunkCounts.get(projectId) ?? 0,
        chunks: previewChunks.get(projectId) ?? [],
      }),
    },
    indexStateCache: {
      delete: () => {},
    },
    operationLock: {
      runExclusive: async (_projectId: string, task: () => Promise<unknown> | unknown) =>
        await task(),
    },
    indexDiagnostics: new IndexDiagnosticsService(),
  };

  const server = await startHttpServer(config, deps as never);
  const address = server.address() as AddressInfo;
  const baseUrl = `http://127.0.0.1:${address.port}`;

  try {
    await page.goto(`${baseUrl}/ui`);

    await expect(page.getByRole("heading", { name: "MCP Project RAG" })).toBeVisible();
    await expect(page.locator("#projects-list .project-name").first()).toContainText("demo-project");

    await page.locator("#create-project-id").fill("fresh-project");
    await page.locator("#create-project-root").fill("/workspace/fresh");
    await page.locator("#create-project-description").fill("fresh registration");
    await page.locator("#create-project-button").click();

    await expect(page.locator("#create-project-status")).toContainText("fresh-project");
    await expect(page.locator("#project-title")).toContainText("fresh-project");

    await page.locator("#project-root-input").fill("/workspace/updated");
    await page.locator("#project-description-input").fill("updated project");
    await page.locator("#save-project").click();

    await expect(page.locator("#action-feedback")).toContainText("updated successfully");
    await expect(page.locator("#project-root-input")).toHaveValue("/workspace/updated");

    await page.locator("#reindex-project").click();
    await expect(page.locator("#action-feedback")).toContainText("Reindex completed");
    await expect(page.locator("#chunk-count")).toContainText("5");

    await page.locator("#query-input").fill("where is answer declared");
    await page.locator("#submit-query").click();
    await expect(page.locator("#query-result")).toContainText("src/index.ts");

    page.once("dialog", (dialog) => dialog.accept());
    await page.locator("#delete-project").click();
    await expect(page.locator("#projects-list")).toContainText("demo-project");
    await expect(page.locator("#projects-list")).not.toContainText("fresh-project");
    await expect(page.locator("#project-title")).toContainText("demo-project");
  } finally {
    await new Promise<void>((resolve, reject) => {
      server.close((error) => (error ? reject(error) : resolve()));
    });
    rmSync(tempDir, { recursive: true, force: true });
  }
});

test("shows index warning in the project list when index inspection fails", async ({ page }) => {
  const tempDir = mkdtempSync(join(tmpdir(), "mcp-rag-ui-warning-"));
  const now = "2026-01-01T00:00:00.000Z";
  const config: AppConfig = {
    transport: "http",
    httpHost: "127.0.0.1",
    httpPort: 0,
    httpPath: "/mcp",
    dataDir: tempDir,
    registryFile: join(tempDir, "projects.json"),
    indexStateDir: join(tempDir, "index-state"),
    modelCacheDir: join(tempDir, "models"),
    chromaUrl: "http://127.0.0.1:8000",
    embeddingModel: "demo",
    rerankModel: "demo",
    maxContextChars: 5000,
  };

  const deps = {
    registry: {
      get: (projectId: string) =>
        projectId === "broken-project"
          ? {
              id: "broken-project",
              rootPath: "/workspace/broken",
              description: "broken index",
              includeGlobs: ["**/*"],
              excludeGlobs: [],
              createdAt: now,
              updatedAt: now,
            }
          : undefined,
      list: () => [
        {
          id: "broken-project",
          rootPath: "/workspace/broken",
          description: "broken index",
          includeGlobs: ["**/*"],
          excludeGlobs: [],
          createdAt: now,
          updatedAt: now,
        },
      ],
    },
    indexer: {
      run: async () => {
        throw new Error("not used");
      },
    },
    contextBuilder: {
      search: async () => [],
      build: async () => ({
        projectId: "broken-project",
        normalizedQuery: "query",
        summary: "summary",
        contextText: "context",
        hits: [],
      }),
    },
    store: {
      deleteProject: async () => {},
      inspectProject: async () => {
        throw new Error("Chroma is unavailable");
      },
    },
    indexStateCache: {
      delete: () => {},
    },
    operationLock: {
      runExclusive: async (_projectId: string, task: () => Promise<unknown> | unknown) =>
        await task(),
    },
    indexDiagnostics: new IndexDiagnosticsService(),
  };

  const server = await startHttpServer(config, deps as never);
  const address = server.address() as AddressInfo;
  const baseUrl = `http://127.0.0.1:${address.port}`;

  try {
    await page.goto(`${baseUrl}/ui`);

    await expect(page.locator("#projects-list .project-alert")).toContainText("Index issue");
    await expect(page.locator("#index-status")).toContainText("Chroma is unavailable");
    await expect(page.locator("#index-status")).toContainText("observed");
  } finally {
    await new Promise<void>((resolve, reject) => {
      server.close((error) => (error ? reject(error) : resolve()));
    });
    rmSync(tempDir, { recursive: true, force: true });
  }
});
