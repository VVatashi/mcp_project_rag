import { useEffect, useEffectEvent, useState, type FormEvent } from 'react';
import { useSearchParams } from 'react-router';

import { api } from '../api';
import { SymbolRow, kindLabel, memberKinds, typeKinds } from '../components/Code';
import { Empty, ErrorBanner, Panel } from '../components/Common';
import { formatNumber } from '../format';
import { useLoad } from '../hooks';
import type { SymbolMatch } from '../types';

const debounceMs = 300;

interface Outcome {
  key: string;
  matches: SymbolMatch[];
}

export function CodeSymbolsTab() {
  const [parameters, setParameters] = useSearchParams();
  const text = parameters.get('q') ?? '';
  const sourceParameter = Number(parameters.get('source'));
  const sourceId = Number.isInteger(sourceParameter) && sourceParameter > 0 ? sourceParameter : null;
  const kind = parameters.get('kind') ?? '';
  const project = parameters.get('project') ?? '';
  const [draft, setDraft] = useState(text);

  const update = (changes: Record<string, string>, replace = false) => {
    const next = new URLSearchParams(parameters);
    next.set('tab', 'symbols');
    for (const [name, value] of Object.entries(changes)) {
      if (value) next.set(name, value);
      else next.delete(name);
    }
    setParameters(next, { replace });
  };

  const commit = useEffectEvent((value: string) => update({ q: value.trim() }, true));

  useEffect(() => {
    if (draft.trim() === text) return;
    const timer = window.setTimeout(() => commit(draft), debounceMs);
    return () => window.clearTimeout(timer);
  }, [draft, text]);

  const sources = useLoad(api.sources, []);
  const codeSources = sources.data?.filter((source) => source.kind === 'code') ?? [];
  const graph = useLoad(() => (sourceId === null ? Promise.resolve(undefined) : api.codeGraph(sourceId)), [sourceId]);
  const projects = graph.data && graph.data.sourceId === sourceId ? graph.data.projects : [];

  const key = JSON.stringify({ text, sourceId, kind, project });
  const outcome = useLoad<Outcome | undefined>(async () => {
    if (!text) return undefined;
    return { key, matches: await api.codeSymbols({ q: text, sourceId, kind, project }) };
  }, [key]);
  const current = outcome.data?.key === key ? outcome.data : undefined;
  const busy = text !== '' && current === undefined && !outcome.error;

  const submit = (event: FormEvent) => {
    event.preventDefault();
    update({ q: draft.trim() });
  };

  return (
    <>
      <form className="search code-form" onSubmit={submit}>
        <input
          className="search__input mono"
          value={draft}
          onChange={(event) => setDraft(event.target.value)}
          placeholder="A name, camel humps (SSync) or a qualified name (SceneHost.Update)"
          aria-label="Symbol"
          autoFocus
        />
        <button type="submit" className="button button--primary" disabled={!draft.trim()}>
          Search
        </button>
        <div className="search__options">
          <label className="inline">
            Codebase
            <select value={sourceId ?? ''} onChange={(event) => update({ source: event.target.value, project: '' })}>
              <option value="">All</option>
              {codeSources.map((source) => (
                <option key={source.sourceId} value={source.sourceId}>
                  {source.name}
                </option>
              ))}
            </select>
          </label>
          <label className="inline">
            Kind
            <select value={kind} onChange={(event) => update({ kind: event.target.value })}>
              <option value="">Any</option>
              <option value="type">Type</option>
              <optgroup label="Types">
                {typeKinds.map((item) => (
                  <option key={item} value={item}>
                    {kindLabel(item)}
                  </option>
                ))}
              </optgroup>
              <optgroup label="Members">
                {memberKinds.map((item) => (
                  <option key={item} value={item}>
                    {kindLabel(item)}
                  </option>
                ))}
              </optgroup>
            </select>
          </label>
          <label className="inline">
            Project
            <select value={project} disabled={sourceId === null} onChange={(event) => update({ project: event.target.value })}>
              <option value="">All</option>
              {projects.map((item) => (
                <option key={item.projectId} value={item.name}>
                  {item.name}
                </option>
              ))}
            </select>
          </label>
        </div>
      </form>
      <ErrorBanner message={outcome.error ?? sources.error ?? graph.error} />
      {busy && <p className="muted">Searching…</p>}
      {current && (
        <Panel title={`${formatNumber(current.matches.length)} symbols`}>
          {current.matches.length === 0 && <Empty>Nothing found.</Empty>}
          <ul className="symbols">
            {current.matches.map((match) => (
              <SymbolRow key={match.symbolId} match={match} />
            ))}
          </ul>
        </Panel>
      )}
    </>
  );
}
