import { createServer, IncomingMessage, Server, ServerResponse } from "node:http";
import { randomUUID } from "node:crypto";
import { resolve } from "node:path";
import { StreamableHTTPServerTransport } from "@modelcontextprotocol/sdk/server/streamableHttp.js";
import { isInitializeRequest } from "@modelcontextprotocol/sdk/types.js";
import { AppConfig } from "../config.js";
import { logger } from "../logger.js";
import { createRagMcpServer, ServerDeps } from "../mcp/create-server.js";
import { handleBrowserApiRequest } from "./browser-api.js";
import { HttpError } from "./http-error.js";
import { readJsonBody, sendJson } from "./http-utils.js";
import { serveStaticFile, serveUiAsset } from "./static-files.js";

interface SessionState {
  transport: StreamableHTTPServerTransport;
}

export async function startHttpServer(
  config: AppConfig,
  deps: ServerDeps,
): Promise<Server> {
  const sessions = new Map<string, SessionState>();

  const server = createServer(async (req, res) => {
    try {
      await routeRequest(config, deps, sessions, req, res);
    } catch (error) {
      if (error instanceof HttpError) {
        sendJson(res, error.statusCode, { error: error.message });
        return;
      }

      logger.error({ error }, "Unhandled HTTP server error");
      if (!res.headersSent) {
        res.statusCode = 500;
        res.setHeader("content-type", "application/json");
        res.end(
          JSON.stringify({
            jsonrpc: "2.0",
            error: { code: -32603, message: "Internal server error" },
            id: null,
          }),
        );
      }
    }
  });

  await new Promise<void>((resolvePromise, rejectPromise) => {
    server.once("error", rejectPromise);
    server.listen(config.httpPort, config.httpHost, () => {
      logger.info(
        { host: config.httpHost, port: config.httpPort, path: config.httpPath },
        "MCP HTTP transport is listening",
      );
      resolvePromise();
    });
  });

  return server;
}

async function routeRequest(
  config: AppConfig,
  deps: ServerDeps,
  sessions: Map<string, SessionState>,
  req: IncomingMessage,
  res: ServerResponse,
): Promise<void> {
  const requestUrl = new URL(req.url ?? "/", `http://${req.headers.host ?? "localhost"}`);

  if (requestUrl.pathname === "/health") {
    res.statusCode = 200;
    res.setHeader("content-type", "application/json");
    res.end(JSON.stringify({ ok: true }));
    return;
  }

  if (requestUrl.pathname === "/" || requestUrl.pathname === "/ui") {
    await serveStaticFile(resolve("public", "index.html"), res);
    return;
  }

  if (requestUrl.pathname.startsWith("/ui/")) {
    await serveUiAsset(requestUrl.pathname, res);
    return;
  }

  if (await handleBrowserApiRequest(req, res, deps, requestUrl.pathname)) {
    return;
  }

  if (requestUrl.pathname !== config.httpPath) {
    res.statusCode = 404;
    res.end("Not Found");
    return;
  }

  if (req.method !== "POST") {
    res.statusCode = 405;
    res.setHeader("allow", "POST");
    res.end("Method Not Allowed");
    return;
  }

  const body = await readJsonBody(req);
  const sessionId = headerValue(req.headers["mcp-session-id"]);

  if (!sessionId && isInitializeRequest(body)) {
    const mcpServer = createRagMcpServer(deps);
    const transport = new StreamableHTTPServerTransport({
      sessionIdGenerator: () => randomUUID(),
      onsessioninitialized: (generatedSessionId) => {
        sessions.set(generatedSessionId, { transport });
      },
    });

    transport.onclose = () => {
      if (transport.sessionId) {
        sessions.delete(transport.sessionId);
      }
    };

    await mcpServer.connect(transport);
    await transport.handleRequest(req, res, body);
    return;
  }

  if (!sessionId) {
    res.statusCode = 400;
    res.setHeader("content-type", "application/json");
    res.end(
      JSON.stringify({
        jsonrpc: "2.0",
        error: { code: -32000, message: "Missing MCP session id" },
        id: null,
      }),
    );
    return;
  }

  const session = sessions.get(sessionId);
  if (!session) {
    res.statusCode = 404;
    res.setHeader("content-type", "application/json");
    res.end(
      JSON.stringify({
        jsonrpc: "2.0",
        error: { code: -32000, message: "Unknown MCP session id" },
        id: null,
      }),
    );
    return;
  }

  await session.transport.handleRequest(req, res, body);
}

function headerValue(value: string | string[] | undefined): string | undefined {
  if (Array.isArray(value)) {
    return value[0];
  }
  return value;
}
