import { Link } from 'react-router';

import { Badge } from './Common';
import type { CodeMatch, MatchClass, SymbolMatch, SymbolSummary, UseCount } from '../types';

export const typeKinds = ['class', 'record', 'struct', 'record_struct', 'interface', 'enum', 'delegate'];
export const memberKinds = ['method', 'constructor', 'destructor', 'operator', 'conversion', 'property', 'indexer', 'field', 'constant', 'event', 'enum_member'];
export const useKinds = ['inherit', 'implement', 'signature', 'create', 'call', 'member', 'type', 'attribute'];

type Tone = 'ok' | 'warn' | 'error' | 'muted' | 'info';

function kindTone(kind: string): Tone {
  if (typeKinds.includes(kind)) return 'info';
  if (kind === 'method' || kind === 'constructor' || kind === 'destructor' || kind === 'operator' || kind === 'conversion') return 'ok';
  if (kind === 'property' || kind === 'indexer' || kind === 'event') return 'warn';
  return 'muted';
}

export function kindLabel(kind: string): string {
  return kind.replace('_', ' ');
}

export function KindBadge({ kind }: { kind: string }) {
  return <Badge tone={kindTone(kind)}>{kindLabel(kind)}</Badge>;
}

export function symbolHref(symbol: Pick<SymbolSummary, 'sourceId' | 'docId' | 'project'>): string {
  const parameters = new URLSearchParams({ source: String(symbol.sourceId), id: symbol.docId, project: symbol.project });
  return `/code/symbol?${parameters.toString()}`;
}

export function fileHref(documentId: number, line?: number | null, end?: number | null): string {
  if (!line) return `/code/files/${documentId}`;
  return end && end > line ? `/code/files/${documentId}?line=${line}&end=${end}` : `/code/files/${documentId}?line=${line}`;
}

export function diagramHref(sourceId: number | null, view: string, extra: Record<string, string | null | undefined> = {}): string {
  const parameters = new URLSearchParams({ view });
  if (sourceId !== null) parameters.set('source', String(sourceId));
  for (const [name, value] of Object.entries(extra)) {
    if (value) parameters.set(name, value);
  }
  return `/code/diagram?${parameters.toString()}`;
}

export function symbolSearchHref(sourceId: number | null, text: string): string {
  const parameters = new URLSearchParams({ tab: 'symbols' });
  if (text) parameters.set('q', text);
  if (sourceId !== null) parameters.set('source', String(sourceId));
  return `/code?${parameters.toString()}`;
}

export function SymbolLink({ symbol, full = false }: { symbol: SymbolSummary; full?: boolean }) {
  return (
    <Link className="link mono" to={symbolHref(symbol)}>
      {full ? symbol.fullName : symbol.name}
    </Link>
  );
}

export function FileLink({ documentId, path, line }: { documentId: number; path: string; line?: number | null }) {
  return (
    <Link className="link mono" to={fileHref(documentId, line)}>
      {path}
      {line ? `:${line}` : ''}
    </Link>
  );
}

export function UseChips({ kinds }: { kinds: UseCount[] }) {
  return (
    <span className="chips">
      {kinds.map((use) => (
        <span key={use.kind} className="count-chip">
          {use.kind} <strong>{use.count}</strong>
        </span>
      ))}
    </span>
  );
}

interface HighlightSource {
  text: string;
  textStart: number;
  column: number;
  length: number;
}

export function Highlighted({ match }: { match: HighlightSource }) {
  const cut = match.textStart > 0 ? '…' : '';
  const start = Math.min(Math.max(0, match.column - 1 - match.textStart), match.text.length);
  const end = Math.min(match.text.length, start + Math.max(0, match.length));
  if (end <= start) return <>{cut}{match.text}</>;
  return (
    <>
      {cut}
      {match.text.slice(0, start)}
      <mark>{match.text.slice(start, end)}</mark>
      {match.text.slice(end)}
    </>
  );
}

const matchLabels: Record<MatchClass, string> = {
  exact: 'exact',
  exact_ci: 'exact, any case',
  prefix: 'prefix',
  humps: 'camel humps',
  qualified: 'qualified',
  substring: 'substring',
  fuzzy: 'similar',
};

export function SymbolRow({ match }: { match: SymbolMatch }) {
  return (
    <li className="symbol">
      <div className="symbol__head">
        <KindBadge kind={match.kind} />
        <Link className="link symbol__name" to={symbolHref(match)}>
          {match.name}
        </Link>
        <span className="muted small">{matchLabels[match.match]}</span>
      </div>
      <div className="muted small mono symbol__full">{match.fullName}</div>
      <div className="mono small symbol__signature">{match.signature}</div>
      <div className="small symbol__where">
        <span className="muted">{match.project}</span>
        {match.documentId !== null && match.path && <FileLink documentId={match.documentId} path={match.path} line={match.line} />}
      </div>
      {match.summary && <div className="small symbol__summary">{match.summary}</div>}
    </li>
  );
}

interface FileGroup {
  documentId: number;
  source: string;
  path: string;
  language: string | null;
  matches: CodeMatch[];
}

function groupByFile(matches: CodeMatch[]): FileGroup[] {
  const groups = new Map<number, FileGroup>();
  for (const match of matches) {
    let group = groups.get(match.documentId);
    if (!group) {
      group = { documentId: match.documentId, source: match.source, path: match.path, language: match.language, matches: [] };
      groups.set(match.documentId, group);
    }
    group.matches.push(match);
  }

  return [...groups.values()];
}

export function MatchFiles({ matches }: { matches: CodeMatch[] }) {
  return (
    <div className="code-files">
      {groupByFile(matches).map((group) => (
        <section key={group.documentId} className="code-file">
          <header className="code-file__head">
            <span className="muted small">{group.source}</span>
            <Link className="link mono" to={fileHref(group.documentId)}>
              {group.path}
            </Link>
            {group.language && <span className="muted small">{group.language}</span>}
          </header>
          <ul className="code-file__rows">
            {group.matches.map((match) => (
              <li key={`${match.line}:${match.column}`}>
                <Link className="code-row" to={fileHref(match.documentId, match.line)}>
                  <span className="code-row__line">{match.line}</span>
                  <span className="code-row__text">
                    <Highlighted match={match} />
                  </span>
                </Link>
              </li>
            ))}
          </ul>
        </section>
      ))}
    </div>
  );
}

/** The short name of a symbol from its documentation comment ID, for a search after a rebuild of the graph. */
export function nameFromDocId(docId: string): string {
  const body = docId.replace(/^\w:/, '').replace(/\(.*$/, '').replace(/``?\d+/g, '');
  const parts = body.split('.').filter((part) => part.length > 0);
  const last = parts.at(-1) ?? body;
  if (last === '#ctor' || last === '#cctor') return parts.at(-2) ?? last;
  return last.replace(/^[#~]/, '');
}
