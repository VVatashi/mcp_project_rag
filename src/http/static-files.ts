import { ServerResponse } from "node:http";
import { existsSync, readFileSync } from "node:fs";
import { extname, resolve, sep } from "node:path";

export async function serveStaticFile(
  absolutePath: string,
  res: ServerResponse,
): Promise<void> {
  if (!existsSync(absolutePath)) {
    res.statusCode = 404;
    res.end("Not Found");
    return;
  }

  const extension = extname(absolutePath).toLowerCase();
  const contentType = (
    {
      ".html": "text/html; charset=utf-8",
      ".css": "text/css; charset=utf-8",
      ".js": "application/javascript; charset=utf-8",
      ".json": "application/json; charset=utf-8",
    } as Record<string, string>
  )[extension] ?? "application/octet-stream";

  res.statusCode = 200;
  res.setHeader("content-type", contentType);
  res.end(readFileSync(absolutePath));
}

export async function serveUiAsset(
  requestPath: string,
  res: ServerResponse,
): Promise<void> {
  const publicRoot = resolve("public");
  const assetPath = decodeURIComponent(requestPath.slice("/ui/".length));
  const resolvedPath = resolve(publicRoot, assetPath);
  const publicRootWithSeparator = `${publicRoot}${sep}`;

  if (resolvedPath !== publicRoot && !resolvedPath.startsWith(publicRootWithSeparator)) {
    res.statusCode = 404;
    res.end("Not Found");
    return;
  }

  await serveStaticFile(resolvedPath, res);
}
