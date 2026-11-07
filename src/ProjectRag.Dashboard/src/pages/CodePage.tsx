import { useState, type FormEvent } from 'react';
import { Link, useSearchParams } from 'react-router';

import { ApiError, api } from '../api';
import { MatchFiles, diagramHref } from '../components/Code';
import { Empty, ErrorBanner, Panel } from '../components/Common';
import { formatNumber, numberOrNull } from '../format';
import { useLoad } from '../hooks';
import { CodeProjectsTab } from './CodeProjectsTab';
import { CodeSearchTab } from './CodeSearchTab';
import { CodeSymbolsTab } from './CodeSymbolsTab';
import type { CodeGrepParams, CodeGrepResult } from '../types';

const limits = [100, 300, 1000];
const defaultLimit = 100;

function readParams(parameters: URLSearchParams): CodeGrepParams {
  const source = Number(parameters.get('source'));
  const limit = Number(parameters.get('limit'));
  return {
    pattern: parameters.get('q') ?? '',
    sourceId: Number.isInteger(source) && source > 0 ? source : null,
    regex: parameters.get('regex') === '1',
    caseSensitive: parameters.get('case') !== '0',
    path: parameters.get('path') ?? '',
    language: parameters.get('language') ?? '',
    limit: limits.includes(limit) ? limit : defaultLimit,
  };
}

function writeParams(params: CodeGrepParams): URLSearchParams {
  const result = new URLSearchParams({ tab: 'text' });
  result.set('q', params.pattern);
  if (params.sourceId !== null) result.set('source', String(params.sourceId));
  if (params.regex) result.set('regex', '1');
  if (!params.caseSensitive) result.set('case', '0');
  if (params.path) result.set('path', params.path);
  if (params.language) result.set('language', params.language);
  if (params.limit !== defaultLimit) result.set('limit', String(params.limit));
  return result;
}

function CodeForm({ params, patternError, onSearch }: { params: CodeGrepParams; patternError: string | undefined; onSearch: (params: CodeGrepParams) => void }) {
  const sources = useLoad(api.sources, []);
  const [draft, setDraft] = useState(params);
  const codeSources = sources.data?.filter((source) => source.kind === 'code') ?? [];

  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (draft.pattern) onSearch(draft);
  };

  return (
    <form className="search code-form" onSubmit={submit}>
      <input
        className="search__input mono"
        value={draft.pattern}
        onChange={(event) => setDraft({ ...draft, pattern: event.target.value })}
        placeholder="The text or the regular expression to find"
        aria-label="Pattern"
        autoFocus
        aria-invalid={patternError !== undefined}
      />
      <button type="submit" className="button button--primary" disabled={!draft.pattern}>
        Search
      </button>
      {patternError && (
        <div className="search__error" role="alert">
          {patternError}
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
        <label className="inline">
          <input type="checkbox" checked={draft.regex} onChange={(event) => setDraft({ ...draft, regex: event.target.checked })} />
          Regex
        </label>
        <label className="inline">
          <input type="checkbox" checked={draft.caseSensitive} onChange={(event) => setDraft({ ...draft, caseSensitive: event.target.checked })} />
          Case sensitive
        </label>
        <input
          className="mono"
          value={draft.path}
          onChange={(event) => setDraft({ ...draft, path: event.target.value })}
          placeholder="Path, for example src/**/*.cs"
          aria-label="Path glob"
        />
        <input value={draft.language} onChange={(event) => setDraft({ ...draft, language: event.target.value })} placeholder="Language" aria-label="Language" size={12} />
        <span className="search__spacer" />
        <label className="inline">
          Matches
          <select value={draft.limit} onChange={(event) => setDraft({ ...draft, limit: Number(event.target.value) })}>
            {limits.map((value) => (
              <option key={value}>{value}</option>
            ))}
          </select>
        </label>
      </div>
    </form>
  );
}

interface Outcome {
  key: string;
  result?: CodeGrepResult;
  patternError?: string;
}

function TextTab() {
  const [parameters, setParameters] = useSearchParams();
  const params = readParams(parameters);
  const key = JSON.stringify(params);
  const outcome = useLoad<Outcome | undefined>(async () => {
    if (!params.pattern) return undefined;
    try {
      return { key, result: await api.codeGrep(params) };
    } catch (exception) {
      if (exception instanceof ApiError && exception.status === 400) {
        const messages = Object.values(exception.fields).flat();
        if (messages.length > 0) return { key, patternError: messages.join(' ') };
      }
      throw exception;
    }
  }, [key]);

  const current = outcome.data?.key === key ? outcome.data : undefined;
  const busy = params.pattern !== '' && current === undefined && !outcome.error;
  const result = current?.result;

  return (
    <>
      <CodeForm key={parameters.toString()} params={params} patternError={current?.patternError} onSearch={(next) => setParameters(writeParams(next))} />
      <ErrorBanner message={outcome.error} />
      {busy && <p className="muted">Searching…</p>}
      {result && (
        <Panel
          title={`${formatNumber(result.matches.length)} matches in ${formatNumber(result.filesMatched)} files`}
          actions={<span className="muted small">{formatNumber(result.elapsedMs)} ms</span>}
        >
          {result.truncated && <div className="banner banner--warn">The list is cut at {formatNumber(params.limit)} matches. Narrow the search or raise the limit.</div>}
          {result.matches.length === 0 && <Empty>Nothing found.</Empty>}
          <MatchFiles matches={result.matches} />
        </Panel>
      )}
    </>
  );
}

const tabs = [
  { name: 'search', label: 'Search' },
  { name: 'text', label: 'Text' },
  { name: 'symbols', label: 'Symbols' },
  { name: 'projects', label: 'Projects' },
] as const;

type Tab = (typeof tabs)[number]['name'];

function readTab(value: string | null): Tab {
  return tabs.find((tab) => tab.name === value)?.name ?? 'search';
}

export function CodePage() {
  const [parameters] = useSearchParams();
  const tab = readTab(parameters.get('tab'));
  const source = parameters.get('source');
  const tabHref = (name: Tab) => {
    const next = new URLSearchParams();
    if (name !== 'search') next.set('tab', name);
    if (source) next.set('source', source);
    const text = next.toString();
    return text ? `/code?${text}` : '/code';
  };

  return (
    <div className="page">
      <div className="tabs" role="tablist">
        {tabs.map((item) => (
          <Link key={item.name} to={tabHref(item.name)} role="tab" aria-selected={tab === item.name} className={tab === item.name ? 'tab tab--on' : 'tab'}>
            {item.label}
          </Link>
        ))}
        <Link to={diagramHref(numberOrNull(source), 'projects')} className="tab">
          Diagrams
        </Link>
      </div>
      {tab === 'search' && <CodeSearchTab />}
      {tab === 'text' && <TextTab />}
      {tab === 'symbols' && <CodeSymbolsTab />}
      {tab === 'projects' && <CodeProjectsTab />}
    </div>
  );
}
