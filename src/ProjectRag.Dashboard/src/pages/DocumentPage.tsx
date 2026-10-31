import katex from 'katex';
import { useEffect, useMemo, useRef, useState } from 'react';

import { api } from '../api';
import { Badge, Empty, ErrorBanner, Panel } from '../components/Common';
import { Markdown } from '../components/Markdown';
import { PdfPage } from '../components/PdfPage';
import { formatBytes, formatDate, formatNumber } from '../format';
import { useLoad } from '../hooks';

const windowLength = 20000;

type Tab = 'text' | 'sections' | 'chunks' | 'formulas';

interface Formula {
  latex: string;
  offset: number;
  page: number | null;
  error: string | null;
}

// The display formulas of the content with their pages. KaTeX checks each one, as the dashboard renders it.
function findFormulas(content: string): Formula[] {
  const pages = [...content.matchAll(/<!-- rag:page (\d+) -->/g)].map((match) => ({ offset: match.index, page: Number(match[1]) }));
  const formulas: Formula[] = [];
  for (const match of content.matchAll(/^\$\$\n([\s\S]*?)\n\$\$$/gm)) {
    const latex = match[1] ?? '';
    let error: string | null = null;
    try {
      katex.renderToString(latex, { displayMode: true, throwOnError: true, strict: 'ignore' });
    } catch (exception) {
      error = exception instanceof Error ? exception.message : String(exception);
    }

    const page = pages.filter((mark) => mark.offset <= match.index).at(-1)?.page ?? null;
    formulas.push({ latex, offset: match.index, page, error });
  }

  return formulas;
}

async function loadContent(documentId: number): Promise<string> {
  let text = '';
  for (let offset = 0; ; ) {
    const part = await api.content(documentId, offset, 100000);
    text += part.text;
    if (part.text.length === 0 || part.offset + part.text.length >= part.totalLength) return text;
    offset = part.offset + part.text.length;
  }
}

export function DocumentPage({ documentId, offset, onBack, onOpenChunk }: { documentId: number; offset: number | null; onBack: () => void; onOpenChunk: (chunkId: number) => void }) {
  const [tab, setTab] = useState<Tab>(offset === null ? 'sections' : 'text');
  const [source, setSource] = useState(false);
  // The window of text starts a little before the hit, so the reader sees its context.
  const [start, setStart] = useState(Math.max(0, (offset ?? 0) - 2000));
  const [sectionId, setSectionId] = useState<number | null>(null);
  const document = useLoad(() => api.document(documentId), [documentId]);
  const content = useLoad(() => api.content(documentId, start, windowLength), [documentId, start]);
  const chunks = useLoad(() => api.chunks(documentId), [documentId]);
  const sections = useLoad(() => api.sections(documentId), [documentId]);
  const markRef = useRef<HTMLElement>(null);

  useEffect(() => {
    markRef.current?.scrollIntoView({ block: 'center' });
  }, [content.data]);

  const data = content.data;
  const info = document.data;
  const embedded = chunks.data?.filter((chunk) => chunk.hasEmbedding).length ?? 0;
  const hit = offset !== null && data && offset >= data.offset && offset < data.offset + data.text.length ? offset - data.offset : null;
  const shownChunks = chunks.data?.filter((chunk) => sectionId === null || chunk.sectionId === sectionId) ?? [];

  return (
    <div className="page">
      <ErrorBanner message={document.error ?? content.error ?? chunks.error ?? sections.error} />
      <button type="button" className="link" onClick={onBack}>
        ← Documents
      </button>
      {info && (
        <Panel title={info.title}>
          <dl className="facts">
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
              {formatNumber(sections.data?.length ?? 0)} sections · {formatNumber(info.chunkCount)} chunks · {formatNumber(embedded)} with vectors
            </dd>
            <dt>Changed</dt>
            <dd>
              {formatDate(info.modifiedAt)} · indexed {formatDate(info.indexedAt)}
            </dd>
          </dl>
        </Panel>
      )}

      <div className="tabs" role="tablist">
        {(['text', 'sections', 'chunks', 'formulas'] as const).map((name) => (
          <button key={name} type="button" role="tab" aria-selected={tab === name} className={tab === name ? 'tab tab--on' : 'tab'} onClick={() => setTab(name)}>
            {name[0]?.toUpperCase()}
            {name.slice(1)}
          </button>
        ))}
      </div>

      {tab === 'text' && data && (
        <Panel
          title={`Characters ${formatNumber(data.offset)}–${formatNumber(data.offset + data.text.length)} of ${formatNumber(data.totalLength)}`}
          actions={
            <>
              <label className="inline small">
                <input type="checkbox" checked={source} onChange={(event) => setSource(event.target.checked)} />
                Markdown source
              </label>
              <button type="button" className="button button--small" disabled={data.offset === 0} onClick={() => setStart(Math.max(0, data.offset - windowLength))}>
                Previous
              </button>
              <button type="button" className="button button--small" disabled={data.offset + data.text.length >= data.totalLength} onClick={() => setStart(data.offset + data.text.length)}>
                Next
              </button>
            </>
          }
        >
          {source || hit !== null ? (
            <pre className="document-text">
              {hit === null ? (
                data.text
              ) : (
                <>
                  {data.text.slice(0, hit)}
                  <mark ref={markRef}>{data.text.slice(hit, hit + 1)}</mark>
                  {data.text.slice(hit + 1)}
                </>
              )}
            </pre>
          ) : (
            <Markdown text={data.text} />
          )}
        </Panel>
      )}

      {tab === 'sections' && sections.data && (
        <Panel title="Sections">
          {sections.data.length === 0 && <Empty>No sections.</Empty>}
          <ol className="sections">
            {sections.data
              .filter((section) => section.level > 0)
              .map((section) => (
                <li key={section.sectionId} style={{ paddingLeft: `${(section.level - 1) * 16}px` }}>
                  <button
                    type="button"
                    className="link"
                    onClick={() => {
                      setSectionId(section.sectionId);
                      setTab('chunks');
                    }}
                  >
                    {section.title}
                  </button>{' '}
                  <span className="muted small">
                    {section.pageStart !== null && `p. ${section.pageStart}${section.pageEnd !== section.pageStart ? `–${section.pageEnd}` : ''} · `}
                    {section.chunkCount} chunks
                  </span>
                </li>
              ))}
          </ol>
        </Panel>
      )}

      {tab === 'chunks' && chunks.data && (
        <>
          {sectionId !== null && (
            <p className="muted small">
              The chunks of one section.{' '}
              <button type="button" className="link" onClick={() => setSectionId(null)}>
                Show all
              </button>
            </p>
          )}
          <ol className="chunks">
            {shownChunks.map((chunk) => (
              <li key={chunk.chunkId} className="chunk">
                <div className="chunk__head">
                  <button type="button" className="link chunk__heading" onClick={() => onOpenChunk(chunk.chunkId)}>
                    #{chunk.ordinal} · {chunk.heading}
                  </button>
                  {chunk.hasEmbedding ? <Badge tone="ok">vector</Badge> : <Badge tone="warn">no vector</Badge>}
                  {chunk.overlapChars > 0 && <Badge tone="muted">overlap {chunk.overlapChars}</Badge>}
                  <span className="muted small">
                    {chunk.pageStart !== null && `p. ${chunk.pageStart}${chunk.pageEnd !== chunk.pageStart ? `–${chunk.pageEnd}` : ''} · `}
                    {formatNumber(chunk.body.length)} characters
                  </span>
                </div>
                <Markdown text={chunk.body} />
              </li>
            ))}
          </ol>
        </>
      )}

      {tab === 'formulas' && info && <Formulas documentId={documentId} isPdf={info.path.toLowerCase().endsWith('.pdf')} />}
    </div>
  );
}

function Formulas({ documentId, isPdf }: { documentId: number; isPdf: boolean }) {
  const content = useLoad(() => loadContent(documentId), [documentId]);
  const formulas = useMemo(() => (content.data ? findFormulas(content.data) : []), [content.data]);
  const [onlyErrors, setOnlyErrors] = useState(false);
  const [page, setPage] = useState<number | null>(null);
  const errors = formulas.filter((formula) => formula.error !== null).length;
  const shown = onlyErrors ? formulas.filter((formula) => formula.error !== null) : formulas;

  if (content.error) return <ErrorBanner message={content.error} />;
  if (!content.data) return <p className="muted">Loading the document…</p>;

  return (
    <div className={isPdf && page !== null ? 'split' : undefined}>
      <Panel
        title={`${formatNumber(formulas.length)} display formulas · ${errors} KaTeX errors`}
        actions={
          <label className="inline small">
            <input type="checkbox" checked={onlyErrors} onChange={(event) => setOnlyErrors(event.target.checked)} />
            Only errors
          </label>
        }
      >
        {shown.length === 0 && <Empty>No formulas.</Empty>}
        <ol className="formulas">
          {shown.map((formula) => (
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
      </Panel>
      {isPdf && page !== null && (
        <Panel title={`Original, page ${page}`}>
          <PdfPage documentId={documentId} page={page} />
        </Panel>
      )}
    </div>
  );
}
