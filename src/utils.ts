import { createHash } from "node:crypto";
import { basename, extname } from "node:path";

export function sha1(value: string): string {
  return createHash("sha1").update(value).digest("hex");
}

export function normalizeQuery(value: string): string {
  return value.replace(/\s+/g, " ").trim();
}

export function tokenizeSearchTerms(value: string): string[] {
  return normalizeQuery(value)
    .toLowerCase()
    .split(/[^a-zа-я0-9_./-]+/iu)
    .filter((token) => token.length >= 2);
}

export function guessFileKind(filePath: string): "code" | "doc" | null {
  const extension = extname(filePath).toLowerCase();
  if (
    [
      ".ts",
      ".tsx",
      ".js",
      ".jsx",
      ".mjs",
      ".cjs",
      ".py",
      ".php",
      ".cs",
      ".java",
      ".go",
      ".rs",
      ".rb",
      ".sh",
      ".ps1",
      ".sql",
      ".html",
      ".css",
      ".scss",
      ".json",
      ".yml",
      ".yaml",
      ".xml",
    ].includes(extension)
  ) {
    return "code";
  }

  if ([".md", ".mdx", ".txt", ".rst", ".adoc"].includes(extension)) {
    return "doc";
  }

  return null;
}

export function guessLanguage(filePath: string): string {
  const extension = extname(filePath).toLowerCase();
  const fileName = basename(filePath).toLowerCase();
  const mapping: Record<string, string> = {
    ".ts": "typescript",
    ".tsx": "tsx",
    ".js": "javascript",
    ".jsx": "jsx",
    ".mjs": "javascript",
    ".cjs": "javascript",
    ".py": "python",
    ".php": "php",
    ".cs": "csharp",
    ".java": "java",
    ".go": "go",
    ".rs": "rust",
    ".rb": "ruby",
    ".sh": "shell",
    ".ps1": "powershell",
    ".sql": "sql",
    ".html": "html",
    ".css": "css",
    ".scss": "scss",
    ".json": "json",
    ".yml": "yaml",
    ".yaml": "yaml",
    ".xml": "xml",
    ".md": "markdown",
    ".mdx": "mdx",
    ".txt": "text",
    ".rst": "rst",
    ".adoc": "asciidoc",
  };

  if (fileName === "dockerfile") {
    return "dockerfile";
  }

  return mapping[extension] ?? "text";
}

export function cosineSimilarity(left: number[], right: number[]): number {
  if (left.length === 0 || right.length === 0 || left.length !== right.length) {
    return 0;
  }

  let dot = 0;
  let leftNorm = 0;
  let rightNorm = 0;

  for (let index = 0; index < left.length; index += 1) {
    const leftValue = left[index] ?? 0;
    const rightValue = right[index] ?? 0;
    dot += leftValue * rightValue;
    leftNorm += leftValue * leftValue;
    rightNorm += rightValue * rightValue;
  }

  if (leftNorm === 0 || rightNorm === 0) {
    return 0;
  }

  return dot / (Math.sqrt(leftNorm) * Math.sqrt(rightNorm));
}

export function termOverlapScore(query: string, text: string): number {
  const queryTerms = new Set(tokenizeSearchTerms(query));

  if (queryTerms.size === 0) {
    return 0;
  }

  const haystack = text.toLowerCase();
  let matches = 0;

  for (const term of queryTerms) {
    if (haystack.includes(term)) {
      matches += 1;
    }
  }

  return matches / queryTerms.size;
}

export function phraseMatchScore(query: string, text: string): number {
  const normalizedQuery = normalizeQuery(query).toLowerCase();
  if (!normalizedQuery) {
    return 0;
  }

  const haystack = normalizeQuery(text).toLowerCase();
  if (!haystack) {
    return 0;
  }

  if (haystack.includes(normalizedQuery)) {
    return 1;
  }

  const queryTerms = tokenizeSearchTerms(normalizedQuery);
  if (queryTerms.length < 2) {
    return 0;
  }

  let matchedPairs = 0;
  for (let index = 0; index < queryTerms.length - 1; index += 1) {
    const pair = `${queryTerms[index]} ${queryTerms[index + 1]}`;
    if (haystack.includes(pair)) {
      matchedPairs += 1;
    }
  }

  return matchedPairs / (queryTerms.length - 1);
}

export function exactTokenBoost(query: string, text: string): number {
  const queryTerms = tokenizeSearchTerms(query);
  if (queryTerms.length === 0) {
    return 0;
  }

  const textTerms = new Set(tokenizeSearchTerms(text));
  let exactMatches = 0;

  for (const term of queryTerms) {
    if (textTerms.has(term)) {
      exactMatches += 1;
    }
  }

  return exactMatches / queryTerms.length;
}

export function documentationIntentScore(query: string): number {
  const normalized = normalizeQuery(query).toLowerCase();
  if (!normalized) {
    return 0;
  }

  const markers = [
    "doc",
    "docs",
    "documentation",
    "readme",
    "architecture",
    "design",
    "overview",
    "api",
    "guide",
    "manual",
    "опис",
    "докум",
    "архитект",
    "гайд",
    "схем",
    "обзор",
    "api",
  ];

  let matches = 0;
  for (const marker of markers) {
    if (normalized.includes(marker)) {
      matches += 1;
    }
  }

  return Math.min(matches * 0.2, 1);
}
