import { existsSync } from "node:fs";
import { resolve } from "node:path";
import { IndexRunStats, ProjectRegistration } from "../domain.js";
import { readJsonFile, writeJsonFileAtomic } from "../fs-utils.js";

interface RegistryPayload {
  projects: ProjectRegistration[];
}

const DEFAULT_EXCLUDES = [
  ".git/**",
  "node_modules/**",
  "dist/**",
  "build/**",
  "coverage/**",
  ".next/**",
  ".nuxt/**",
  ".venv/**",
  "venv/**",
  "__pycache__/**",
  ".pytest_cache/**",
  ".tmp/**",
  "data/**",
];

export const DEFAULT_MAX_FILE_SIZE_BYTES = 8 * 1024 * 1024;

function dedupe(values: readonly string[]): string[] {
  return [...new Set(values)];
}

function normalizeExcludePath(value: string): string {
  return value.trim().replaceAll("\\", "/").replace(/^\.\/+/u, "").replace(/\/+$/u, "");
}

function normalizeExtension(value: string): string {
  const trimmed = value.trim().toLowerCase();
  if (!trimmed) {
    return "";
  }

  return trimmed.startsWith(".") ? trimmed : `.${trimmed}`;
}

function normalizeProject(project: ProjectRegistration): ProjectRegistration {
  return {
    ...project,
    includeGlobs: dedupe(project.includeGlobs?.length ? project.includeGlobs : ["**/*"]),
    excludeGlobs: dedupe([...DEFAULT_EXCLUDES, ...(project.excludeGlobs ?? [])]),
    excludePaths: dedupe((project.excludePaths ?? []).map(normalizeExcludePath).filter(Boolean)),
    excludeExtensions: dedupe(
      (project.excludeExtensions ?? []).map(normalizeExtension).filter(Boolean),
    ),
    maxFileSizeBytes:
      typeof project.maxFileSizeBytes === "number" && Number.isFinite(project.maxFileSizeBytes)
        ? Math.max(1, Math.floor(project.maxFileSizeBytes))
        : DEFAULT_MAX_FILE_SIZE_BYTES,
  };
}

export class ProjectRegistry {
  public constructor(private readonly registryFile: string) {}

  public list(): ProjectRegistration[] {
    return this.read().projects;
  }

  public get(projectId: string): ProjectRegistration | undefined {
    return this.list().find((project) => project.id === projectId);
  }

  public register(input: {
    id: string;
    rootPath: string;
    description?: string;
    includeGlobs?: string[];
    excludeGlobs?: string[];
    excludePaths?: string[];
    excludeExtensions?: string[];
    maxFileSizeBytes?: number;
  }): ProjectRegistration {
    const payload = this.read();
    const rootPath = resolve(input.rootPath);
    const now = new Date().toISOString();
    const project = normalizeProject({
      id: input.id.trim(),
      rootPath,
      description: input.description?.trim() || undefined,
      includeGlobs: input.includeGlobs?.length ? input.includeGlobs : ["**/*"],
      excludeGlobs: input.excludeGlobs ?? [],
      excludePaths: input.excludePaths ?? [],
      excludeExtensions: input.excludeExtensions ?? [],
      maxFileSizeBytes: input.maxFileSizeBytes ?? DEFAULT_MAX_FILE_SIZE_BYTES,
      createdAt: now,
      updatedAt: now,
    });

    const existingIndex = payload.projects.findIndex(
      (existing) => existing.id === project.id,
    );

    if (existingIndex >= 0) {
      const existingProject = payload.projects[existingIndex]!;
      payload.projects[existingIndex] = {
        ...existingProject,
        ...project,
        createdAt: existingProject.createdAt,
      };
    } else {
      payload.projects.push(project);
    }

    this.write(payload);
    return this.get(project.id)!;
  }

  public update(
    projectId: string,
    input: {
      rootPath: string;
      description?: string;
      includeGlobs?: string[];
      excludeGlobs?: string[];
      excludePaths?: string[];
      excludeExtensions?: string[];
      maxFileSizeBytes?: number;
    },
  ): ProjectRegistration | undefined {
    const existingProject = this.get(projectId);
    if (!existingProject) {
      return undefined;
    }

    return this.register({
      id: existingProject.id,
      rootPath: input.rootPath,
      description: input.description,
      includeGlobs: input.includeGlobs ?? existingProject.includeGlobs,
      excludeGlobs:
        input.excludeGlobs ??
        existingProject.excludeGlobs.filter((pattern) => !DEFAULT_EXCLUDES.includes(pattern)),
      excludePaths: input.excludePaths ?? existingProject.excludePaths,
      excludeExtensions: input.excludeExtensions ?? existingProject.excludeExtensions,
      maxFileSizeBytes: input.maxFileSizeBytes ?? existingProject.maxFileSizeBytes,
    });
  }

  public unregister(projectId: string): boolean {
    const payload = this.read();
    const next = payload.projects.filter((project) => project.id !== projectId);
    const deleted = next.length !== payload.projects.length;
    if (deleted) {
      this.write({ projects: next });
    }
    return deleted;
  }

  public updateIndexStats(projectId: string, stats: IndexRunStats): void {
    const payload = this.read();
    const project = payload.projects.find((entry) => entry.id === projectId);

    if (!project) {
      throw new Error(`Project ${projectId} is not registered`);
    }

    project.lastIndexedAt = stats.finishedAt;
    project.lastIndexStats = stats;
    project.updatedAt = new Date().toISOString();
    this.write(payload);
  }

  private read(): RegistryPayload {
    if (!existsSync(this.registryFile)) {
      const emptyPayload: RegistryPayload = { projects: [] };
      this.write(emptyPayload);
      return emptyPayload;
    }

    const payload = readJsonFile<RegistryPayload>(this.registryFile);
    return {
      projects: (payload.projects ?? []).map((project) =>
        normalizeProject(project as ProjectRegistration),
      ),
    };
  }

  private write(payload: RegistryPayload): void {
    writeJsonFileAtomic(this.registryFile, payload);
  }
}
