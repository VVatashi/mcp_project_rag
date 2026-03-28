import { existsSync, mkdirSync, readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { z } from "zod";

const configSchema = z.object({
  transport: z.enum(["stdio", "http"]).default("stdio"),
  httpHost: z.string().default("127.0.0.1"),
  httpPort: z.coerce.number().int().positive().default(3123),
  httpPath: z.string().default("/mcp"),
  dataDir: z.string().default("./data"),
  chromaUrl: z.string().url().default("http://127.0.0.1:8000"),
  embeddingModel: z.string().default("Xenova/all-MiniLM-L6-v2"),
  rerankModel: z.string().default("Xenova/all-MiniLM-L6-v2"),
  maxContextChars: z.coerce.number().int().positive().default(12000),
});

export interface AppConfig {
  transport: "stdio" | "http";
  httpHost: string;
  httpPort: number;
  httpPath: string;
  dataDir: string;
  registryFile: string;
  indexStateDir: string;
  modelCacheDir: string;
  chromaUrl: string;
  embeddingModel: string;
  rerankModel: string;
  maxContextChars: number;
}

export function ensureParentDirectory(filePath: string): void {
  mkdirSync(dirname(filePath), { recursive: true });
}

function loadDotEnvFile(filePath: string): void {
  if (!existsSync(filePath)) {
    return;
  }

  const raw = readFileSync(filePath, "utf8");

  for (const line of raw.split(/\r?\n/u)) {
    const trimmed = line.trim();
    if (!trimmed || trimmed.startsWith("#")) {
      continue;
    }

    const separatorIndex = trimmed.indexOf("=");
    if (separatorIndex <= 0) {
      continue;
    }

    const key = trimmed.slice(0, separatorIndex).trim();
    if (!key || process.env[key] !== undefined) {
      continue;
    }

    let value = trimmed.slice(separatorIndex + 1).trim();
    if (
      (value.startsWith('"') && value.endsWith('"')) ||
      (value.startsWith("'") && value.endsWith("'"))
    ) {
      value = value.slice(1, -1);
    }

    process.env[key] = value;
  }
}

export function loadConfig(
  argv = process.argv.slice(2),
  options: { cwd?: string } = {},
): AppConfig {
  const cwd = options.cwd ?? process.cwd();
  loadDotEnvFile(resolve(cwd, ".env"));

  const transportOverride = argv
    .find((arg) => arg.startsWith("--transport="))
    ?.split("=")[1];

  const parsed = configSchema.parse({
    transport: transportOverride ?? process.env.MCP_TRANSPORT,
    httpHost: process.env.MCP_HTTP_HOST,
    httpPort: process.env.MCP_HTTP_PORT,
    httpPath: process.env.MCP_HTTP_PATH,
    dataDir: process.env.MCP_DATA_DIR,
    chromaUrl: process.env.CHROMA_URL,
    embeddingModel: process.env.EMBEDDING_MODEL,
    rerankModel: process.env.RERANK_MODEL,
    maxContextChars: process.env.MAX_CONTEXT_CHARS,
  });

  const dataDir = resolve(cwd, parsed.dataDir);
  const indexStateDir = resolve(dataDir, "index-state");
  const modelCacheDir = resolve(dataDir, "models");
  mkdirSync(dataDir, { recursive: true });
  mkdirSync(indexStateDir, { recursive: true });
  mkdirSync(modelCacheDir, { recursive: true });

  return {
    ...parsed,
    dataDir,
    registryFile: resolve(dataDir, "projects.json"),
    indexStateDir,
    modelCacheDir,
  };
}
