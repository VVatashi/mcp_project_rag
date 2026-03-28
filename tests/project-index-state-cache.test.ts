import { mkdtempSync, readdirSync, rmSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { tmpdir } from "node:os";
import { afterEach, describe, expect, it } from "vitest";
import { ProjectIndexStateCache } from "../src/services/project-index-state-cache.js";

const tempDirs: string[] = [];

function createTempDir(): string {
  const dir = mkdtempSync(join(tmpdir(), "mcp-rag-index-state-"));
  tempDirs.push(dir);
  return dir;
}

afterEach(() => {
  for (const dir of tempDirs.splice(0)) {
    rmSync(dir, { recursive: true, force: true });
  }
});

describe("ProjectIndexStateCache", () => {
  it("moves corrupted state aside and returns undefined", () => {
    const root = createTempDir();
    const cache = new ProjectIndexStateCache(root);
    const stateFile = join(root, "demo.json");
    writeFileSync(stateFile, "{ broken json", "utf8");

    const result = cache.get("demo");

    expect(result).toBeUndefined();
    expect(readdirSync(root).some((name) => name.startsWith("demo.json.corrupt-"))).toBe(true);
  });
});
