import { useState, type FormEvent } from 'react';
import { Link } from 'react-router';

import { api } from '../api';
import { Badge, Empty, ErrorBanner, Panel } from '../components/Common';
import { Markdown } from '../components/Markdown';
import { anchorAddress } from '../format';
import { useLoad } from '../hooks';
import type { SearchResult } from '../types';

export function SearchPage() {
  const categories = useLoad(api.categories, []);
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
      setResult(await api.search({ query, categories: selected.length > 0 ? selected : null, sources: null, limit, rerank }));
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
          {categories.data?.map((category) => (
            <label key={category.name} className={`chip ${selected.includes(category.name) ? 'chip--on' : ''}`} title={`${category.documentCount} documents`}>
              <input type="checkbox" checked={selected.includes(category.name)} onChange={() => toggle(category.name)} />
              {category.name}
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
                  <Link className="link hit__heading" to={`/chunks/${hit.chunkId}`}>
                    {hit.heading}
                  </Link>
                  <span className="hit__scores">
                    {hit.rerankScore !== null && <Badge tone="ok" title="The score of the reranker">rerank {hit.rerankScore.toFixed(3)}</Badge>}
                    <Badge tone="muted" title="The rank in the vector search">vec {hit.vectorRank ?? '—'}</Badge>
                    <Badge tone="muted" title="The rank in the full text search">fts {hit.textRank ?? '—'}</Badge>
                    <Badge tone="muted" title="The score of the reciprocal rank fusion">rrf {hit.score.toFixed(4)}</Badge>
                  </span>
                </div>
                <div className="hit__path mono small">
                  {hit.category} · {hit.source} ·{' '}
                  <Link className="link" to={`/documents/${hit.documentId}/text?offset=${hit.charStart}`}>
                    {hit.path}
                  </Link>
                  {hit.pageStart !== null && ` · page ${hit.pageStart}${hit.pageEnd !== hit.pageStart ? `–${hit.pageEnd}` : ''}`}
                  {hit.linkCount > 0 && ` · ${hit.linkCount} links`}
                </div>
                {hit.copies.length > 0 && (
                  <div className="hit__path mono small">
                    also in{' '}
                    {hit.copies.map((copy, index) => (
                      <span key={copy.chunkId}>
                        {index > 0 && ', '}
                        <Link className="link" to={`/chunks/${copy.chunkId}`} title={copy.heading}>
                          {copy.path}
                        </Link>
                        {copy.similarity < 1 && ` (${copy.similarity.toFixed(2)})`}
                      </span>
                    ))}
                  </div>
                )}
                <Markdown text={hit.body} anchorHref={(anchor) => anchorAddress(hit.documentId, anchor)} />
              </li>
            ))}
          </ol>
        </Panel>
      )}
    </div>
  );
}
