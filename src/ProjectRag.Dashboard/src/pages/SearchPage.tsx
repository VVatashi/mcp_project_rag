import { useState, type FormEvent } from 'react';

import { api } from '../api';
import { Badge, Empty, ErrorBanner, Panel } from '../components/Common';
import { useLoad } from '../hooks';
import type { SearchResult } from '../types';

export function SearchPage({ onOpenHit }: { onOpenHit: (documentId: number, offset: number) => void }) {
  const sources = useLoad(api.sources, []);
  const [query, setQuery] = useState('');
  const [selected, setSelected] = useState<string[]>([]);
  const [limit, setLimit] = useState(8);
  const [rerank, setRerank] = useState(true);
  const [result, setResult] = useState<SearchResult>();
  const [elapsed, setElapsed] = useState<number>();
  const [error, setError] = useState<string>();
  const [busy, setBusy] = useState(false);

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (!query.trim()) return;
    setBusy(true);
    setError(undefined);
    const started = performance.now();
    try {
      setResult(await api.search({ query, sources: selected.length > 0 ? selected : null, limit, rerank }));
      setElapsed(Math.round(performance.now() - started));
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : String(exception));
    } finally {
      setBusy(false);
    }
  };

  const toggle = (name: string) => setSelected((list) => (list.includes(name) ? list.filter((item) => item !== name) : [...list, name]));

  return (
    <div className="page">
      <form className="search" onSubmit={(event) => void submit(event)}>
        <input
          className="search__input"
          value={query}
          onChange={(event) => setQuery(event.target.value)}
          placeholder="Ask a question or type keywords, for example: how does seamless cube map filtering work"
          autoFocus
        />
        <button type="submit" className="button button--primary" disabled={busy || !query.trim()}>
          {busy ? 'Searching…' : 'Search'}
        </button>
        <div className="search__options">
          {sources.data?.map((source) => (
            <label key={source.sourceId} className={`chip ${selected.includes(source.name) ? 'chip--on' : ''}`}>
              <input type="checkbox" checked={selected.includes(source.name)} onChange={() => toggle(source.name)} />
              {source.name}
            </label>
          ))}
          <span className="search__spacer" />
          <label className="inline">
            Hits
            <select value={limit} onChange={(event) => setLimit(Number(event.target.value))}>
              {[5, 8, 10, 20, 50].map((value) => (
                <option key={value}>{value}</option>
              ))}
            </select>
          </label>
          <label className="inline">
            <input type="checkbox" checked={rerank} onChange={(event) => setRerank(event.target.checked)} />
            Reranker
          </label>
        </div>
      </form>

      <ErrorBanner message={error} />
      {result && (
        <Panel
          title={`${result.hits.length} hits`}
          actions={
            <>
              <Badge tone="info">{result.mode}</Badge>
              {result.reranked && <Badge tone="ok">reranked</Badge>}
              {elapsed !== undefined && <span className="muted small">{elapsed} ms</span>}
            </>
          }
        >
          {result.notes.map((note) => (
            <div key={note} className="banner banner--warn">
              {note}
            </div>
          ))}
          {result.hits.length === 0 && <Empty>Nothing found.</Empty>}
          <ol className="hits">
            {result.hits.map((hit) => (
              <li key={hit.chunkId} className="hit">
                <div className="hit__head">
                  <button type="button" className="link hit__heading" onClick={() => onOpenHit(hit.documentId, hit.charStart)}>
                    {hit.heading}
                  </button>
                  <span className="hit__scores">
                    {hit.rerankScore !== null && <Badge tone="ok" title="The score of the reranker">rerank {hit.rerankScore.toFixed(3)}</Badge>}
                    <Badge tone="muted" title="The rank in the vector search">vec {hit.vectorRank ?? '—'}</Badge>
                    <Badge tone="muted" title="The rank in the full text search">fts {hit.textRank ?? '—'}</Badge>
                    <Badge tone="muted" title="The score of the reciprocal rank fusion">rrf {hit.score.toFixed(4)}</Badge>
                  </span>
                </div>
                <div className="hit__path mono small">
                  {hit.source} · {hit.path} · characters {hit.charStart}–{hit.charEnd}
                </div>
                <p className="hit__body">{hit.body}</p>
              </li>
            ))}
          </ol>
        </Panel>
      )}
    </div>
  );
}
