import { useEffect, useState } from 'react';

import { api } from '../api';
import { Badge, Empty, ErrorBanner, Panel } from '../components/Common';
import { Markdown } from '../components/Markdown';
import { PdfPage } from '../components/PdfPage';
import { useLoad } from '../hooks';
import type { ChunkDetail } from '../types';

function pages(chunk: ChunkDetail): string {
  if (chunk.pageStart === null) return '';
  return chunk.pageEnd !== null && chunk.pageEnd !== chunk.pageStart ? `pages ${chunk.pageStart}–${chunk.pageEnd}` : `page ${chunk.pageStart}`;
}

/**
 * A chunk with its place: the section, the pages, the neighbors, the links with their targets and the links to it.
 * A PDF document shows its page next to the Markdown, so a reader can compare the import with the original.
 */
export function ChunkPage({ chunkId, follow, onOpenChunk, onOpenDocument }: { chunkId: number; follow: string | null; onOpenChunk: (chunkId: number, follow?: string) => void; onOpenDocument: (documentId: number, offset: number) => void }) {
  const chunk = useLoad(() => api.chunk(chunkId), [chunkId]);
  const [page, setPage] = useState<number | null>(null);
  const data = chunk.data;

  // A link of a hit names only an anchor. The chunk knows the target of each of its anchors.
  useEffect(() => {
    if (!data || !follow) return;
    const target = data.links.find((link) => link.targetAnchor === follow && link.targetChunkId !== null);
    if (target?.targetChunkId) onOpenChunk(target.targetChunkId);
  }, [data, follow, onOpenChunk]);

  const isPdf = data?.path.toLowerCase().endsWith('.pdf') ?? false;
  const shownPage = page ?? data?.pageStart ?? null;

  return (
    <div className="page">
      <ErrorBanner message={chunk.error} />
      {data && (
        <>
          <Panel
            title={data.heading}
            actions={
              <>
                <button type="button" className="button button--small" disabled={data.previousChunkId === null} onClick={() => data.previousChunkId && onOpenChunk(data.previousChunkId)}>
                  ← Previous
                </button>
                <button type="button" className="button button--small" disabled={data.nextChunkId === null} onClick={() => data.nextChunkId && onOpenChunk(data.nextChunkId)}>
                  Next →
                </button>
              </>
            }
          >
            <p className="muted small">
              {data.source} ·{' '}
              <button type="button" className="link" onClick={() => onOpenDocument(data.documentId, data.charStart)}>
                {data.path}
              </button>{' '}
              · chunk #{data.ordinal} {pages(data) && `· ${pages(data)}`} {data.overlapChars > 0 && `· repeats ${data.overlapChars} characters of the previous chunk`}{' '}
              {data.hasEmbedding ? <Badge tone="ok">vector</Badge> : <Badge tone="warn">no vector</Badge>}
            </p>
          </Panel>

          <div className={isPdf && shownPage !== null ? 'split' : undefined}>
            <Panel title="Text">
              <Markdown text={data.body} onAnchor={(anchor) => onOpenChunk(data.chunkId, anchor)} />
            </Panel>
            {isPdf && shownPage !== null && (
              <Panel
                title={`Original, page ${shownPage}`}
                actions={
                  data.pageEnd !== null && data.pageEnd !== data.pageStart ? (
                    <select value={shownPage} onChange={(event) => setPage(Number(event.target.value))}>
                      {Array.from({ length: data.pageEnd - (data.pageStart ?? data.pageEnd) + 1 }, (_, index) => (data.pageStart ?? 0) + index).map((value) => (
                        <option key={value} value={value}>
                          Page {value}
                        </option>
                      ))}
                    </select>
                  ) : undefined
                }
              >
                <PdfPage documentId={data.documentId} page={shownPage} />
              </Panel>
            )}
          </div>

          <div className="split">
            <Panel title={`Links (${data.links.length})`}>
              {data.links.length === 0 && <Empty>The chunk links nowhere.</Empty>}
              <ul className="links">
                {data.links.map((link, index) => (
                  <li key={index}>
                    <Badge tone={link.kind === 'link' ? 'info' : 'muted'}>{link.kind}</Badge> <strong>{link.label}</strong> →{' '}
                    {link.targetChunkId !== null ? (
                      <button type="button" className="link" onClick={() => link.targetChunkId && onOpenChunk(link.targetChunkId)}>
                        {link.targetHeading}
                        {link.targetPage !== null && `, page ${link.targetPage}`}
                      </button>
                    ) : (
                      <span className="muted">
                        {link.targetPath ?? 'this document'}#{link.targetAnchor} (not found)
                      </span>
                    )}
                  </li>
                ))}
              </ul>
            </Panel>
            <Panel title={`Backlinks (${data.backlinks.length})`}>
              {data.backlinks.length === 0 && <Empty>No chunk links here.</Empty>}
              <ul className="links">
                {data.backlinks.map((link) => (
                  <li key={link.chunkId}>
                    <Badge tone={link.kind === 'link' ? 'info' : 'muted'}>{link.kind}</Badge>{' '}
                    <button type="button" className="link" onClick={() => onOpenChunk(link.chunkId)}>
                      {link.heading}
                    </button>{' '}
                    <span className="muted small">“{link.label}”</span>
                  </li>
                ))}
              </ul>
            </Panel>
          </div>
        </>
      )}
    </div>
  );
}
