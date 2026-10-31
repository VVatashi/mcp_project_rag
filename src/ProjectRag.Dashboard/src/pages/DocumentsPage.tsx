import { useState } from 'react';
import { Link, useNavigate } from 'react-router';

import { api } from '../api';
import { Empty, ErrorBanner, Pager, Panel } from '../components/Common';
import { formatBytes, formatDate, formatNumber } from '../format';
import { useLoad } from '../hooks';

const pageSize = 50;

export function DocumentsPage({ sourceId, category }: { sourceId: number | null; category: string | null }) {
  const navigate = useNavigate();
  const sources = useLoad(api.sources, []);
  const categories = useLoad(api.categories, []);
  const [text, setText] = useState('');
  const [offset, setOffset] = useState(0);
  const documents = useLoad(() => api.documents(sourceId, category, text, offset, pageSize), [sourceId, category, text, offset]);
  const total = documents.data?.total ?? 0;
  // The filters live in the address, so a link can name a category or a source.
  const filter = (next: { source?: number | null; category?: string | null }) => {
    const parameters = new URLSearchParams();
    const nextSource = next.source === undefined ? sourceId : next.source;
    const nextCategory = next.category === undefined ? category : next.category;
    if (nextSource !== null) parameters.set('source', String(nextSource));
    if (nextCategory) parameters.set('category', nextCategory);
    setOffset(0);
    void navigate(`/documents${parameters.size > 0 ? `?${parameters}` : ''}`);
  };

  return (
    <div className="page">
      <ErrorBanner message={documents.error} />
      <Panel
        title="Documents"
        actions={
          <>
            <select value={category ?? ''} onChange={(event) => filter({ category: event.target.value || null })}>
              <option value="">All categories</option>
              {categories.data?.map((item) => (
                <option key={item.name} value={item.name}>
                  {item.name} ({formatNumber(item.documentCount)})
                </option>
              ))}
            </select>
            <select value={sourceId ?? ''} onChange={(event) => filter({ source: event.target.value ? Number(event.target.value) : null })}>
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
                    <th>Category</th>
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
                        <Link className="link" to={`/documents/${document.documentId}`}>
                          {document.title}
                        </Link>
                      </td>
                      <td>{document.category}</td>
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
            <Pager offset={offset} pageSize={pageSize} total={total} onChange={setOffset} />
          </>
        )}
      </Panel>
    </div>
  );
}
