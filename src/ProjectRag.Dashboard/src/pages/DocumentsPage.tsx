import { useState } from 'react';

import { api } from '../api';
import { Empty, ErrorBanner, Panel } from '../components/Common';
import { formatBytes, formatDate, formatNumber } from '../format';
import { useLoad } from '../hooks';

const pageSize = 50;

export function DocumentsPage({ sourceId, onSourceChange, onOpen }: { sourceId: number | null; onSourceChange: (sourceId: number | null) => void; onOpen: (documentId: number) => void }) {
  const sources = useLoad(api.sources, []);
  const [text, setText] = useState('');
  const [offset, setOffset] = useState(0);
  const documents = useLoad(() => api.documents(sourceId, text, offset, pageSize), [sourceId, text, offset]);
  const total = documents.data?.total ?? 0;

  return (
    <div className="page">
      <ErrorBanner message={documents.error} />
      <Panel
        title="Documents"
        actions={
          <>
            <select
              value={sourceId ?? ''}
              onChange={(event) => {
                setOffset(0);
                onSourceChange(event.target.value ? Number(event.target.value) : null);
              }}
            >
              <option value="">All sources</option>
              {sources.data?.map((source) => (
                <option key={source.sourceId} value={source.sourceId}>
                  {source.name}
                </option>
              ))}
            </select>
            <input
              type="search"
              value={text}
              placeholder="Path or title"
              onChange={(event) => {
                setOffset(0);
                setText(event.target.value);
              }}
            />
          </>
        }
      >
        {documents.data && documents.data.items.length === 0 && <Empty>No documents.</Empty>}
        {documents.data && documents.data.items.length > 0 && (
          <>
            <div className="table-wrap">
              <table className="table">
                <thead>
                  <tr>
                    <th>Title</th>
                    <th>Source</th>
                    <th>Path</th>
                    <th className="num">Chunks</th>
                    <th className="num">Size</th>
                    <th>Changed</th>
                  </tr>
                </thead>
                <tbody>
                  {documents.data.items.map((document) => (
                    <tr key={document.documentId}>
                      <td>
                        <button type="button" className="link" onClick={() => onOpen(document.documentId)}>
                          {document.title}
                        </button>
                      </td>
                      <td>{document.source}</td>
                      <td className="mono small">{document.path}</td>
                      <td className="num">{formatNumber(document.chunkCount)}</td>
                      <td className="num">{formatBytes(document.sizeBytes)}</td>
                      <td className="small">{formatDate(document.modifiedAt)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            <div className="pager">
              <button type="button" className="button button--small" disabled={offset === 0} onClick={() => setOffset(Math.max(0, offset - pageSize))}>
                Previous
              </button>
              <span className="muted small">
                {formatNumber(offset + 1)}–{formatNumber(Math.min(total, offset + pageSize))} of {formatNumber(total)}
              </span>
              <button type="button" className="button button--small" disabled={offset + pageSize >= total} onClick={() => setOffset(offset + pageSize)}>
                Next
              </button>
            </div>
          </>
        )}
      </Panel>
    </div>
  );
}
