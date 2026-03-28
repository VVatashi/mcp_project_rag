import { mkdtempSync, readFileSync, rmSync } from "node:fs";
import { join } from "node:path";
import { tmpdir } from "node:os";
import { afterEach, describe, expect, it } from "vitest";
import {
  DEFAULT_MAX_FILE_SIZE_BYTES,
  ProjectRegistry,
} from "../src/services/project-registry.js";

const tempDirs: string[] = [];

function createTempDir(): string {
  const dir = mkdtempSync(join(tmpdir(), "mcp-rag-registry-"));
  tempDirs.push(dir);
  return dir;
}

afterEach(() => {
  for (const dir of tempDirs.splice(0)) {
    rmSync(dir, { recursive: true, force: true });
  }
});

describe("ProjectRegistry", () => {
  it("registers and updates a project while preserving createdAt", () => {
    const root = createTempDir();
    const registryFile = join(root, "data", "projects.json");
    const registry = new ProjectRegistry(registryFile);

    const first = registry.register({
      id: "demo",
      rootPath: join(root, "project-a"),
      description: "first version",
    });
    const second = registry.register({
      id: "demo",
      rootPath: join(root, "project-b"),
      description: "second version",
      excludeGlobs: ["custom/**"],
    });

    expect(first.id).toBe("demo");
    expect(second.rootPath).toContain("project-b");
    expect(second.description).toBe("second version");
    expect(second.createdAt).toBe(first.createdAt);
    expect(second.excludeGlobs).toContain("custom/**");
    expect(second.excludeGlobs).toContain("node_modules/**");
    expect(second.excludePaths).toEqual([]);
    expect(second.excludeExtensions).toEqual([]);
    expect(second.maxFileSizeBytes).toBe(DEFAULT_MAX_FILE_SIZE_BYTES);

    const stored = JSON.parse(readFileSync(registryFile, "utf8"));
    expect(stored.projects).toHaveLength(1);
  });

  it("updates a project without duplicating default excludes", () => {
    const root = createTempDir();
    const registryFile = join(root, "data", "projects.json");
    const registry = new ProjectRegistry(registryFile);

    registry.register({
      id: "demo",
      rootPath: join(root, "project-a"),
      description: "first version",
      excludeGlobs: ["custom/**"],
    });

    const updated = registry.update("demo", {
      rootPath: join(root, "project-b"),
      description: "second version",
    });

    expect(updated?.rootPath).toContain("project-b");
    expect(updated?.description).toBe("second version");
    expect(updated?.excludeGlobs.filter((pattern) => pattern === "node_modules/**")).toHaveLength(1);
    expect(updated?.excludeGlobs).toContain("custom/**");
  });

  it("stores per-project exclude paths, extensions, and max file size", () => {
    const root = createTempDir();
    const registry = new ProjectRegistry(join(root, "projects.json"));

    registry.register({
      id: "demo",
      rootPath: join(root, "project-a"),
      excludePaths: ["dist", "./coverage/", "docs\\generated"],
      excludeExtensions: ["log", ".tmp", "  .cache  "],
      maxFileSizeBytes: 1024,
    });

    const updated = registry.update("demo", {
      rootPath: join(root, "project-b"),
      description: "second version",
      excludePaths: ["artifacts", "reports/daily.md"],
      excludeExtensions: [".bin"],
      maxFileSizeBytes: 2048,
    });

    expect(updated?.excludePaths).toEqual(["artifacts", "reports/daily.md"]);
    expect(updated?.excludeExtensions).toEqual([".bin"]);
    expect(updated?.maxFileSizeBytes).toBe(2048);
  });

  it("updates index stats and unregisters a project", () => {
    const root = createTempDir();
    const registry = new ProjectRegistry(join(root, "projects.json"));
    registry.register({ id: "demo", rootPath: root });

    registry.updateIndexStats("demo", {
      projectId: "demo",
      fileCount: 3,
      chunkCount: 8,
      startedAt: "2026-01-01T00:00:00.000Z",
      finishedAt: "2026-01-01T00:00:03.000Z",
      durationMs: 3000,
    });

    const project = registry.get("demo");
    expect(project?.lastIndexedAt).toBe("2026-01-01T00:00:03.000Z");
    expect(project?.lastIndexStats?.chunkCount).toBe(8);

    expect(registry.unregister("demo")).toBe(true);
    expect(registry.get("demo")).toBeUndefined();
  });
});
