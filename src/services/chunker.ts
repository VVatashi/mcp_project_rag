import matter from "gray-matter";
import { basename } from "node:path";
import { IndexedChunk } from "../domain.js";
import { guessFileKind, guessLanguage, sha1 } from "../utils.js";
import { DiscoveredFile } from "./file-discovery.js";

const CODE_WINDOW = 80;
const CODE_OVERLAP = 20;
const DOC_WINDOW = 1200;
const DOC_OVERLAP = 200;

export class ChunkingService {
  public chunkProjectFile(projectId: string, file: DiscoveredFile): IndexedChunk[] {
    const kind = guessFileKind(file.absolutePath);
    if (!kind) {
      return [];
    }

    return kind === "code"
      ? this.chunkCode(projectId, file)
      : this.chunkDoc(projectId, file);
  }

  private chunkCode(projectId: string, file: DiscoveredFile): IndexedChunk[] {
    const lines = file.content.split(/\r?\n/u);
    const chunks: IndexedChunk[] = [];
    let chunkIndex = 0;

    for (let start = 0; start < lines.length; start += CODE_WINDOW - CODE_OVERLAP) {
      const slice = lines.slice(start, start + CODE_WINDOW);
      const text = slice.join("\n").trim();
      if (!text) {
        continue;
      }

      chunks.push(
        this.createChunk(projectId, file, chunkIndex, text, {
          startLine: start + 1,
          endLine: start + slice.length,
        }),
      );
      chunkIndex += 1;
    }

    return chunks;
  }

  private chunkDoc(projectId: string, file: DiscoveredFile): IndexedChunk[] {
    const parsed = matter(file.content);
    const plainBody = parsed.content.trim();
    if (!plainBody) {
      return [];
    }

    const paragraphs = plainBody
      .split(/\n\s*\n/u)
      .map((paragraph) => paragraph.trim())
      .filter(Boolean);

    const chunks: IndexedChunk[] = [];
    let chunkIndex = 0;
    let buffer = "";

    for (const paragraph of paragraphs) {
      const candidate = buffer ? `${buffer}\n\n${paragraph}` : paragraph;
      if (candidate.length <= DOC_WINDOW) {
        buffer = candidate;
        continue;
      }

      if (buffer) {
        chunks.push(this.createChunk(projectId, file, chunkIndex, buffer));
        chunkIndex += 1;
      }

      buffer = paragraph.slice(0, DOC_WINDOW);
    }

    if (buffer) {
      chunks.push(this.createChunk(projectId, file, chunkIndex, buffer));
    }

    return chunks.map((chunk, index, allChunks) => {
      const previousChunk = index > 0 ? allChunks[index - 1] : undefined;
      const previousTail =
        previousChunk
          ? previousChunk.text.slice(Math.max(0, previousChunk.text.length - DOC_OVERLAP))
          : "";

      return {
        ...chunk,
        text: previousTail ? `${previousTail}\n${chunk.text}` : chunk.text,
      };
    });
  }

  private createChunk(
    projectId: string,
    file: DiscoveredFile,
    chunkIndex: number,
    text: string,
    lineInfo?: { startLine: number; endLine: number },
  ): IndexedChunk {
    return {
      id: sha1(`${projectId}:${file.relativePath}:${chunkIndex}:${text}`),
      projectId,
      sourcePath: file.relativePath,
      sourceName: basename(file.relativePath),
      kind: guessFileKind(file.absolutePath)!,
      language: guessLanguage(file.absolutePath),
      chunkIndex,
      startLine: lineInfo?.startLine,
      endLine: lineInfo?.endLine,
      text,
      hash: sha1(text),
    };
  }
}
