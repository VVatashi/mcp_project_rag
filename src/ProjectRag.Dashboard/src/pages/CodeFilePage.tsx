import { useEffect, useRef, useState } from 'react';
import { Link, useNavigate } from 'react-router';

import { api } from '../api';
import { KindBadge, symbolHref } from '../components/Code';
import { ErrorBanner, Panel } from '../components/Common';
import { formatNumber } from '../format';
import { useLoad } from '../hooks';
import type { CodeLines, OutlineNode } from '../types';

const aroundLine = 300;
const wholeFileLines = 2000;
const stepLines = 500;

async function loadFirst(documentId: number, line: number | null): Promise<CodeLines> {
  if (line === null) return api.codeLines(documentId, 1, wholeFileLines);
  const window = await api.codeLines(documentId, Math.max(1, line - aroundLine), line + aroundLine);
  const whole = window.lineCount <= wholeFileLines && (window.start > 1 || window.end < window.lineCount);
  return whole ? api.codeLines(documentId, 1, window.lineCount) : window;
}

function OutlineItems({ nodes, documentId, onGo }: { nodes: OutlineNode[]; documentId: number; onGo: (line: number) => void }) {
  return (
    <ul className="outline">
      {nodes.map((node) => {
        const line = node.symbol.documentId === documentId ? node.symbol.line : null;
        return (
          <li key={node.symbol.symbolId}>
            <div className="outline__item">
              <KindBadge kind={node.symbol.kind} />
              <button type="button" className="link outline__name" disabled={line === null} title={node.symbol.signature} onClick={() => line !== null && onGo(line)}>
                {node.symbol.name}
              </button>
              <Link className="link small outline__open" to={symbolHref(node.symbol)} title="Open the symbol">
                ↗
              </Link>
            </div>
            {node.children.length > 0 && <OutlineItems nodes={node.children} documentId={documentId} onGo={onGo} />}
          </li>
        );
      })}
    </ul>
  );
}

/** A file of code with line numbers. It shows a window of lines and loads more lines on demand. */
export function CodeFilePage({ documentId, line, end: markEnd }: { documentId: number; line: number | null; end: number | null }) {
  const first = useLoad(() => loadFirst(documentId, line), [documentId, line]);
  const [before, setBefore] = useState<string[]>([]);
  const [after, setAfter] = useState<string[]>([]);
  const [error, setError] = useState<string>();
  const [busy, setBusy] = useState(false);
  const body = useRef<HTMLDivElement>(null);
  const scrolled = useRef(false);
  const navigate = useNavigate();
  const [marked, setMarked] = useState(line);
  const [markedEnd, setMarkedEnd] = useState(markEnd ?? line);
  const [outlineOpen, setOutlineOpen] = useState(() => window.matchMedia('(min-width: 961px)').matches);

  const data = first.data;
  const start = data ? data.start - before.length : 1;
  const end = data ? data.start + data.lines.length - 1 + after.length : 0;

  useEffect(() => {
    if (!data || line === null || scrolled.current) return;
    scrolled.current = true;
    body.current?.querySelector(`[data-line="${line}"]`)?.scrollIntoView({ block: 'center' });
  }, [data, line]);

  const more = async (direction: 'before' | 'after') => {
    if (!data) return;
    setBusy(true);
    setError(undefined);
    try {
      if (direction === 'before') {
        const page = await api.codeLines(documentId, Math.max(1, start - stepLines), start - 1);
        setBefore((lines) => [...page.lines, ...lines]);
      } else {
        const page = await api.codeLines(documentId, end + 1, end + stepLines);
        setAfter((lines) => [...lines, ...page.lines]);
      }
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : String(exception));
    } finally {
      setBusy(false);
    }
  };

  const info = data?.document;
  const hasOutline = info?.language === 'csharp';
  const outline = useLoad(() => (hasOutline ? api.codeOutline(documentId) : Promise.resolve([])), [documentId, hasOutline]);
  const outlineNodes = outline.data ?? [];

  const goTo = (target: number) => {
    if (target < start || target > end) {
      void navigate(`/code/files/${documentId}?line=${target}`);
      return;
    }

    setMarked(target);
    setMarkedEnd(target);
    body.current?.querySelector(`[data-line="${target}"]`)?.scrollIntoView({ block: 'center' });
  };

  const lines = data ? [...before, ...data.lines, ...after] : [];

  return (
    <div className="page">
      <ErrorBanner message={error ?? first.error ?? outline.error} />
      <Link className="link" to="/code">
        ← Code
      </Link>
      {info && data && (
        <Panel title={info.path}>
          <dl className="facts">
            <dt>Source</dt>
            <dd>{info.source}</dd>
            <dt>Language</dt>
            <dd>{info.language ?? '—'}</dd>
            <dt>Lines</dt>
            <dd>{formatNumber(data.lineCount)}</dd>
            <dt>Document</dt>
            <dd>
              <Link className="link" to={`/documents/${info.documentId}`}>
                Open the document page
              </Link>
            </dd>
          </dl>
        </Panel>
      )}
      {data && (
        <div className={hasOutline && outlineNodes.length > 0 && outlineOpen ? 'file-layout file-layout--outline' : 'file-layout'}>
        <Panel
          title={`Lines ${formatNumber(start)}–${formatNumber(end)} of ${formatNumber(data.lineCount)}`}
          actions={
            <>
              {hasOutline && outlineNodes.length > 0 && (
                <button type="button" className="button button--small" onClick={() => setOutlineOpen(!outlineOpen)}>
                  {outlineOpen ? 'Hide outline' : 'Show outline'}
                </button>
              )}
              <button type="button" className="button button--small" disabled={busy || start <= 1} onClick={() => void more('before')}>
                Earlier lines
              </button>
              <button type="button" className="button button--small" disabled={busy || end >= data.lineCount} onClick={() => void more('after')}>
                Later lines
              </button>
            </>
          }
        >
          <div className="code" ref={body}>
            {lines.map((text, index) => {
              const number = start + index;
              return (
                <div key={number} className={marked !== null && markedEnd !== null && number >= marked && number <= markedEnd ?'code__line code__line--on' : 'code__line'} data-line={number} data-n={number}>
                  {text}
                </div>
              );
            })}
          </div>
          {data.lineCount === 0 && <p className="empty">The file is empty.</p>}
        </Panel>
        {hasOutline && outlineNodes.length > 0 && outlineOpen && (
          <aside className="file-layout__outline">
            <Panel title="Outline">
              <OutlineItems nodes={outlineNodes} documentId={documentId} onGo={goTo} />
            </Panel>
          </aside>
        )}
        </div>
      )}
    </div>
  );
}
