import { existsSync, rmSync } from "node:fs";
import { resolve } from "node:path";
import { ProjectIndexState } from "../domain.js";
import {
  moveCorruptedFileAside,
  readJsonFile,
  writeJsonFileAtomic,
} from "../fs-utils.js";

export class ProjectIndexStateCache {
  public constructor(private readonly stateDir: string) {}

  public get(projectId: string): ProjectIndexState | undefined {
    const filePath = this.filePath(projectId);
    if (!existsSync(filePath)) {
      return undefined;
    }

    try {
      return readJsonFile<ProjectIndexState>(filePath);
    } catch {
      moveCorruptedFileAside(filePath);
      return undefined;
    }
  }

  public set(projectId: string, state: ProjectIndexState): void {
    writeJsonFileAtomic(this.filePath(projectId), state);
  }

  public delete(projectId: string): void {
    rmSync(this.filePath(projectId), { force: true });
  }

  private filePath(projectId: string): string {
    return resolve(this.stateDir, `${projectId}.json`);
  }
}
