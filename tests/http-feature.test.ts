import { AddressInfo } from "node:net";
import { mkdtempSync, rmSync } from "node:fs";
import { join } from "node:path";
import { tmpdir } from "node:os";
import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { AppConfig } from "../src/config.js";
import { startHttpServer } from "../src/http/start-http-server.js";
import { IndexDiagnosticsService } from "../src/services/index-diagnostics.js";

let tempDir: string;

function parseSseOrJson(body: string, contentType: string | undefined) {
  if (contentType?.includes("text/event-stream")) {
    const dataLine = body
      .split(/\r?\n/u)
      .find((line) => line.startsWith("data: "));
    if (!dataLine) {
      throw new Error(`Missing SSE data line in body: ${body}`);
    }
    return JSON.parse(dataLine.slice(6)) as Record<string, unknown>;
  }

  return JSON.parse(body) as Record<string, unknown>;
}

async function httpRequest(baseUrl: string, init: {
  method?: string;
  path: string;
  headers?: Record<string, string>;
  body?: unknown;
}) {
  const response = await fetch(`${baseUrl}${init.path}`, {
    method: init.method ?? "GET",
    headers: init.headers,
    body: init.body ? JSON.stringify(init.body) : undefined,
  });

  return {
    status: response.status,
    headers: response.headers,
    body: await response.text(),
  };
}

async function rawHttpRequest(baseUrl: string, init: {
  method?: string;
  path: string;
  headers?: Record<string, string>;
  body?: string;
}) {
  const response = await fetch(`${baseUrl}${init.path}`, {
    method: init.method ?? "GET",
    headers: init.headers,
    body: init.body,
  });

  return {
    status: response.status,
    headers: response.headers,
    body: await response.text(),
  };
}

beforeEach(() => {
  tempDir = mkdtempSync(join(tmpdir(), "mcp-rag-http-"));
});

afterEach(() => {
  rmSync(tempDir, { recursive: true, force: true });
});

describe("HTTP MCP feature flow", () => {
  it("serves health, initialize, tools/list and tool calls over JSON-RPC", async () => {
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

    let inspectChunkCount = 0;
    let deletedProject = false;
    let projectId = "demo-project";
    let lastRegisteredInput:
      | {
          id: string;
          rootPath: string;
          description?: string;
          includeGlobs?: string[];
          excludeGlobs?: string[];
        }
      | undefined;
    let lastIndexedAt = "2026-01-01T00:00:00.000Z";

    const deps = {
      registry: {
        get: (requestedProjectId: string) =>
          requestedProjectId === projectId && !deletedProject
            ? {
                id: projectId,
                rootPath: lastRegisteredInput?.rootPath ?? "/workspace/demo",
                description: lastRegisteredInput?.description,
                includeGlobs: ["**/*"],
                excludeGlobs: [],
                createdAt: "2026-01-01T00:00:00.000Z",
                updatedAt: lastIndexedAt,
                lastIndexedAt,
              }
            : undefined,
        list: () =>
          deletedProject
            ? []
            : [
                {
                  id: projectId,
                  rootPath: lastRegisteredInput?.rootPath ?? "/workspace/demo",
                  description: lastRegisteredInput?.description,
                  includeGlobs: ["**/*"],
                  excludeGlobs: [],
                  createdAt: "2026-01-01T00:00:00.000Z",
                  updatedAt: lastIndexedAt,
                  lastIndexedAt,
                },
              ],
        register: (input: {
          id: string;
          rootPath: string;
          description?: string;
          includeGlobs?: string[];
          excludeGlobs?: string[];
        }) => {
          projectId = input.id;
          lastRegisteredInput = input;
          deletedProject = false;
          return {
            id: input.id,
            rootPath: input.rootPath,
            description: input.description,
            includeGlobs: input.includeGlobs ?? ["**/*"],
            excludeGlobs: input.excludeGlobs ?? [],
            createdAt: "2026-01-01T00:00:00.000Z",
            updatedAt: lastIndexedAt,
            lastIndexedAt,
          };
        },
        update: (requestedProjectId: string, input: {
          rootPath: string;
          description?: string;
        }) => ({
          id: requestedProjectId,
          rootPath: input.rootPath,
          description: input.description,
          includeGlobs: ["**/*"],
          excludeGlobs: [],
          createdAt: "2026-01-01T00:00:00.000Z",
          updatedAt: lastIndexedAt,
          lastIndexedAt,
        }),
        unregister: () => {
          deletedProject = true;
          return true;
        },
      },
      indexer: {
        run: async (projectId: string) => {
          inspectChunkCount = 5;
          lastIndexedAt = "2026-01-01T00:00:02.000Z";
          return {
            projectId,
            fileCount: 2,
            chunkCount: 5,
            startedAt: "2026-01-01T00:00:00.000Z",
            finishedAt: "2026-01-01T00:00:02.000Z",
            durationMs: 2000,
          };
        },
      },
      contextBuilder: {
        search: async () => [
          {
            sourcePath: "src/index.ts",
            language: "typescript",
            kind: "code",
            finalScore: 0.98,
            text: "export const answer = 42;",
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
        deleteProject: async () => {
          inspectChunkCount = 0;
        },
        inspectProject: async (projectId: string) => ({
          projectId,
          collectionName: `rag_project_${projectId}`,
          chunkCount: inspectChunkCount,
          chunks: [],
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
      const health = await httpRequest(baseUrl, { path: "/health" });
      expect(health.status).toBe(200);
      expect(health.body).toContain('"ok":true');

      const ui = await httpRequest(baseUrl, { path: "/ui" });
      expect(ui.status).toBe(200);
      expect(ui.body).toContain("MCP Project RAG");

      const uiTraversal = await httpRequest(baseUrl, { path: "/ui/../../README.md" });
      expect(uiTraversal.status).toBe(404);

      const projectsApi = await httpRequest(baseUrl, { path: "/api/projects" });
      expect(projectsApi.status).toBe(200);
      const projectsApiJson = JSON.parse(projectsApi.body) as {
        projects: Array<{ id: string; index: { chunkCount: number } }>;
      };
      expect(projectsApiJson.projects[0]?.id).toBe("demo-project");
      expect(projectsApiJson.projects[0]?.index.chunkCount).toBe(0);

      const projectApi = await httpRequest(baseUrl, { path: "/api/projects/demo-project" });
      expect(projectApi.status).toBe(200);
      const projectApiJson = JSON.parse(projectApi.body) as {
        project: { id: string };
        index: { collectionName: string };
      };
      expect(projectApiJson.project.id).toBe("demo-project");
      expect(projectApiJson.index.collectionName).toBe("rag_project_demo-project");

      const createApi = await httpRequest(baseUrl, {
        method: "POST",
        path: "/api/projects",
        headers: { "content-type": "application/json", accept: "application/json" },
        body: {
          id: "fresh-project",
          rootPath: "/workspace/fresh",
          description: "fresh registration",
        },
      });
      expect(createApi.status).toBe(201);
      const createApiJson = JSON.parse(createApi.body) as {
        project: { id: string; rootPath: string; description?: string };
      };
      expect(createApiJson.project.id).toBe("fresh-project");
      expect(createApiJson.project.rootPath).toBe("/workspace/fresh");
      expect(createApiJson.project.description).toBe("fresh registration");

      const updateApi = await httpRequest(baseUrl, {
        method: "PUT",
        path: "/api/projects/fresh-project",
        headers: { "content-type": "application/json", accept: "application/json" },
        body: {
          rootPath: "/workspace/updated",
          description: "updated project",
        },
      });
      expect(updateApi.status).toBe(200);
      const updateApiJson = JSON.parse(updateApi.body) as {
        project: { rootPath: string; description?: string };
      };
      expect(updateApiJson.project.rootPath).toBe("/workspace/updated");
      expect(updateApiJson.project.description).toBe("updated project");

      const reindexApi = await httpRequest(baseUrl, {
        method: "POST",
        path: "/api/projects/fresh-project/reindex",
        headers: { "content-type": "application/json", accept: "application/json" },
        body: {},
      });
      expect(reindexApi.status).toBe(200);
      const reindexApiJson = JSON.parse(reindexApi.body) as {
        stats: { chunkCount: number };
        index: { chunkCount: number };
      };
      expect(reindexApiJson.stats.chunkCount).toBe(5);
      expect(reindexApiJson.index.chunkCount).toBe(5);

      const searchApi = await httpRequest(baseUrl, {
        method: "POST",
        path: "/api/projects/fresh-project/search",
        headers: { "content-type": "application/json", accept: "application/json" },
        body: {
          query: "where is answer declared",
          limit: 1,
        },
      });
      expect(searchApi.status).toBe(200);
      const searchApiJson = JSON.parse(searchApi.body) as {
        projectId: string;
        hits: Array<{ sourcePath: string }>;
      };
      expect(searchApiJson.projectId).toBe("fresh-project");
      expect(searchApiJson.hits[0]?.sourcePath).toBe("src/index.ts");

      const contextApi = await httpRequest(baseUrl, {
        method: "POST",
        path: "/api/projects/fresh-project/context",
        headers: { "content-type": "application/json", accept: "application/json" },
        body: {
          query: "where is answer declared",
        },
      });
      expect(contextApi.status).toBe(200);
      const contextApiJson = JSON.parse(contextApi.body) as {
        projectId: string;
        contextText: string;
      };
      expect(contextApiJson.projectId).toBe("fresh-project");
      expect(contextApiJson.contextText).toBe("context");

      const invalidJson = await rawHttpRequest(baseUrl, {
        method: "POST",
        path: "/api/projects/fresh-project/search",
        headers: { "content-type": "application/json", accept: "application/json" },
        body: "{ invalid json",
      });
      expect(invalidJson.status).toBe(400);
      expect(invalidJson.body).toContain("valid JSON");

      const invalidCreate = await httpRequest(baseUrl, {
        method: "POST",
        path: "/api/projects",
        headers: { "content-type": "application/json", accept: "application/json" },
        body: {
          id: "x",
          rootPath: "",
        },
      });
      expect(invalidCreate.status).toBe(400);
      expect(invalidCreate.body).toContain("Project id must contain at least 2 characters");

      const invalidSearch = await httpRequest(baseUrl, {
        method: "POST",
        path: "/api/projects/fresh-project/search",
        headers: { "content-type": "application/json", accept: "application/json" },
        body: {
          query: "ok query",
          limit: "oops",
        },
      });
      expect(invalidSearch.status).toBe(400);
      expect(invalidSearch.body).toContain("expected number");

      const deleteApi = await httpRequest(baseUrl, {
        method: "DELETE",
        path: "/api/projects/fresh-project",
        headers: { accept: "application/json" },
      });
      expect(deleteApi.status).toBe(200);
      const deleteApiJson = JSON.parse(deleteApi.body) as {
        deleted: boolean;
      };
      expect(deleteApiJson.deleted).toBe(true);

      const invalidAccept = await httpRequest(baseUrl, {
        method: "POST",
        path: "/mcp",
        headers: { "content-type": "application/json", accept: "application/json" },
        body: {
          jsonrpc: "2.0",
          id: 1,
          method: "initialize",
          params: {
            protocolVersion: "2025-11-05",
            capabilities: {},
            clientInfo: { name: "feature-test", version: "0.1.0" },
          },
        },
      });
      expect(invalidAccept.status).toBe(406);

      const initialize = await httpRequest(baseUrl, {
        method: "POST",
        path: "/mcp",
        headers: {
          "content-type": "application/json",
          accept: "application/json, text/event-stream",
        },
        body: {
          jsonrpc: "2.0",
          id: 2,
          method: "initialize",
          params: {
            protocolVersion: "2025-11-05",
            capabilities: {},
            clientInfo: { name: "feature-test", version: "0.1.0" },
          },
        },
      });

      expect(initialize.status).toBe(200);
      const sessionId = initialize.headers.get("mcp-session-id");
      expect(sessionId).toBeTruthy();

      const initializeJson = parseSseOrJson(
        initialize.body,
        initialize.headers.get("content-type") ?? undefined,
      );
      expect(initializeJson.result).toBeTruthy();

      const toolsList = await httpRequest(baseUrl, {
        method: "POST",
        path: "/mcp",
        headers: {
          "content-type": "application/json",
          accept: "application/json, text/event-stream",
          "mcp-session-id": sessionId!,
        },
        body: {
          jsonrpc: "2.0",
          id: 3,
          method: "tools/list",
          params: {},
        },
      });

      const toolsJson = parseSseOrJson(
        toolsList.body,
        toolsList.headers.get("content-type") ?? undefined,
      );
      const toolNames = ((toolsJson.result as { tools?: Array<{ name: string }> })?.tools ?? []).map(
        (tool) => tool.name,
      );
      expect(toolNames).toContain("projects.list");
      expect(toolNames).toContain("index.run");
      expect(toolNames).toContain("context.build");

      const toolCall = await httpRequest(baseUrl, {
        method: "POST",
        path: "/mcp",
        headers: {
          "content-type": "application/json",
          accept: "application/json, text/event-stream",
          "mcp-session-id": sessionId!,
        },
        body: {
          jsonrpc: "2.0",
          id: 4,
          method: "tools/call",
          params: {
            name: "search.query",
            arguments: {
              projectId: "demo-project",
              query: "where is answer declared",
              limit: 1,
            },
          },
        },
      });

      const toolCallJson = parseSseOrJson(
        toolCall.body,
        toolCall.headers.get("content-type") ?? undefined,
      );
      const hits = ((toolCallJson.result as { structuredContent?: { hits?: Array<{ sourcePath: string }> } })
        ?.structuredContent?.hits ?? []);
      expect(hits[0]?.sourcePath).toBe("src/index.ts");
    } finally {
      await new Promise<void>((resolve, reject) => {
        server.close((error) => (error ? reject(error) : resolve()));
      });
    }
  });

  it("surfaces index inspection errors instead of faking an empty index", async () => {
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
          projectId === "demo-project"
            ? {
                id: "demo-project",
                rootPath: "/workspace/demo",
                includeGlobs: ["**/*"],
                excludeGlobs: [],
                createdAt: "2026-01-01T00:00:00.000Z",
                updatedAt: "2026-01-01T00:00:00.000Z",
              }
            : undefined,
        list: () => [
          {
            id: "demo-project",
            rootPath: "/workspace/demo",
            includeGlobs: ["**/*"],
            excludeGlobs: [],
            createdAt: "2026-01-01T00:00:00.000Z",
            updatedAt: "2026-01-01T00:00:00.000Z",
          },
        ],
      },
      indexer: {
        run: async () => ({
          projectId: "demo-project",
          fileCount: 0,
          chunkCount: 0,
          startedAt: "2026-01-01T00:00:00.000Z",
          finishedAt: "2026-01-01T00:00:01.000Z",
          durationMs: 1000,
        }),
      },
      contextBuilder: {
        search: async () => [],
        build: async () => ({
          projectId: "demo-project",
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
      const projectsApi = await httpRequest(baseUrl, { path: "/api/projects" });
      expect(projectsApi.status).toBe(200);
      expect(projectsApi.body).toContain('"index":null');
      expect(projectsApi.body).toContain("Chroma is unavailable");
      expect(projectsApi.body).toContain('"indexErrorAt":"');

      const projectApi = await httpRequest(baseUrl, { path: "/api/projects/demo-project" });
      expect(projectApi.status).toBe(200);
      expect(projectApi.body).toContain('"index":null');
      expect(projectApi.body).toContain("Chroma is unavailable");
      expect(projectApi.body).toContain('"diagnostics":{"inspect"');

      const invalidNestedRoute = await httpRequest(baseUrl, {
        method: "POST",
        path: "/api/projects/demo-project/search/extra",
        headers: { "content-type": "application/json", accept: "application/json" },
        body: { query: "where is answer declared" },
      });
      expect(invalidNestedRoute.status).toBe(404);
    } finally {
      await new Promise<void>((resolve, reject) => {
        server.close((error) => (error ? reject(error) : resolve()));
      });
    }
  });

  it("records reindex failures with diagnostics and returns a meaningful 500", async () => {
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

    const diagnostics = new IndexDiagnosticsService();
    const deps = {
      registry: {
        get: (projectId: string) =>
          projectId === "demo-project"
            ? {
                id: "demo-project",
                rootPath: "/workspace/demo",
                includeGlobs: ["**/*"],
                excludeGlobs: [],
                createdAt: "2026-01-01T00:00:00.000Z",
                updatedAt: "2026-01-01T00:00:00.000Z",
              }
            : undefined,
        list: () => [
          {
            id: "demo-project",
            rootPath: "/workspace/demo",
            includeGlobs: ["**/*"],
            excludeGlobs: [],
            createdAt: "2026-01-01T00:00:00.000Z",
            updatedAt: "2026-01-01T00:00:00.000Z",
          },
        ],
      },
      indexer: {
        run: async () => {
          throw new Error("Filesystem scan failed");
        },
      },
      contextBuilder: {
        search: async () => [],
        build: async () => ({
          projectId: "demo-project",
          normalizedQuery: "query",
          summary: "summary",
          contextText: "context",
          hits: [],
        }),
      },
      store: {
        deleteProject: async () => {},
        inspectProject: async () => ({
          projectId: "demo-project",
          collectionName: "rag_project_demo-project",
          chunkCount: 0,
          chunks: [],
        }),
      },
      indexStateCache: {
        delete: () => {},
      },
      operationLock: {
        runExclusive: async (_projectId: string, task: () => Promise<unknown> | unknown) =>
          await task(),
      },
      indexDiagnostics: diagnostics,
    };

    const server = await startHttpServer(config, deps as never);
    const address = server.address() as AddressInfo;
    const baseUrl = `http://127.0.0.1:${address.port}`;

    try {
      const reindexApi = await httpRequest(baseUrl, {
        method: "POST",
        path: "/api/projects/demo-project/reindex",
        headers: { "content-type": "application/json", accept: "application/json" },
        body: {},
      });
      expect(reindexApi.status).toBe(500);
      expect(reindexApi.body).toContain("Reindex failed: Filesystem scan failed");

      const projectApi = await httpRequest(baseUrl, { path: "/api/projects/demo-project" });
      expect(projectApi.status).toBe(200);
      expect(projectApi.body).toContain("Filesystem scan failed");
      expect(projectApi.body).toContain('"reindex"');
    } finally {
      await new Promise<void>((resolve, reject) => {
        server.close((error) => (error ? reject(error) : resolve()));
      });
    }
  });
});
