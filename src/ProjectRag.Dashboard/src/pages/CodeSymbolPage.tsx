import { useState } from 'react';
import { Link } from 'react-router';

import { ApiError, api } from '../api';
import { Diagram } from '../components/Diagram';
import { FileLink, Highlighted, KindBadge, SymbolLink, UseChips, diagramHref, fileHref, nameFromDocId, symbolHref, symbolSearchHref, typeKinds, useKinds } from '../components/Code';
import { Empty, ErrorBanner, Pager, Panel } from '../components/Common';
import { formatNumber } from '../format';
import { useLoad } from '../hooks';
import type { CodeReference, EdgeSummary, SymbolDetail, SymbolSummary } from '../types';

const collapsedRows = 20;
const referencePageSize = 100;

interface Outcome {
  key: string;
  detail: SymbolDetail | null;
}

function SymbolList({ symbols }: { symbols: SymbolSummary[] }) {
  return (
    <ul className="plain-list">
      {symbols.map((symbol) => (
        <li key={symbol.symbolId}>
          <KindBadge kind={symbol.kind} /> <SymbolLink symbol={symbol} full />
        </li>
      ))}
    </ul>
  );
}

function Members({ members }: { members: SymbolSummary[] }) {
  return (
    <div className="table-wrap">
      <table className="table table--compact">
        <thead>
          <tr>
            <th>Kind</th>
            <th>Name</th>
            <th>Signature</th>
            <th className="num">Line</th>
          </tr>
        </thead>
        <tbody>
          {members.map((member) => (
            <tr key={member.symbolId}>
              <td>
                <KindBadge kind={member.kind} />
              </td>
              <td>
                <SymbolLink symbol={member} />
              </td>
              <td className="mono small">{member.signature}</td>
              <td className="num small">
                {member.documentId !== null && member.line !== null ? (
                  <Link className="link" to={fileHref(member.documentId, member.line)}>
                    {member.line}
                  </Link>
                ) : (
                  '—'
                )}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function Edges({ title, edges }: { title: string; edges: EdgeSummary[] }) {
  const [all, setAll] = useState(false);
  if (edges.length === 0) return null;
  const shown = all ? edges : edges.slice(0, collapsedRows);
  const total = edges.reduce((sum, edge) => sum + edge.total, 0);

  return (
    <Panel title={`${title} (${formatNumber(edges.length)} symbols, ${formatNumber(total)} uses)`}>
      <div className="table-wrap">
        <table className="table table--compact">
          <thead>
            <tr>
              <th>Symbol</th>
              <th>Uses</th>
              <th className="num">Total</th>
            </tr>
          </thead>
          <tbody>
            {shown.map((edge) => (
              <tr key={edge.symbol.symbolId}>
                <td>
                  <KindBadge kind={edge.symbol.kind} /> <SymbolLink symbol={edge.symbol} full />
                </td>
                <td>
                  <UseChips kinds={edge.kinds} />
                </td>
                <td className="num">{formatNumber(edge.total)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {edges.length > collapsedRows && (
        <button type="button" className="button button--small show-all" onClick={() => setAll(!all)}>
          {all ? 'Show fewer' : `Show all ${formatNumber(edges.length)}`}
        </button>
      )}
    </Panel>
  );
}

function ExternalUses({ detail }: { detail: SymbolDetail }) {
  if (detail.externalUses.length === 0) return null;
  return (
    <details className="panel details">
      <summary>External uses ({formatNumber(detail.externalUses.length)})</summary>
      <div className="table-wrap">
        <table className="table table--compact">
          <thead>
            <tr>
              <th>Type</th>
              <th>Assembly</th>
              <th>Uses</th>
              <th className="num">Total</th>
            </tr>
          </thead>
          <tbody>
            {detail.externalUses.map((use) => (
              <tr key={`${use.assembly}:${use.target}`}>
                <td className="mono small">{use.target}</td>
                <td className="small">{use.assembly}</td>
                <td>
                  <UseChips kinds={use.kinds} />
                </td>
                <td className="num">{formatNumber(use.total)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </details>
  );
}

function groupByFile(items: CodeReference[]): { documentId: number; path: string; items: CodeReference[] }[] {
  const groups: { documentId: number; path: string; items: CodeReference[] }[] = [];
  for (const item of items) {
    const last = groups.at(-1);
    if (last && last.documentId === item.documentId) last.items.push(item);
    else groups.push({ documentId: item.documentId, path: item.path, items: [item] });
  }

  return groups;
}

function References({ symbolId, count }: { symbolId: number; count: number }) {
  const [offset, setOffset] = useState(0);
  const [kind, setKind] = useState('');
  const page = useLoad(() => api.codeReferences(symbolId, offset, referencePageSize, kind || null), [symbolId, offset, kind]);
  const data = page.data;
  const groups = data ? groupByFile(data.items) : [];

  return (
    <Panel
      title={`References (${formatNumber(data?.total ?? count)})`}
      actions={
        <label className="inline small">
          Kind
          <select
            value={kind}
            onChange={(event) => {
              setKind(event.target.value);
              setOffset(0);
            }}
          >
            <option value="">Any</option>
            {useKinds.map((item) => (
              <option key={item} value={item}>
                {item}
              </option>
            ))}
          </select>
        </label>
      }
    >
      <ErrorBanner message={page.error} />
      {data && data.items.length === 0 && <Empty>No references.</Empty>}
      <div className="code-files">
        {groups.map((group) => (
          <section key={`${group.documentId}:${group.items[0]?.line}`} className="code-file">
            <header className="code-file__head">
              <FileLink documentId={group.documentId} path={group.path} />
            </header>
            <ul className="code-file__rows">
              {group.items.map((item) => (
                <li key={`${item.line}:${item.column}`} className="ref-row">
                  <Link className="code-row__line ref-row__line" to={fileHref(item.documentId, item.line)}>
                    {item.line}
                  </Link>
                  <span className="count-chip">{item.kind}</span>
                  <span className="code-row__text ref-row__text">
                    <Highlighted match={item} />
                  </span>
                  {item.from && (
                    <span className="ref-row__from small">
                      in <SymbolLink symbol={item.from} />
                    </span>
                  )}
                </li>
              ))}
            </ul>
          </section>
        ))}
      </div>
      {data && <Pager offset={offset} pageSize={referencePageSize} total={data.total} onChange={setOffset} />}
    </Panel>
  );
}

function InlineDiagram({ symbol }: { symbol: SymbolSummary }) {
  const key = `${symbol.sourceId}:${symbol.docId}:${symbol.project}`;
  const outcome = useLoad(
    async () => ({ key, diagram: await api.codeDiagram('type', { sourceId: symbol.sourceId, docId: symbol.docId, project: symbol.project, depth: 1, maxNodes: 20, members: 'none' }) }),
    [key],
  );
  const current = outcome.data?.key === key ? outcome.data.diagram : undefined;

  return (
    <Panel
      title="Diagram"
      actions={
        <Link className="button button--small" to={diagramHref(symbol.sourceId, 'type', { id: symbol.docId, project: symbol.project })}>
          Open diagram
        </Link>
      }
    >
      <ErrorBanner message={outcome.error} />
      {!current && !outcome.error && <p className="muted">Building the diagram…</p>}
      {current && current.nodes.length <= 1 && <Empty>This type has no neighbors.</Empty>}
      {current && current.nodes.length > 1 && <Diagram diagram={current} height={360} wheel="modified" />}
    </Panel>
  );
}

function Detail({ detail }: { detail: SymbolDetail }) {
  const { symbol } = detail;
  const isType = typeKinds.includes(symbol.kind);
  const memberTarget = !isType && detail.parent && typeKinds.includes(detail.parent.kind) ? detail.parent : symbol;

  return (
    <>
      <Panel
        title={symbol.fullName}
        actions={
          <>
            <KindBadge kind={symbol.kind} />
            {!isType && (
              <Link className="button button--small" to={diagramHref(memberTarget.sourceId, 'type', { id: memberTarget.docId, project: memberTarget.project })}>
                Open diagram
              </Link>
            )}
            {detail.modifiers.map((modifier) => (
              <span key={modifier} className="count-chip">
                {modifier}
              </span>
            ))}
          </>
        }
      >
        <div className="mono symbol__signature">{symbol.signature}</div>
        {symbol.summary && <p className="symbol__summary">{symbol.summary}</p>}
        <dl className="facts">
          <dt>Codebase</dt>
          <dd>{symbol.source}</dd>
          <dt>Project</dt>
          <dd>{symbol.project}</dd>
          {detail.parent && (
            <>
              <dt>Parent</dt>
              <dd>
                <KindBadge kind={detail.parent.kind} /> <SymbolLink symbol={detail.parent} full />
              </dd>
            </>
          )}
          <dt>Declarations</dt>
          <dd>
            {detail.declarations.length === 0 && '—'}
            {detail.declarations.map((declaration) => (
              <div key={`${declaration.documentId}:${declaration.nameLine}`}>
                <FileLink documentId={declaration.documentId} path={declaration.path} line={declaration.nameLine} />
              </div>
            ))}
          </dd>
          <dt>References</dt>
          <dd>{formatNumber(detail.referenceCount)}</dd>
        </dl>
      </Panel>

      {isType && <InlineDiagram key={symbol.symbolId} symbol={symbol} />}

      {isType && detail.members.length > 0 && (
        <Panel title={`Members (${formatNumber(detail.members.length)})`}>
          <Members members={detail.members} />
        </Panel>
      )}

      {(detail.bases.length > 0 || detail.derived.length > 0) && (
        <div className="split">
          {detail.bases.length > 0 && (
            <Panel title="Bases">
              <SymbolList symbols={detail.bases} />
            </Panel>
          )}
          {detail.derived.length > 0 && (
            <Panel title="Derived">
              <SymbolList symbols={detail.derived} />
            </Panel>
          )}
        </div>
      )}

      {(detail.overrides.length > 0 || detail.overriddenBy.length > 0) && (
        <div className="split">
          {detail.overrides.length > 0 && (
            <Panel title="Overrides">
              <SymbolList symbols={detail.overrides} />
            </Panel>
          )}
          {detail.overriddenBy.length > 0 && (
            <Panel title="Overridden by">
              <SymbolList symbols={detail.overriddenBy} />
            </Panel>
          )}
        </div>
      )}

      <Edges title="Uses" edges={detail.uses} />
      <Edges title="Used by" edges={detail.usedBy} />
      <ExternalUses detail={detail} />
      <References key={symbol.symbolId} symbolId={symbol.symbolId} count={detail.referenceCount} />
    </>
  );
}

/** A symbol of the code graph. The address holds the documentation comment ID, because symbol ids change when the graph is built again. */
export function CodeSymbolPage({ sourceId, docId, project }: { sourceId: number; docId: string; project: string | null }) {
  const key = `${sourceId}:${docId}:${project ?? ''}`;
  const outcome = useLoad<Outcome>(async () => {
    try {
      return { key, detail: await api.codeSymbolByDocId(sourceId, docId, project) };
    } catch (exception) {
      if (exception instanceof ApiError && exception.status === 404) return { key, detail: null };
      throw exception;
    }
  }, [key]);
  const current = outcome.data?.key === key ? outcome.data : undefined;

  return (
    <div className="page">
      <Link className="link" to={symbolSearchHref(sourceId, '')}>
        ← Symbols
      </Link>
      <ErrorBanner message={outcome.error} />
      {current?.detail === null && (
        <div className="banner banner--warn">
          This symbol is gone, probably because the graph was built again.{' '}
          <Link className="link" to={symbolSearchHref(sourceId, nameFromDocId(docId))}>
            Search for {nameFromDocId(docId)}
          </Link>
        </div>
      )}
      {current?.detail && <Detail key={symbolHref(current.detail.symbol)} detail={current.detail} />}
    </div>
  );
}
