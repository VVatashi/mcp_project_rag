import { useState } from 'react';
import { Link, useNavigate } from 'react-router';

import { api } from '../api';
import { Badge, Empty, ErrorBanner, Panel } from '../components/Common';
import { Markdown } from '../components/Markdown';
import { PdfPage } from '../components/PdfPage';
import { anchorAddress } from '../format';
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
export function ChunkPage({ chunkId }: { chunkId: number }) {
  const chunk = useLoad(() => api.chunk(chunkId), [chunkId]);
  const [page, setPage] = useState<number | null>(null);
  const navigate = useNavigate();
  const data = chunk.data;

  // The chunk knows the targets of its own links. A link in the repeated start of the chunk belongs to the previous chunk, so the page of the anchor resolves it.
  const anchorHref = (anchor: string) => {
    const target = data?.links.find((link) => link.targetPath === null && link.targetAnchor === anchor && link.targetChunkId !== null);
    if (target) return `/chunks/${target.targetChunkId}`;
    return data ? anchorAddress(data.documentId, anchor) : '#';
  };

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
                <button type="button" className="button button--small" disabled={data.previousChunkId === null} onClick={() => void navigate(`/chunks/${data.previousChunkId}`)}>
                  ← Previous
                </button>
                <button type="button" className="button button--small" disabled={data.nextChunkId === null} onClick={() => void navigate(`/chunks/${data.nextChunkId}`)}>
                  Next →
                </button>
              </>
            }
          >
            <p className="muted small">
              {data.source} ·{' '}
              <Link className="link" to={`/documents/${data.documentId}/text?offset=${data.charStart}`}>
                {data.path}
              </Link>{' '}
              · chunk #{data.ordinal} {pages(data) && `· ${pages(data)}`} {data.overlapChars > 0 && `· repeats ${data.overlapChars} characters of the previous chunk`}{' '}
              {data.hasEmbedding ? <Badge tone="ok">vector</Badge> : <Badge tone="warn">no vector</Badge>}
            </p>
          </Panel>

          <div className={isPdf && shownPage !== null ? 'split' : undefined}>
            <Panel title="Text">
              <Markdown text={data.body} anchorHref={anchorHref} />
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
                      <Link className="link" to={`/chunks/${link.targetChunkId}`}>
                        {link.targetHeading}
                        {link.targetPage !== null && `, page ${link.targetPage}`}
                      </Link>
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
                    <Link className="link" to={`/chunks/${link.chunkId}`}>
                      {link.heading}
                    </Link>{' '}
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
