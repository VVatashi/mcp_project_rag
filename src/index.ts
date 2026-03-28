import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { loadConfig } from "./config.js";
import { logger } from "./logger.js";
import { createRagMcpServer } from "./mcp/create-server.js";
import { ContextBuilderService } from "./services/context-builder.js";
import { ChunkingService } from "./services/chunker.js";
import { ChromaProjectStore } from "./services/chroma-store.js";
import { TransformersEmbeddingFunction } from "./services/embedder.js";
import { FileDiscoveryService } from "./services/file-discovery.js";
import { startHttpServer } from "./http/start-http-server.js";
import { IndexingService } from "./services/indexer.js";
import { IndexDiagnosticsService } from "./services/index-diagnostics.js";
import { ProjectIndexStateCache } from "./services/project-index-state-cache.js";
import { ProjectOperationLock } from "./services/project-operation-lock.js";
import { ProjectRegistry } from "./services/project-registry.js";
import { NeuralReranker } from "./services/reranker.js";

async function main(): Promise<void> {
  const config = loadConfig();
  const registry = new ProjectRegistry(config.registryFile);
  const embedder = new TransformersEmbeddingFunction(config);
  const store = new ChromaProjectStore(config, embedder);
  const reranker = new NeuralReranker(embedder);
  const contextBuilder = new ContextBuilderService(config, store, reranker);
  const indexStateCache = new ProjectIndexStateCache(config.indexStateDir);
  const operationLock = new ProjectOperationLock();
  const indexDiagnostics = new IndexDiagnosticsService();
  const indexer = new IndexingService(
    registry,
    new FileDiscoveryService(),
    new ChunkingService(),
    store,
    indexStateCache,
  );

  const deps = {
    registry,
    indexer,
    contextBuilder,
    store,
    indexStateCache,
    operationLock,
    indexDiagnostics,
  };

  if (config.transport === "stdio") {
    const transport = new StdioServerTransport();
    const server = createRagMcpServer(deps);
    await server.connect(transport);
    logger.info("MCP stdio transport is ready");
    return;
  }

  await startHttpServer(config, deps);
}

main().catch((error) => {
  logger.error({ error }, "Fatal startup error");
  process.exit(1);
});
