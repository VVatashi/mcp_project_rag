import katex from 'katex';
import { useEffect, useMemo, useRef, useState } from 'react';
import { Link, useLocation, useSearchParams } from 'react-router';

import { api } from '../api';
import { Badge, Empty, ErrorBanner, Pager, Panel } from '../components/Common';
import { Markdown } from '../components/Markdown';
import { PdfPage } from '../components/PdfPage';
import { anchorAddress, decodePart, formatBytes, formatDate, formatNumber, numberOrNull } from '../format';
import { useLoad } from '../hooks';
import type { Formula } from '../types';

const windowLength = 20000;

// The text before the target, so the reader sees its context.
const contextLength = 1000;

// A specification of thousands of pages has thousands of sections and chunks. The lists show them in pages.
const sectionPageSize = 200;
const chunkPageSize = 50;
const formulaPageSize = 50;

const tabs = ['text', 'sections', 'chunks', 'formulas'] as const;

type Tab = (typeof tabs)[number];

function isTab(value: string | undefined): value is Tab {
  return tabs.some((tab) => tab === value);
}

/** The last boundary at or before the value. */
function boundaryBefore(boundaries: number[], value: number): number {
  let result = 0;
  for (const boundary of boundaries) {
    if (boundary > value) break;
    result = boundary;
  }

  return result;
}

// The previous window ends near the start of the current one: it starts at the first boundary of the length before, or at the length without one.
function previousStart(boundaries: number[], offset: number): number {
  const after = boundaryAfter(boundaries, offset - windowLength);
  return after < offset ? Math.max(0, after) : Math.max(0, offset - windowLength);
}

/** The first boundary at or after the value, or the value when no boundary follows. */
function boundaryAfter(boundaries: number[], value: number): number {
  return boundaries.find((boundary) => boundary >= value) ?? value;
}

/**
 * A document with its text, its sections, its chunks and its formulas. The tab is a part of the address, and so is the page of a list (?from=).
 * The text tab shows a window of the Markdown. It shows the place of the offset in the query, or of the anchor in the hash, for example /documents/4/text#p123-2.
 */
export function DocumentPage({ documentId, tab: tabName }: { documentId: number; tab: string | undefined }) {
  const [parameters, setParameters] = useSearchParams();
  const { hash } = useLocation();
  const anchorName = hash.length > 1 ? decodePart(hash.slice(1)) : null;
  const offset = numberOrNull(parameters.get('offset'));
  const sectionId = numberOrNull(parameters.get('section'));
  const from = Math.max(0, numberOrNull(parameters.get('from')) ?? 0);
  const tab: Tab = isTab(tabName) ? tabName : anchorName !== null || offset !== null ? 'text' : 'sections';

  const document = useLoad(() => api.document(documentId), [documentId]);
  // The first chunk gives the counts of the whole document.
  const counts = useLoad(() => api.chunks(documentId, null, 0, 1), [documentId]);
  const sections = useLoad(() => api.sections(documentId), [documentId]);
  const chunks = useLoad(() => (tab === 'chunks' ? api.chunks(documentId, sectionId, from, chunkPageSize) : Promise.resolve(undefined)), [documentId, tab, sectionId, from]);
  const anchor = useLoad(() => (anchorName === null ? Promise.resolve(null) : api.anchor(documentId, anchorName)), [documentId, anchorName]);

  const info = document.data;
  const anchorHref = (name: string) => `/documents/${documentId}/text#${encodeURIComponent(name)}`;
  // The load keeps the previous anchor until the next one comes.
  const target = anchorName === null ? offset : anchor.data?.name === anchorName ? anchor.data.charOffset : null;
  const shownSections = sections.data?.filter((section) => section.level > 0) ?? [];
  const goTo = (value: number) => {
    const next = new URLSearchParams(parameters);
    next.set('from', String(value));
    setParameters(next);
    window.scrollTo({ top: 0 });
  };

  return (
    <div className="page">
      <ErrorBanner message={document.error ?? counts.error ?? sections.error ?? chunks.error ?? (anchor.error && `The anchor “${anchorName}” does not resolve: ${anchor.error}`)} />
      <Link className="link" to="/documents">
        ← Documents
      </Link>
      {info && (
        <Panel title={info.title}>
          <dl className="facts">
            <dt>Category</dt>
            <dd>
              <Link className="link" to={`/documents?category=${encodeURIComponent(info.category)}`}>
                {info.category}
              </Link>
            </dd>
            <dt>Source</dt>
            <dd>{info.source}</dd>
            <dt>Path</dt>
            <dd className="mono">{info.path}</dd>
            <dt>Size</dt>
            <dd>
              {formatBytes(info.sizeBytes)} · {formatNumber(info.contentLength)} characters of Markdown
            </dd>
            <dt>Structure</dt>
            <dd>
              {formatNumber(sections.data?.length ?? 0)} sections · {formatNumber(info.chunkCount)} chunks · {formatNumber(counts.data?.embeddedCount ?? 0)} with vectors
            </dd>
            <dt>Changed</dt>
            <dd>
              {formatDate(info.modifiedAt)} · indexed {formatDate(info.indexedAt)}
            </dd>
          </dl>
        </Panel>
      )}

      <div className="tabs" role="tablist">
        {tabs.map((name) => (
          <Link key={name} to={`/documents/${documentId}/${name}`} role="tab" aria-selected={tab === name} className={tab === name ? 'tab tab--on' : 'tab'}>
            {name[0]?.toUpperCase()}
            {name.slice(1)}
          </Link>
        ))}
      </div>

      {tab === 'text' && <Text documentId={documentId} target={target} anchorHref={anchorHref} />}

      {tab === 'sections' && sections.data && (
        <Panel title={`Sections (${formatNumber(shownSections.length)})`} actions={<Pager offset={from} pageSize={sectionPageSize} total={shownSections.length} onChange={goTo} />}>
          {shownSections.length === 0 && <Empty>No sections.</Empty>}
          <ol className="sections">
            {shownSections.slice(from, from + sectionPageSize).map((section) => (
              <li key={section.sectionId} style={{ paddingLeft: `${(section.level - 1) * 16}px` }}>
                <Link className="link" to={`/documents/${documentId}/chunks?section=${section.sectionId}`}>
                  {section.title}
                </Link>{' '}
                <span className="muted small">
                  {section.pageStart !== null && `p. ${section.pageStart}${section.pageEnd !== section.pageStart ? `–${section.pageEnd}` : ''} · `}
                  {section.chunkCount} chunks
                </span>
              </li>
            ))}
          </ol>
          <Pager offset={from} pageSize={sectionPageSize} total={shownSections.length} onChange={goTo} />
        </Panel>
      )}

      {tab === 'chunks' && chunks.data && (
        <Panel
          title={sectionId === null ? `Chunks (${formatNumber(chunks.data.total)})` : `Chunks of one section (${formatNumber(chunks.data.total)})`}
          actions={
            <>
              {sectionId !== null && (
                <Link className="link small" to={`/documents/${documentId}/chunks`}>
                  Show all
                </Link>
              )}
              <Pager offset={from} pageSize={chunkPageSize} total={chunks.data.total} onChange={goTo} />
            </>
          }
        >
          {chunks.data.items.length === 0 && <Empty>No chunks.</Empty>}
          <ol className="chunks">
            {chunks.data.items.map((chunk) => (
              <li key={chunk.chunkId} className="chunk">
                <div className="chunk__head">
                  <Link className="link chunk__heading" to={`/chunks/${chunk.chunkId}`}>
                    #{chunk.ordinal} · {chunk.heading}
                  </Link>
                  {chunk.hasEmbedding ? <Badge tone="ok">vector</Badge> : <Badge tone="warn">no vector</Badge>}
                  {chunk.overlapChars > 0 && <Badge tone="muted">overlap {chunk.overlapChars}</Badge>}
                  <span className="muted small">
                    {chunk.pageStart !== null && `p. ${chunk.pageStart}${chunk.pageEnd !== chunk.pageStart ? `–${chunk.pageEnd}` : ''} · `}
                    {formatNumber(chunk.body.length)} characters
                  </span>
                </div>
                <Markdown text={chunk.body} anchorHref={(name) => anchorAddress(documentId, name)} />
              </li>
            ))}
          </ol>
          <Pager offset={from} pageSize={chunkPageSize} total={chunks.data.total} onChange={goTo} />
        </Panel>
      )}

      {tab === 'formulas' && info && <Formulas documentId={documentId} isPdf={info.path.toLowerCase().endsWith('.pdf')} from={from} onPage={goTo} />}
    </div>
  );
}

/**
 * A window of the Markdown of a document. The window starts and ends at the starts of blocks, which the server gives.
 * With a target the window holds it, and the page scrolls to the block at the target and marks it.
 */
function Text({ documentId, target, anchorHref }: { documentId: number; target: number | null; anchorHref: (name: string) => string }) {
  const loaded = useLoad(() => api.boundaries(documentId), [documentId]);
  const boundaries = useMemo(() => [0, ...(loaded.data ?? [])], [loaded.data]);
  const [source, setSource] = useState(false);
  // The buttons move the window away from the target to a start of their own. A new target brings the window back to it.
  // A long stretch without the start of a block, such as a large table, starts the window right before the target.
  const [moved, setMoved] = useState<{ target: number | null; start: number } | null>(null);
  const wanted = Math.max(0, (target ?? 0) - contextLength);
  const snapped = boundaryBefore(boundaries, wanted);
  const start = moved && moved.target === target ? moved.start : target !== null && target - snapped > windowLength / 2 ? wanted : snapped;
  const content = useLoad(() => (loaded.data ? api.content(documentId, start, windowLength) : Promise.resolve(undefined)), [documentId, start, loaded.data]);
  const textRef = useRef<HTMLDivElement>(null);
  const markRef = useRef<HTMLElement>(null);

  const data = content.data?.offset === start ? content.data : undefined;
  // The window ends before the last boundary in it, so the next window starts there.
  const fullEnd = data ? data.offset + data.text.length : 0;
  const last = data && fullEnd < data.totalLength ? boundaryBefore(boundaries, fullEnd) : fullEnd;
  const end = data && last > data.offset ? last : fullEnd;
  const text = data ? data.text.slice(0, end - data.offset) : '';
  const hit = data && target !== null && target >= data.offset && target < end ? target - data.offset : null;

  useEffect(() => {
    if (hit === null) return;
    if (source) {
      markRef.current?.scrollIntoView({ block: 'center' });
      return;
    }

    const blocks = [...(textRef.current?.querySelectorAll<HTMLElement>('[data-end]') ?? [])];
    const block = blocks.find((element) => Number(element.dataset.end) > (target ?? 0));
    if (!block) return;
    block.classList.add('markdown__target');
    block.scrollIntoView({ block: 'start' });
    return () => block.classList.remove('markdown__target');
  }, [text, hit, target, source]);

  if (loaded.error ?? content.error) return <ErrorBanner message={loaded.error ?? content.error} />;
  if (!data) return <p className="muted">Loading the text…</p>;

  return (
    <Panel
      title={`Characters ${formatNumber(data.offset)}–${formatNumber(end)} of ${formatNumber(data.totalLength)}`}
      actions={
        <>
          <label className="inline small">
            <input type="checkbox" checked={source} onChange={(event) => setSource(event.target.checked)} />
            Markdown source
          </label>
          <button type="button" className="button button--small" disabled={data.offset === 0} onClick={() => setMoved({ target, start: previousStart(boundaries, data.offset) })}>
            Previous
          </button>
          <button type="button" className="button button--small" disabled={end >= data.totalLength} onClick={() => setMoved({ target, start: end })}>
            Next
          </button>
        </>
      }
    >
      {source ? (
        <pre className="document-text">
          {hit === null ? (
            text
          ) : (
            <>
              {text.slice(0, hit)}
              <mark ref={markRef}>{text.slice(hit, hit + 1)}</mark>
              {text.slice(hit + 1)}
            </>
          )}
        </pre>
      ) : (
        <div ref={textRef}>
          <Markdown text={text} offset={data.offset} anchorHref={anchorHref} />
        </div>
      )}
    </Panel>
  );
}

interface CheckedFormula extends Formula {
  error: string | null;
}

// KaTeX checks each formula, as the dashboard renders it. The check needs no rendering of the page, so it covers all formulas.
function check(formula: Formula): CheckedFormula {
  try {
    katex.renderToString(formula.latex, { displayMode: true, throwOnError: true, strict: 'ignore' });
    return { ...formula, error: null };
  } catch (exception) {
    return { ...formula, error: exception instanceof Error ? exception.message : String(exception) };
  }
}

function Formulas({ documentId, isPdf, from, onPage }: { documentId: number; isPdf: boolean; from: number; onPage: (from: number) => void }) {
  const loaded = useLoad(() => api.formulas(documentId), [documentId]);
  const formulas = useMemo(() => (loaded.data ?? []).map(check), [loaded.data]);
  const [onlyErrors, setOnlyErrors] = useState(false);
  const [page, setPage] = useState<number | null>(null);
  const errors = formulas.filter((formula) => formula.error !== null).length;
  const shown = onlyErrors ? formulas.filter((formula) => formula.error !== null) : formulas;

  if (loaded.error) return <ErrorBanner message={loaded.error} />;
  if (!loaded.data) return <p className="muted">Loading the formulas…</p>;

  return (
    <div className={isPdf && page !== null ? 'split' : undefined}>
      <Panel
        title={`${formatNumber(formulas.length)} display formulas · ${errors} KaTeX errors`}
        actions={
          <>
            <label className="inline small">
              <input
                type="checkbox"
                checked={onlyErrors}
                onChange={(event) => {
                  setOnlyErrors(event.target.checked);
                  onPage(0);
                }}
              />
              Only errors
            </label>
            <Pager offset={from} pageSize={formulaPageSize} total={shown.length} onChange={onPage} />
          </>
        }
      >
        {shown.length === 0 && <Empty>No formulas.</Empty>}
        <ol className="formulas">
          {shown.slice(from, from + formulaPageSize).map((formula) => (
            <li key={formula.offset} className={formula.error ? 'formula formula--error' : 'formula'}>
              <div className="formula__head small muted">
                {formula.page !== null && isPdf ? (
                  <button type="button" className="link" onClick={() => setPage(formula.page)}>
                    Page {formula.page}
                  </button>
                ) : (
                  formula.page !== null && `Page ${formula.page}`
                )}
                {formula.error && <span className="formula__error"> · {formula.error}</span>}
              </div>
              <Markdown text={`$$\n${formula.latex}\n$$`} />
              <pre className="formula__source">{formula.latex}</pre>
            </li>
          ))}
        </ol>
        <Pager offset={from} pageSize={formulaPageSize} total={shown.length} onChange={onPage} />
      </Panel>
      {isPdf && page !== null && (
        <Panel title={`Original, page ${page}`}>
          <PdfPage documentId={documentId} page={page} />
        </Panel>
      )}
    </div>
  );
}
