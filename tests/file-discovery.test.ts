import { mkdtempSync, mkdirSync, rmSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { tmpdir } from "node:os";
import { afterEach, describe, expect, it } from "vitest";
import { FileDiscoveryService } from "../src/services/file-discovery.js";
import { ProjectRegistration } from "../src/domain.js";

const tempDirs: string[] = [];

function createTempDir(): string {
  const dir = mkdtempSync(join(tmpdir(), "mcp-rag-discovery-"));
  tempDirs.push(dir);
  return dir;
}

afterEach(() => {
  for (const dir of tempDirs.splice(0)) {
    rmSync(dir, { recursive: true, force: true });
  }
});

function createProject(rootPath: string): ProjectRegistration {
  return {
    id: "demo",
    rootPath,
    includeGlobs: ["**/*"],
    excludeGlobs: ["dist/**", "node_modules/**"],
    excludePaths: [],
    excludeExtensions: [],
    maxFileSizeBytes: 8 * 1024 * 1024,
    createdAt: "2026-01-01T00:00:00.000Z",
    updatedAt: "2026-01-01T00:00:00.000Z",
  };
}

describe("FileDiscoveryService", () => {
  it("discovers stat-only entries without loading file content", () => {
    const root = createTempDir();
    mkdirSync(join(root, "src"), { recursive: true });
    writeFileSync(join(root, "src", "index.ts"), "export const value = 1;\n");

    const entries = new FileDiscoveryService().discoverEntries(createProject(root));

    expect(entries).toHaveLength(1);
    expect(entries[0]).toMatchObject({
      relativePath: "src/index.ts",
    });
    expect("content" in (entries[0] ?? {})).toBe(false);
    expect("contentHash" in (entries[0] ?? {})).toBe(false);
  });

  it("discovers code and docs while respecting .gitignore and excludes", () => {
    const root = createTempDir();
    mkdirSync(join(root, "src"), { recursive: true });
    mkdirSync(join(root, "dist"), { recursive: true });
    mkdirSync(join(root, "docs"), { recursive: true });

    writeFileSync(join(root, ".gitignore"), "ignored.md\n");
    writeFileSync(join(root, "src", "index.ts"), "export const value = 1;\n");
    writeFileSync(join(root, "docs", "guide.md"), "# Guide\n");
    writeFileSync(join(root, "ignored.md"), "# Ignored\n");
    writeFileSync(join(root, "dist", "bundle.js"), "compiled\n");
    writeFileSync(join(root, "notes.bin"), "binary-ish\n");

    const files = new FileDiscoveryService().discover(createProject(root));
    const relativePaths = files.map((file) => file.relativePath).sort();

    expect(relativePaths).toEqual(["docs/guide.md", "src/index.ts"]);
    expect(files.every((file) => typeof file.mtimeMs === "number" && file.mtimeMs > 0)).toBe(true);
    expect(files.every((file) => typeof file.size === "number" && file.size > 0)).toBe(true);
    expect(files.every((file) => typeof file.contentHash === "string" && file.contentHash.length > 0)).toBe(true);
  });

  it("skips excluded paths, extensions, and oversized files", () => {
    const root = createTempDir();
    mkdirSync(join(root, "src"), { recursive: true });
    mkdirSync(join(root, "generated"), { recursive: true });

    writeFileSync(join(root, "src", "keep.ts"), "export const keep = true;\n");
    writeFileSync(join(root, "src", "skip.log"), "skip\n");
    writeFileSync(join(root, "generated", "schema.ts"), "export const generated = true;\n");
    writeFileSync(join(root, "large.md"), "# ".padEnd(2048, "x"));

    const project = createProject(root);
    project.excludePaths = ["generated"];
    project.excludeExtensions = [".log"];
    project.maxFileSizeBytes = 1024;

    const files = new FileDiscoveryService().discover(project);

    expect(files.map((file) => file.relativePath)).toEqual(["src/keep.ts"]);
  });
});
