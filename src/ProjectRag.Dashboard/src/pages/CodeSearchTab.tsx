import { useState, type FormEvent } from 'react';
import { Link, useSearchParams } from 'react-router';

import { ApiError, api } from '../api';
import { MatchFiles, SymbolLink, SymbolRow, fileHref } from '../components/Code';
import { Badge, Empty, ErrorBanner, Panel } from '../components/Common';
import { formatNumber } from '../format';
import { useLoad } from '../hooks';
import type { CodeChunkHit, CodeSearchResult } from '../types';

const limits = [8, 16, 32];
const defaultLimit = 8;
const previewLines = 15;

type Rerank = '' | 'on' | 'off';

interface Params {
  query: string;
  sourceId: number | null;
  path: string;
  language: string;
  includeTests: boolean;
  limit: number;
  rerank: Rerank;
}

function readParams(parameters: URLSearchParams): Params {
  const source = Number(parameters.get('source'));
  const limit = Number(parameters.get('limit'));
  const rerank = parameters.get('rerank');
  return {
    query: parameters.get('q') ?? '',
    sourceId: Number.isInteger(source) && source > 0 ? source : null,
    path: parameters.get('path') ?? '',
    language: parameters.get('language') ?? '',
    includeTests: parameters.get('tests') !== '0',
    limit: limits.includes(limit) ? limit : defaultLimit,
    rerank: rerank === '1' ? 'on' : rerank === '0' ? 'off' : '',
  };
}

function writeParams(params: Params): URLSearchParams {
  const result = new URLSearchParams();
  result.set('q', params.query);
  if (params.sourceId !== null) result.set('source', String(params.sourceId));
  if (params.path) result.set('path', params.path);
  if (params.language) result.set('language', params.language);
  if (!params.includeTests) result.set('tests', '0');
  if (params.limit !== defaultLimit) result.set('limit', String(params.limit));
  if (params.rerank) result.set('rerank', params.rerank === 'on' ? '1' : '0');
  return result;
}

function FieldError({ messages }: { messages: string[] | undefined }) {
  if (!messages || messages.length === 0) return null;
  return (
    <span className="field-error" role="alert">
      {messages.join(' ')}
    </span>
  );
}

function CodeSearchForm({ params, errors, onSearch }: { params: Params; errors: Record<string, string[]>; onSearch: (params: Params) => void }) {
  const sources = useLoad(api.sources, []);
  const [draft, setDraft] = useState(params);
  const codeSources = sources.data?.filter((source) => source.kind === 'code') ?? [];

  const submit = (event: FormEvent) => {
    event.preventDefault();
    const query = draft.query.trim();
    if (query) onSearch({ ...draft, query });
  };

  return (
    <form className="search code-form" onSubmit={submit}>
      <input
        className="search__input mono"
        value={draft.query}
        onChange={(event) => setDraft({ ...draft, query: event.target.value })}
        placeholder="A name, a piece of code or a question, for example: where the scene updates its objects"
        aria-label="Query"
        autoFocus
        aria-invalid={errors.query !== undefined}
      />
      <button type="submit" className="button button--primary" disabled={!draft.query.trim()}>
        Search
      </button>
      {errors.query && (
        <div className="search__error" role="alert">
          {errors.query.join(' ')}
        </div>
      )}
      <div className="search__options">
        <label className="inline">
          Codebase
          <select value={draft.sourceId ?? ''} onChange={(event) => setDraft({ ...draft, sourceId: event.target.value ? Number(event.target.value) : null })}>
            <option value="">All</option>
            {codeSources.map((source) => (
              <option key={source.sourceId} value={source.sourceId}>
                {source.name}
              </option>
            ))}
          </select>
        </label>
        <FieldError messages={errors.sourceid} />
        <input
          className="mono"
          value={draft.path}
          onChange={(event) => setDraft({ ...draft, path: event.target.value })}
          placeholder="Path, for example *.cs"
          aria-label="Path glob"
          aria-invalid={errors.path !== undefined}
        />
        <FieldError messages={errors.path} />
        <input
          value={draft.language}
          onChange={(event) => setDraft({ ...draft, language: event.target.value })}
          placeholder="Language"
          aria-label="Language"
          size={12}
          aria-invalid={errors.language !== undefined}
        />
        <FieldError messages={errors.language} />
        <label className="inline">
          <input type="checkbox" checked={draft.includeTests} onChange={(event) => setDraft({ ...draft, includeTests: event.target.checked })} />
          Include tests
        </label>
        <span className="search__spacer" />
        <label className="inline">
          Similar
          <select value={draft.limit} onChange={(event) => setDraft({ ...draft, limit: Number(event.target.value) })}>
            {limits.map((value) => (
              <option key={value}>{value}</option>
            ))}
          </select>
        </label>
        <FieldError messages={errors.limit} />
        <label className="inline">
          Reranker
          <select value={draft.rerank} onChange={(event) => setDraft({ ...draft, rerank: event.target.value as Rerank })}>
            <option value="">Server default</option>
            <option value="on">On</option>
            <option value="off">Off</option>
          </select>
        </label>
        <FieldError messages={errors.rerank} />
      </div>
    </form>
  );
}

function Heading({ text }: { text: string }) {
  const parts = text.split(' > ');
  const last = parts.pop() ?? '';
  return (
    <span className="chunk__heading">
      {parts.length > 0 && <span className="muted">{parts.join(' > ')} &gt; </span>}
      <strong>{last}</strong>
    </span>
  );
}

function Scores({ hit }: { hit: CodeChunkHit }) {
  const parts = [`vector ${hit.vectorRank ?? '—'}`, `text ${hit.textRank ?? '—'}`];
  if (hit.rerankScore !== null) parts.push(`rerank ${hit.rerankScore.toFixed(3)}`);
  return (
    <span className="muted small mono chunk__scores" title="The ranks in the vector and token search, and the rerank score">
      score {hit.score.toFixed(4)} · {parts.join(' · ')}
    </span>
  );
}

function ChunkCard({ hit }: { hit: CodeChunkHit }) {
  const [all, setAll] = useState(false);
  const lines = hit.text.replace(/\r?\n$/, '').split(/\r?\n/);
  const shown = all ? lines : lines.slice(0, previewLines);

  return (
    <li className="chunk">
      <div className="chunk__head">
        <Heading text={hit.heading} />
        <Scores hit={hit} />
      </div>
      <div className="chunk__where small">
        <span className="muted">{hit.source}</span>
        <Link className="link mono" to={fileHref(hit.documentId, hit.lineStart, hit.lineEnd)}>
          {hit.path}:{hit.lineStart}-{hit.lineEnd}
        </Link>
        {hit.symbol && <SymbolLink symbol={hit.symbol} full />}
      </div>
      <div className="code chunk__body">
        {shown.map((text, index) => (
          <div key={index} className="code__line" data-n={hit.lineStart + index}>
            {text}
          </div>
        ))}
      </div>
      {lines.length > previewLines && (
        <button type="button" className="button button--small" onClick={() => setAll(!all)}>
          {all ? 'Show fewer lines' : `Show all ${formatNumber(lines.length)} lines`}
        </button>
      )}
    </li>
  );
}

interface Outcome {
  key: string;
  result?: CodeSearchResult;
  errors?: Record<string, string[]>;
}

export function CodeSearchTab() {
  const [parameters, setParameters] = useSearchParams();
  const params = readParams(parameters);
  const key = JSON.stringify(params);
  const outcome = useLoad<Outcome | undefined>(async () => {
    if (!params.query) return undefined;
    try {
      const result = await api.codeSearch({
        query: params.query,
        sourceId: params.sourceId,
        path: params.path || null,
        language: params.language || null,
        includeTests: params.includeTests,
        limit: params.limit,
        rerank: params.rerank === '' ? null : params.rerank === 'on',
      });
      return { key, result };
    } catch (exception) {
      if (exception instanceof ApiError && exception.status === 400 && Object.keys(exception.fields).length > 0) {
        return { key, errors: exception.fields };
      }
      throw exception;
    }
  }, [key]);

  const current = outcome.data?.key === key ? outcome.data : undefined;
  const busy = params.query !== '' && current === undefined && !outcome.error;
  const result = current?.result;
  const empty = result !== undefined && result.symbols.length + result.matches.length + result.chunks.length === 0;

  return (
    <>
      <CodeSearchForm key={parameters.toString()} params={params} errors={current?.errors ?? {}} onSearch={(next) => setParameters(writeParams(next))} />
      <ErrorBanner message={outcome.error} />
      {busy && <p className="muted">Searching…</p>}
      {result && (
        <>
          <div className="code-summary">
            <Badge tone="info">{result.mode}</Badge>
            {result.reranked && <Badge tone="ok">reranked</Badge>}
            <span className="muted small">{formatNumber(result.elapsedMs)} ms</span>
          </div>
          {result.notes.map((note) => (
            <div key={note} className="banner banner--warn">
              {note}
            </div>
          ))}
          {empty && <Empty>Nothing found.</Empty>}
          {result.symbols.length > 0 && (
            <Panel title={`Symbols (${formatNumber(result.symbols.length)})`}>
              <ul className="symbols">
                {result.symbols.map((match) => (
                  <SymbolRow key={match.symbolId} match={match} />
                ))}
              </ul>
            </Panel>
          )}
          {result.matches.length > 0 && (
            <Panel title={`Exact matches (${formatNumber(result.matches.length)})`}>
              <MatchFiles matches={result.matches} />
            </Panel>
          )}
          {result.chunks.length > 0 && (
            <Panel title={`Similar code (${formatNumber(result.chunks.length)})`}>
              <ul className="chunks">
                {result.chunks.map((hit) => (
                  <ChunkCard key={hit.chunkId} hit={hit} />
                ))}
              </ul>
            </Panel>
          )}
        </>
      )}
    </>
  );
}
