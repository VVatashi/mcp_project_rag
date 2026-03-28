import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { tmpdir } from "node:os";
import { afterEach, describe, expect, it } from "vitest";
import { loadConfig } from "../src/config.js";

const tempDirs: string[] = [];

function createTempDir(): string {
  const dir = mkdtempSync(join(tmpdir(), "mcp-rag-config-"));
  tempDirs.push(dir);
  return dir;
}

afterEach(() => {
  delete process.env.MCP_TRANSPORT;
  delete process.env.MCP_HTTP_PORT;
  delete process.env.MCP_DATA_DIR;
  delete process.env.CHROMA_URL;

  for (const dir of tempDirs.splice(0)) {
    rmSync(dir, { recursive: true, force: true });
  }
});

describe("loadConfig", () => {
  it("loads values from .env and keeps process.env overrides", () => {
    const root = createTempDir();
    writeFileSync(
      join(root, ".env"),
      [
        "MCP_TRANSPORT=http",
        "MCP_HTTP_PORT=4010",
        "MCP_DATA_DIR=./custom-data",
        "CHROMA_URL=http://127.0.0.1:9000",
      ].join("\n"),
    );

    process.env.MCP_HTTP_PORT = "4999";
    const config = loadConfig([], { cwd: root });
    expect(config.transport).toBe("http");
    expect(config.httpPort).toBe(4999);
    expect(config.chromaUrl).toBe("http://127.0.0.1:9000");
    expect(config.dataDir).toContain("custom-data");
    expect(config.indexStateDir).toContain("custom-data");
  });
});
