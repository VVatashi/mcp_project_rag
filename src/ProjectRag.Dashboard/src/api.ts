import type {
  Anchor,
  Category,
  ChunkDetail,
  CodeGrepParams,
  CodeGrepResult,
  CodeGraphStatus,
  CodeLines,
  CodeReferencePage,
  Diagram,
  DiagramFormat,
  DiagramParams,
  DiagramView,
  CodeSearchRequest,
  CodeSearchResult,
  CodeSymbolParams,
  ChunkPage,
  DocumentContent,
  DocumentPage,
  DocumentSummary,
  Formula,
  IndexRun,
  KnowledgeStatus,
  OutlineNode,
  SearchRequest,
  SearchResult,
  Section,
  Source,
  SourceInput,
  SymbolDetail,
  SymbolMatch,
} from './types';

const tokenKey = 'project-rag.token';

/** An error of the web API with its HTTP status and a message for people. */
export class ApiError extends Error {
  readonly status: number;
  /** The messages of a validation error by field name, in lower case. */
  readonly fields: Record<string, string[]>;

  constructor(status: number, message: string, fields: Record<string, string[]> = {}) {
    super(message);
    this.status = status;
    this.fields = fields;
  }
}

/** Reads the token of the server. Browser storage can be off, so a failure means no token. */
export function getToken(): string {
  try {
    return localStorage.getItem(tokenKey) ?? '';
  } catch {
    return '';
  }
}

/** Stores the token of the server, or removes it for an empty text. */
export function setToken(token: string): void {
  try {
    if (token) localStorage.setItem(tokenKey, token);
    else localStorage.removeItem(tokenKey);
  } catch {
    // The token then lives until the page reloads.
  }
}

interface ProblemDetails {
  title?: string;
  detail?: string;
  errors?: Record<string, string[]>;
}

async function request<T>(path: string, init: RequestInit = {}): Promise<T> {
  const headers = new Headers(init.headers);
  if (init.body) headers.set('Content-Type', 'application/json');
  const token = getToken();
  if (token) headers.set('Authorization', `Bearer ${token}`);

  const response = await fetch(path, { ...init, headers });
  if (response.status === 204) return undefined as T;

  const text = await response.text();
  if (!response.ok) throw new ApiError(response.status, errorMessage(response, text), errorFields(text));

  return (text ? JSON.parse(text) : undefined) as T;
}

async function requestText(path: string): Promise<string> {
  const headers = new Headers();
  const token = getToken();
  if (token) headers.set('Authorization', `Bearer ${token}`);

  const response = await fetch(path, { headers });
  const text = await response.text();
  if (!response.ok) throw new ApiError(response.status, errorMessage(response, text), errorFields(text));
  return text;
}

function errorMessage(response: Response, text: string): string {
  if (response.status === 401) return 'The server needs a token. Set it with the button "Token".';
  try {
    const problem = JSON.parse(text) as ProblemDetails | string;
    if (typeof problem === 'string') return problem;
    const details = Object.values(problem.errors ?? {}).flat();
    if (details.length > 0) return details.join(' ');
    return problem.detail ?? problem.title ?? `${response.status} ${response.statusText}`;
  } catch {
    return text || `${response.status} ${response.statusText}`;
  }
}

function errorFields(text: string): Record<string, string[]> {
  try {
    const problem = JSON.parse(text) as ProblemDetails | string;
    if (typeof problem !== 'object' || problem === null || !problem.errors) return {};
    return Object.fromEntries(Object.entries(problem.errors).map(([key, value]) => [key.toLowerCase(), value]));
  } catch {
    return {};
  }
}

function query(parameters: Record<string, string | number | boolean | null | undefined>): string {
  const search = new URLSearchParams();
  for (const [key, value] of Object.entries(parameters)) {
    if (value !== null && value !== undefined && value !== '') search.set(key, String(value));
  }
  const text = search.toString();
  return text ? `?${text}` : '';
}

export const api = {
  status: () => request<KnowledgeStatus>('/api/status'),

  sources: () => request<Source[]>('/api/sources'),
  addSource: (input: SourceInput) => request<Source>('/api/sources', { method: 'POST', body: JSON.stringify(input) }),
  updateSource: (sourceId: number, input: SourceInput) =>
    request<Source>(`/api/sources/${sourceId}`, { method: 'PUT', body: JSON.stringify(input) }),
  deleteSource: (sourceId: number) => request<undefined>(`/api/sources/${sourceId}`, { method: 'DELETE' }),
  indexSource: (sourceId: number) => request<{ queued: boolean }>(`/api/sources/${sourceId}/index`, { method: 'POST' }),

  runs: (sourceId: number | null, limit: number) => request<IndexRun[]>(`/api/runs${query({ sourceId, limit })}`),

  categories: () => request<Category[]>('/api/categories'),
  documents: (sourceId: number | null, category: string | null, text: string, offset: number, limit: number) =>
    request<DocumentPage>(`/api/documents${query({ sourceId, category, q: text, offset, limit })}`),
  document: (documentId: number) => request<DocumentSummary>(`/api/documents/${documentId}`),
  content: (documentId: number, offset: number, length: number) =>
    request<DocumentContent>(`/api/documents/${documentId}/content${query({ offset, length })}`),
  chunks: (documentId: number, sectionId: number | null, offset: number, limit: number) =>
    request<ChunkPage>(`/api/documents/${documentId}/chunks${query({ sectionId, offset, limit })}`),
  boundaries: (documentId: number) => request<number[]>(`/api/documents/${documentId}/boundaries`),
  formulas: (documentId: number) => request<Formula[]>(`/api/documents/${documentId}/formulas`),
  sections: (documentId: number) => request<Section[]>(`/api/documents/${documentId}/sections`),
  anchor: (documentId: number, name: string) => request<Anchor>(`/api/documents/${documentId}/anchors/${encodeURIComponent(name)}`),
  chunk: (chunkId: number) => request<ChunkDetail>(`/api/chunks/${chunkId}`),

  /** Loads the bytes of the original file. A link to the API cannot carry the token, a fetch can. */
  fileBytes: async (documentId: number) => {
    const headers = new Headers();
    const token = getToken();
    if (token) headers.set('Authorization', `Bearer ${token}`);
    const response = await fetch(`/api/documents/${documentId}/file`, { headers });
    if (!response.ok) throw new ApiError(response.status, `The file is not available (${response.status}).`);
    return new Uint8Array(await response.arrayBuffer());
  },

  codeGrep: (params: CodeGrepParams) =>
    request<CodeGrepResult>(
      `/api/code/grep${query({
        pattern: params.pattern,
        sourceId: params.sourceId,
        regex: String(params.regex),
        caseSensitive: String(params.caseSensitive),
        path: params.path,
        language: params.language,
        limit: params.limit,
      })}`,
    ),
  codeSearch: (body: CodeSearchRequest) => request<CodeSearchResult>('/api/code/search', { method: 'POST', body: JSON.stringify(body) }),
  codeLines: (documentId: number, start: number, end?: number) => request<CodeLines>(`/api/code/lines${query({ documentId, start, end })}`),
  codeGraph: (sourceId: number) => request<CodeGraphStatus>(`/api/code/graph${query({ sourceId })}`),
  codeSymbols: (params: CodeSymbolParams) =>
    request<SymbolMatch[]>(
      `/api/code/symbols${query({ q: params.q, sourceId: params.sourceId, kind: params.kind, project: params.project, limit: params.limit ?? 50 })}`,
    ),
  codeSymbol: (symbolId: number) => request<SymbolDetail>(`/api/code/symbols/${symbolId}`),
  codeSymbolByDocId: (sourceId: number, docId: string, project?: string | null) =>
    request<SymbolDetail>(`/api/code/symbol${query({ sourceId, docId, project })}`),
  codeReferences: (symbolId: number, offset: number, limit: number, kind?: string | null) =>
    request<CodeReferencePage>(`/api/code/symbols/${symbolId}/references${query({ offset, limit, kind })}`),
  codeDiagram: (view: DiagramView, params: DiagramParams) => request<Diagram>(`/api/code/diagrams/${view}${query(params)}`),
  codeDiagramText: (view: DiagramView, params: DiagramParams, format: DiagramFormat) =>
    requestText(`/api/code/diagrams/${view}${query({ ...params, format })}`),
  codeOutline:(documentId: number) => request<OutlineNode[]>(`/api/code/outline${query({ documentId })}`),

  search: (body: SearchRequest) => request<SearchResult>('/api/search', { method: 'POST', body: JSON.stringify(body) }),
};
