import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { ContextBuilderService } from "../services/context-builder.js";
import { IndexingService } from "../services/indexer.js";
import { IndexDiagnosticsService } from "../services/index-diagnostics.js";
import { ProjectIndexStateCache } from "../services/project-index-state-cache.js";
import { ProjectOperationLock } from "../services/project-operation-lock.js";
import { ProjectRegistry } from "../services/project-registry.js";
import { ChromaProjectStore } from "../services/chroma-store.js";

export interface ServerDeps {
  registry: ProjectRegistry;
  indexer: IndexingService;
  contextBuilder: ContextBuilderService;
  store: ChromaProjectStore;
  indexStateCache: ProjectIndexStateCache;
  operationLock: ProjectOperationLock;
  indexDiagnostics: IndexDiagnosticsService;
}

export function createRagMcpServer(deps: ServerDeps): McpServer {
  const server = new McpServer({
    name: "mcp-project-rag",
    version: "0.1.0",
  });

  server.registerTool(
    "projects.list",
    {
      description: "List registered local projects available for indexing and retrieval.",
      inputSchema: {},
      outputSchema: {
        projects: z.array(
          z.object({
            id: z.string(),
            rootPath: z.string(),
            description: z.string().optional(),
            excludePaths: z.array(z.string()),
            excludeExtensions: z.array(z.string()),
            maxFileSizeBytes: z.number(),
            lastIndexedAt: z.string().optional(),
          }),
        ),
      },
    },
    async () => {
      const projects = deps.registry.list().map((project) => ({
        id: project.id,
        rootPath: project.rootPath,
        description: project.description,
        excludePaths: project.excludePaths,
        excludeExtensions: project.excludeExtensions,
        maxFileSizeBytes: project.maxFileSizeBytes,
        lastIndexedAt: project.lastIndexedAt,
      }));

      return {
        content: [{ type: "text", text: JSON.stringify(projects, null, 2) }],
        structuredContent: { projects },
      };
    },
  );

  server.registerTool(
    "projects.register",
    {
      description: "Register or update a local project root for future indexing.",
      inputSchema: {
        id: z.string().min(2),
        rootPath: z.string().min(1),
        description: z.string().optional(),
        includeGlobs: z.array(z.string()).optional(),
        excludeGlobs: z.array(z.string()).optional(),
        excludePaths: z.array(z.string()).optional(),
        excludeExtensions: z.array(z.string()).optional(),
        maxFileSizeBytes: z.number().int().positive().optional(),
      },
      outputSchema: {
        project: z.object({
          id: z.string(),
          rootPath: z.string(),
          description: z.string().optional(),
          excludePaths: z.array(z.string()),
          excludeExtensions: z.array(z.string()),
          maxFileSizeBytes: z.number(),
        }),
      },
    },
    async (input) => {
      const project = await deps.operationLock.runExclusive(input.id, () =>
        deps.registry.register(input),
      );
      return {
        content: [
          {
            type: "text",
            text: `Registered project ${project.id} at ${project.rootPath}`,
          },
        ],
        structuredContent: {
          project: {
            id: project.id,
            rootPath: project.rootPath,
            description: project.description,
            excludePaths: project.excludePaths,
            excludeExtensions: project.excludeExtensions,
            maxFileSizeBytes: project.maxFileSizeBytes,
          },
        },
      };
    },
  );

  server.registerTool(
    "projects.unregister",
    {
      description: "Remove a project registration and delete its Chroma collection.",
      inputSchema: {
        id: z.string().min(2),
      },
      outputSchema: {
        deleted: z.boolean(),
      },
    },
    async ({ id }) => {
      const deleted = await deps.operationLock.runExclusive(id, async () => {
        await deps.store.deleteProject(id);
        deps.indexStateCache.delete(id);
        return deps.registry.unregister(id);
      });
      return {
        content: [{ type: "text", text: deleted ? `Deleted ${id}` : `${id} not found` }],
        structuredContent: { deleted },
      };
    },
  );

  server.registerTool(
    "index.run",
    {
      description:
        "Scan the local project, split code and docs into chunks, and rebuild its Chroma index.",
      inputSchema: {
        projectId: z.string().min(2),
      },
      outputSchema: {
        projectId: z.string(),
        mode: z.enum(["full", "incremental"]).optional(),
        fileCount: z.number(),
        chunkCount: z.number(),
        indexedFileCount: z.number().optional(),
        changedFileCount: z.number().optional(),
        removedFileCount: z.number().optional(),
        unchangedFileCount: z.number().optional(),
        durationMs: z.number(),
        finishedAt: z.string(),
      },
    },
    async ({ projectId }) => {
      const stats = await deps.operationLock.runExclusive(projectId, () =>
        deps.indexer.run(projectId),
      );
      return {
        content: [
          {
            type: "text",
            text:
              stats.mode === "incremental"
                ? `Incrementally indexed ${stats.changedFileCount ?? 0} changed files, removed ${stats.removedFileCount ?? 0} files, total chunks=${stats.chunkCount} for ${projectId}`
                : `Indexed ${stats.fileCount} files into ${stats.chunkCount} chunks for ${projectId}`,
          },
        ],
        structuredContent: {
          projectId: stats.projectId,
          mode: stats.mode,
          fileCount: stats.fileCount,
          chunkCount: stats.chunkCount,
          indexedFileCount: stats.indexedFileCount,
          changedFileCount: stats.changedFileCount,
          removedFileCount: stats.removedFileCount,
          unchangedFileCount: stats.unchangedFileCount,
          durationMs: stats.durationMs,
          finishedAt: stats.finishedAt,
        },
      };
    },
  );

  server.registerTool(
    "search.query",
    {
      description:
        "Run semantic retrieval plus local neural rerank for a specific project without building a final context bundle.",
      inputSchema: {
        projectId: z.string().min(2),
        query: z.string().min(3),
        limit: z.number().int().positive().max(20).optional(),
      },
      outputSchema: {
        projectId: z.string(),
        query: z.string(),
        hits: z.array(
          z.object({
            sourcePath: z.string(),
            language: z.string(),
            kind: z.enum(["code", "doc"]),
            finalScore: z.number(),
            text: z.string(),
          }),
        ),
      },
    },
    async ({ projectId, query, limit }) => {
      const hits = await deps.contextBuilder.search(projectId, query, {
        limit,
        initialLimit: Math.max(limit ?? 6, 10),
      });

      return {
        content: [
          {
            type: "text",
            text: hits
              .map(
                (hit, index) =>
                  `${index + 1}. ${hit.sourcePath} score=${hit.finalScore.toFixed(3)}\n${hit.text}`,
              )
              .join("\n\n"),
          },
        ],
        structuredContent: {
          projectId,
          query,
          hits: hits.map((hit) => ({
            sourcePath: hit.sourcePath,
            language: hit.language,
            kind: hit.kind,
            finalScore: hit.finalScore,
            text: hit.text,
          })),
        },
      };
    },
  );

  server.registerTool(
    "context.build",
    {
      description:
        "Build a compact, ready-to-paste project context for the current engineering task.",
      inputSchema: {
        projectId: z.string().min(2),
        query: z.string().min(3),
      },
      outputSchema: {
        projectId: z.string(),
        normalizedQuery: z.string(),
        summary: z.string(),
        contextText: z.string(),
      },
    },
    async ({ projectId, query }) => {
      const bundle = await deps.contextBuilder.build(projectId, query);
      return {
        content: [{ type: "text", text: bundle.contextText }],
        structuredContent: {
          projectId: bundle.projectId,
          normalizedQuery: bundle.normalizedQuery,
          summary: bundle.summary,
          contextText: bundle.contextText,
        },
      };
    },
  );

  return server;
}
