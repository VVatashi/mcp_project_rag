import type {
  Chunk,
  ChunkDetail,
  DocumentContent,
  DocumentPage,
  DocumentSummary,
  IndexRun,
  KnowledgeStatus,
  SearchRequest,
  SearchResult,
  Section,
  Source,
  SourceInput,
} from './types';

const tokenKey = 'project-rag.token';

/** An error of the web API with its HTTP status and a message for people. */
export class ApiError extends Error {
  readonly status: number;

  constructor(status: number, message: string) {
    super(message);
    this.status = status;
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
  if (!response.ok) throw new ApiError(response.status, errorMessage(response, text));

  return (text ? JSON.parse(text) : undefined) as T;
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

function query(parameters: Record<string, string | number | null | undefined>): string {
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

  documents: (sourceId: number | null, text: string, offset: number, limit: number) =>
    request<DocumentPage>(`/api/documents${query({ sourceId, q: text, offset, limit })}`),
  document: (documentId: number) => request<DocumentSummary>(`/api/documents/${documentId}`),
  content: (documentId: number, offset: number, length: number) =>
    request<DocumentContent>(`/api/documents/${documentId}/content${query({ offset, length })}`),
  chunks: (documentId: number) => request<Chunk[]>(`/api/documents/${documentId}/chunks`),
  sections: (documentId: number) => request<Section[]>(`/api/documents/${documentId}/sections`),
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

  search: (body: SearchRequest) => request<SearchResult>('/api/search', { method: 'POST', body: JSON.stringify(body) }),
};
