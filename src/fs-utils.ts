import { randomUUID } from "node:crypto";
import { existsSync, readFileSync, renameSync, writeFileSync } from "node:fs";
import { basename, dirname, join } from "node:path";
import { ensureParentDirectory } from "./config.js";

export function readJsonFile<T>(filePath: string): T {
  return JSON.parse(readFileSync(filePath, "utf8")) as T;
}

export function writeJsonFileAtomic(filePath: string, value: unknown): void {
  ensureParentDirectory(filePath);
  const tempFilePath = join(
    dirname(filePath),
    `${basename(filePath)}.${randomUUID()}.tmp`,
  );
  writeFileSync(tempFilePath, `${JSON.stringify(value, null, 2)}\n`, "utf8");
  renameSync(tempFilePath, filePath);
}

export function moveCorruptedFileAside(filePath: string): string | undefined {
  if (!existsSync(filePath)) {
    return undefined;
  }

  const backupPath = join(
    dirname(filePath),
    `${basename(filePath)}.corrupt-${Date.now()}`,
  );
  renameSync(filePath, backupPath);
  return backupPath;
}
