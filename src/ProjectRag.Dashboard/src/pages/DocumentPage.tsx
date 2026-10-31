import { useEffect, useRef, useState } from 'react';

import { api } from '../api';
import { Badge, ErrorBanner, Panel } from '../components/Common';
import { formatBytes, formatDate, formatNumber } from '../format';
import { useLoad } from '../hooks';

const windowLength = 20000;

export function DocumentPage({ documentId, offset, onBack }: { documentId: number; offset: number | null; onBack: () => void }) {
  const [tab, setTab] = useState<'text' | 'chunks'>(offset === null ? 'chunks' : 'text');
  // The window of text starts a little before the hit, so the reader sees its context.
  const [start, setStart] = useState(Math.max(0, (offset ?? 0) - 2000));
  const document = useLoad(() => api.document(documentId), [documentId]);
  const content = useLoad(() => api.content(documentId, start, windowLength), [documentId, start]);
  const chunks = useLoad(() => api.chunks(documentId), [documentId]);
  const markRef = useRef<HTMLElement>(null);

  useEffect(() => {
    markRef.current?.scrollIntoView({ block: 'center' });
  }, [content.data]);

  const data = content.data;
  const info = document.data;
  const embedded = chunks.data?.filter((chunk) => chunk.hasEmbedding).length ?? 0;
  const hit = offset !== null && data && offset >= data.offset && offset < data.offset + data.text.length ? offset - data.offset : null;
  const hitChunk = chunks.data?.find((chunk) => chunk.charStart === offset);
  const hitLength = hitChunk ? hitChunk.charEnd - hitChunk.charStart : 0;

  return (
    <div className="page">
      <ErrorBanner message={document.error ?? content.error ?? chunks.error} />
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
              {formatBytes(info.sizeBytes)} · {formatNumber(info.contentLength)} characters of text
            </dd>
            <dt>Chunks</dt>
            <dd>
              {formatNumber(info.chunkCount)} · {formatNumber(embedded)} with vectors
            </dd>
            <dt>Changed</dt>
            <dd>
              {formatDate(info.modifiedAt)} · indexed {formatDate(info.indexedAt)}
            </dd>
          </dl>
        </Panel>
      )}

      <div className="tabs" role="tablist">
        <button type="button" role="tab" aria-selected={tab === 'text'} className={tab === 'text' ? 'tab tab--on' : 'tab'} onClick={() => setTab('text')}>
          Text
        </button>
        <button type="button" role="tab" aria-selected={tab === 'chunks'} className={tab === 'chunks' ? 'tab tab--on' : 'tab'} onClick={() => setTab('chunks')}>
          Chunks
        </button>
      </div>

      {tab === 'text' && data && (
        <Panel
          title={`Characters ${formatNumber(data.offset)}–${formatNumber(data.offset + data.text.length)} of ${formatNumber(data.totalLength)}`}
          actions={
            <>
              <button type="button" className="button button--small" disabled={data.offset === 0} onClick={() => setStart(Math.max(0, data.offset - windowLength))}>
                Previous
              </button>
              <button
                type="button"
                className="button button--small"
                disabled={data.offset + data.text.length >= data.totalLength}
                onClick={() => setStart(data.offset + data.text.length)}
              >
                Next
              </button>
            </>
          }
        >
          <pre className="document-text">
            {hit === null ? (
              data.text
            ) : (
              <>
                {data.text.slice(0, hit)}
                <mark ref={markRef}>{data.text.slice(hit, hit + hitLength)}</mark>
                {data.text.slice(hit + hitLength)}
              </>
            )}
          </pre>
        </Panel>
      )}

      {tab === 'chunks' && chunks.data && (
        <ol className="chunks">
          {chunks.data.map((chunk) => (
            <li key={chunk.chunkId} className="chunk">
              <div className="chunk__head">
                <span className="chunk__heading">
                  #{chunk.ordinal} · {chunk.heading}
                </span>
                {chunk.hasEmbedding ? <Badge tone="ok">vector</Badge> : <Badge tone="warn">no vector</Badge>}
                <span className="muted small">
                  {formatNumber(chunk.charStart)}–{formatNumber(chunk.charEnd)}
                </span>
              </div>
              <p className="chunk__body">{chunk.body}</p>
            </li>
          ))}
        </ol>
      )}
    </div>
  );
}
