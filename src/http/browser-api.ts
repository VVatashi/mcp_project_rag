import { IncomingMessage, ServerResponse } from "node:http";
import { ServerDeps } from "../mcp/create-server.js";
import { ProjectOperationConflictError } from "../services/project-operation-lock.js";
import {
  contextRequestSchema,
  createProjectRequestSchema,
  parseBrowserApiBody,
  searchRequestSchema,
  updateProjectRequestSchema,
} from "./browser-api-schemas.js";
import { HttpError } from "./http-error.js";
import { readJsonBody, sendJson } from "./http-utils.js";

export async function handleBrowserApiRequest(
  req: IncomingMessage,
  res: ServerResponse,
  deps: ServerDeps,
  pathname: string,
): Promise<boolean> {
  const route = parseBrowserApiRoute(pathname, req.method ?? "GET");
  if (!route) {
    return false;
  }

  if (route.kind === "projects-list") {
    await handleProjectsApi(deps, res);
    return true;
  }

  if (route.kind === "projects-create") {
    await handleProjectCreateApi(deps, req, res);
    return true;
  }

  if (route.kind === "projects-search") {
    await handleProjectSearchApi(deps, route.projectId, req, res);
    return true;
  }

  if (route.kind === "projects-context") {
    await handleProjectContextApi(deps, route.projectId, req, res);
    return true;
  }

  if (route.kind === "projects-reindex") {
    await handleProjectReindexApi(deps, route.projectId, res);
    return true;
  }

  if (route.kind === "projects-update") {
    await handleProjectUpdateApi(deps, route.projectId, req, res);
    return true;
  }

  if (route.kind === "projects-delete") {
    await handleProjectDeleteApi(deps, route.projectId, res);
    return true;
  }

  if (route.kind === "projects-get") {
    await handleProjectIndexApi(deps, route.projectId, res);
    return true;
  }

  return true;
}

async function handleProjectsApi(
  deps: ServerDeps,
  res: ServerResponse,
): Promise<void> {
  const projects = await Promise.all(
    deps.registry.list().map(async (project) => {
      const inspection = await inspectProjectSummary(deps, project.id, 5);
      const diagnostics = deps.indexDiagnostics.get(project.id);

      return {
        ...project,
        index: inspection.index
          ? {
              collectionName: inspection.index.collectionName,
              chunkCount: inspection.index.chunkCount,
              previewChunkCount: inspection.index.chunks.length,
            }
          : null,
        indexError: inspection.indexError,
        indexErrorAt: inspection.indexErrorAt,
        diagnostics,
      };
    }),
  );

  sendJson(res, 200, { projects });
}

async function handleProjectIndexApi(
  deps: ServerDeps,
  projectId: string,
  res: ServerResponse,
): Promise<void> {
  const project = deps.registry.get(projectId);
  if (!project) {
    sendJson(res, 404, { error: "Project not found" });
    return;
  }

  const inspection = await inspectProjectSummary(deps, projectId, 100);
  const diagnostics = deps.indexDiagnostics.get(projectId);

  sendJson(res, 200, {
    project,
    index: inspection.index,
    indexError: inspection.indexError,
    indexErrorAt: inspection.indexErrorAt,
    diagnostics,
  });
}

async function handleProjectCreateApi(
  deps: ServerDeps,
  req: IncomingMessage,
  res: ServerResponse,
): Promise<void> {
  const body = parseBrowserApiBody(createProjectRequestSchema, await readJsonBody(req));
  const {
    id,
    rootPath,
    description,
    includeGlobs,
    excludeGlobs,
    excludePaths,
    excludeExtensions,
    maxFileSizeBytes,
  } = body;
  const created = await runProjectMutation(deps, id, async () => {
    const project = deps.registry.register({
      id,
      rootPath,
      description,
      includeGlobs,
      excludeGlobs,
      excludePaths,
      excludeExtensions,
      maxFileSizeBytes,
    });
    const inspection = await inspectProjectSummary(deps, project.id, 5);
    const diagnostics = deps.indexDiagnostics.get(project.id);

    return {
      project,
      index: inspection.index,
      indexError: inspection.indexError,
      indexErrorAt: inspection.indexErrorAt,
      diagnostics,
    };
  });

  sendJson(res, 201, created);
}

async function handleProjectUpdateApi(
  deps: ServerDeps,
  projectId: string,
  req: IncomingMessage,
  res: ServerResponse,
): Promise<void> {
  const existingProject = deps.registry.get(projectId);
  if (!existingProject) {
    sendJson(res, 404, { error: "Project not found" });
    return;
  }

  const body = parseBrowserApiBody(updateProjectRequestSchema, await readJsonBody(req));
  const {
    rootPath,
    description,
    includeGlobs,
    excludeGlobs,
    excludePaths,
    excludeExtensions,
    maxFileSizeBytes,
  } = body;

  const updated = await runProjectMutation(deps, projectId, async () => {
    const project = deps.registry.update(projectId, {
      rootPath,
      description,
      includeGlobs,
      excludeGlobs,
      excludePaths,
      excludeExtensions,
      maxFileSizeBytes,
    });
    if (!project) {
      throw new HttpError(404, "Project not found");
    }

    const inspection = await inspectProjectSummary(deps, project.id, 100);
    const diagnostics = deps.indexDiagnostics.get(project.id);

    return {
      project,
      index: inspection.index,
      indexError: inspection.indexError,
      indexErrorAt: inspection.indexErrorAt,
      diagnostics,
    };
  });

  sendJson(res, 200, updated);
}

async function handleProjectDeleteApi(
  deps: ServerDeps,
  projectId: string,
  res: ServerResponse,
): Promise<void> {
  const project = deps.registry.get(projectId);
  if (!project) {
    sendJson(res, 404, { error: "Project not found" });
    return;
  }

  await runProjectMutation(deps, projectId, async () => {
    await deps.store.deleteProject(projectId);
    deps.indexStateCache.delete(projectId);
    deps.indexDiagnostics.clear(projectId);
    deps.registry.unregister(projectId);
  });
  sendJson(res, 200, { deleted: true, projectId });
}

async function handleProjectSearchApi(
  deps: ServerDeps,
  projectId: string,
  req: IncomingMessage,
  res: ServerResponse,
): Promise<void> {
  const project = deps.registry.get(projectId);
  if (!project) {
    sendJson(res, 404, { error: "Project not found" });
    return;
  }

  const body = parseBrowserApiBody(searchRequestSchema, await readJsonBody(req));
  const { query, limit } = body;

  const hits = await deps.contextBuilder.search(projectId, query, {
    limit,
    initialLimit: Math.max(limit, 10),
  });

  sendJson(res, 200, {
    projectId,
    query,
    hits,
  });
}

async function handleProjectContextApi(
  deps: ServerDeps,
  projectId: string,
  req: IncomingMessage,
  res: ServerResponse,
): Promise<void> {
  const project = deps.registry.get(projectId);
  if (!project) {
    sendJson(res, 404, { error: "Project not found" });
    return;
  }

  const body = parseBrowserApiBody(contextRequestSchema, await readJsonBody(req));
  const { query } = body;

  const bundle = await deps.contextBuilder.build(projectId, query);

  sendJson(res, 200, bundle);
}

async function handleProjectReindexApi(
  deps: ServerDeps,
  projectId: string,
  res: ServerResponse,
): Promise<void> {
  const project = deps.registry.get(projectId);
  if (!project) {
    sendJson(res, 404, { error: "Project not found" });
    return;
  }

  const result = await runProjectMutation(deps, projectId, async () => {
    let stats;
    try {
      stats = await deps.indexer.run(projectId);
      deps.indexDiagnostics.clear(projectId, "reindex");
    } catch (error) {
      const message = error instanceof Error ? error.message : "Unknown reindex error";
      deps.indexDiagnostics.record(projectId, "reindex", message);
      throw new HttpError(500, `Reindex failed: ${message}`);
    }
    const updatedProject = deps.registry.get(projectId) ?? project;
    const inspection = await inspectProjectSummary(deps, projectId, 100);
    const diagnostics = deps.indexDiagnostics.get(projectId);

    return {
      project: updatedProject,
      index: inspection.index,
      indexError: inspection.indexError,
      indexErrorAt: inspection.indexErrorAt,
      diagnostics,
      stats,
    };
  });

  sendJson(res, 200, result);
}

async function runProjectMutation<T>(
  deps: ServerDeps,
  projectId: string,
  task: () => Promise<T>,
): Promise<T> {
  try {
    return await deps.operationLock.runExclusive(projectId, task);
  } catch (error) {
    if (error instanceof ProjectOperationConflictError) {
      throw new HttpError(409, error.message);
    }

    throw error;
  }
}

type BrowserApiRoute =
  | { kind: "projects-list" }
  | { kind: "projects-create" }
  | { kind: "projects-get"; projectId: string }
  | { kind: "projects-update"; projectId: string }
  | { kind: "projects-delete"; projectId: string }
  | { kind: "projects-search"; projectId: string }
  | { kind: "projects-context"; projectId: string }
  | { kind: "projects-reindex"; projectId: string };

function parseBrowserApiRoute(pathname: string, method: string): BrowserApiRoute | undefined {
  if (pathname === "/api/projects") {
    if (method === "GET") {
      return { kind: "projects-list" };
    }

    if (method === "POST") {
      return { kind: "projects-create" };
    }

    return undefined;
  }

  if (!pathname.startsWith("/api/projects/")) {
    return undefined;
  }

  const suffix = pathname.slice("/api/projects/".length);
  const segments = suffix.split("/");
  const [encodedProjectId, action, ...rest] = segments;
  if (!encodedProjectId || rest.length > 0) {
    return undefined;
  }

  const projectId = decodeURIComponent(encodedProjectId);
  if (!projectId) {
    return undefined;
  }

  if (!action) {
    if (method === "GET") {
      return { kind: "projects-get", projectId };
    }

    if (method === "PUT") {
      return { kind: "projects-update", projectId };
    }

    if (method === "DELETE") {
      return { kind: "projects-delete", projectId };
    }

    return undefined;
  }

  if (method !== "POST") {
    return undefined;
  }

  if (action === "search") {
    return { kind: "projects-search", projectId };
  }

  if (action === "context") {
    return { kind: "projects-context", projectId };
  }

  if (action === "reindex") {
    return { kind: "projects-reindex", projectId };
  }

  return undefined;
}

async function inspectProjectSummary(
  deps: ServerDeps,
  projectId: string,
  previewLimit: number,
): Promise<{
  index:
    | {
        projectId: string;
        collectionName: string;
        chunkCount: number;
        chunks: unknown[];
      }
    | null;
  indexError?: string;
  indexErrorAt?: string;
}> {
  try {
    const index = await deps.store.inspectProject(projectId, previewLimit);
    deps.indexDiagnostics.clear(projectId, "inspect");
    return { index };
  } catch (error) {
    const message = error instanceof Error ? error.message : "Failed to inspect project index";
    const entry = deps.indexDiagnostics.record(projectId, "inspect", message);
    return {
      index: null,
      indexError: message,
      indexErrorAt: entry.observedAt,
    };
  }
}
