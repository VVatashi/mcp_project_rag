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
  }): ProjectRegistration {
    const payload = this.read();
    const rootPath = resolve(input.rootPath);
    const now = new Date().toISOString();
    const project: ProjectRegistration = {
      id: input.id.trim(),
      rootPath,
      description: input.description?.trim() || undefined,
      includeGlobs: input.includeGlobs?.length ? input.includeGlobs : ["**/*"],
      excludeGlobs: [...DEFAULT_EXCLUDES, ...(input.excludeGlobs ?? [])],
      createdAt: now,
      updatedAt: now,
    };

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
      includeGlobs: existingProject.includeGlobs,
      excludeGlobs: existingProject.excludeGlobs.filter(
        (pattern) => !DEFAULT_EXCLUDES.includes(pattern),
      ),
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

    return readJsonFile<RegistryPayload>(this.registryFile);
  }

  private write(payload: RegistryPayload): void {
    writeJsonFileAtomic(this.registryFile, payload);
  }
}
