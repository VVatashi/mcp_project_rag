import { existsSync, readdirSync, readFileSync, statSync } from "node:fs";
import { join, relative } from "node:path";
import ignore from "ignore";
import { ProjectRegistration } from "../domain.js";
import { guessFileKind, sha1 } from "../utils.js";

export interface DiscoveredFileEntry {
  absolutePath: string;
  relativePath: string;
  mtimeMs: number;
  size: number;
}

export interface DiscoveredFile {
  absolutePath: string;
  relativePath: string;
  mtimeMs: number;
  size: number;
  contentHash: string;
  content: string;
}

export class FileDiscoveryService {
  public discover(project: ProjectRegistration): DiscoveredFile[] {
    return this.discoverEntries(project).map((entry) => this.readFile(entry));
  }

  public discoverEntries(project: ProjectRegistration): DiscoveredFileEntry[] {
    const gitignore = this.loadGitIgnore(project.rootPath);
    const matcher = ignore().add(project.excludeGlobs).add(gitignore);
    const files: DiscoveredFileEntry[] = [];
    this.walkDirectory(project.rootPath, project.rootPath, matcher, files);
    return files.filter((file) => guessFileKind(file.absolutePath) !== null);
  }

  public readFile(file: DiscoveredFileEntry): DiscoveredFile {
    const content = readFileSync(file.absolutePath, "utf8");
    return {
      ...file,
      contentHash: sha1(content),
      content,
    };
  }

  private walkDirectory(
    rootPath: string,
    currentPath: string,
    matcher: ReturnType<typeof ignore>,
    files: DiscoveredFileEntry[],
  ): void {
    for (const entry of readdirSync(currentPath, { withFileTypes: true })) {
      const absolutePath = join(currentPath, entry.name);
      const relativePath = relative(rootPath, absolutePath).replaceAll("\\", "/");

      if (!relativePath || matcher.ignores(relativePath)) {
        continue;
      }

      if (entry.isDirectory()) {
        this.walkDirectory(rootPath, absolutePath, matcher, files);
        continue;
      }

      if (!entry.isFile()) {
        continue;
      }

      const stats = statSync(absolutePath);
      files.push({
        absolutePath,
        relativePath,
        mtimeMs: stats.mtimeMs,
        size: stats.size,
      });
    }
  }

  private loadGitIgnore(rootPath: string): string[] {
    const gitignorePath = join(rootPath, ".gitignore");
    if (!existsSync(gitignorePath)) {
      return [];
    }

    return readFileSync(gitignorePath, "utf8")
      .split(/\r?\n/u)
      .map((line) => line.trim())
      .filter((line) => line.length > 0 && !line.startsWith("#"));
  }
}
