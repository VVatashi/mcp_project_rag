import { Link, Navigate } from 'react-router';

import { api } from '../api';
import { ErrorBanner } from '../components/Common';
import { useLoad } from '../hooks';

/** Follows a link of a chunk: finds the chunk at an anchor of a document and opens it in place of this address. */
export function AnchorPage({ documentId, name }: { documentId: number; name: string }) {
  const anchor = useLoad(() => api.anchor(documentId, name), [documentId, name]);

  if (anchor.data?.chunkId != null) return <Navigate to={`/chunks/${anchor.data.chunkId}`} replace />;

  return (
    <div className="page">
      <ErrorBanner message={anchor.error && `The anchor “${name}” does not resolve: ${anchor.error}`} />
      {anchor.data && <p className="muted">No chunk lies at the anchor “{name}”.</p>}
      {(anchor.error ?? anchor.data) && (
        <Link className="link" to={`/documents/${documentId}`}>
          Open the document
        </Link>
      )}
      {!anchor.error && !anchor.data && <p className="muted">Finding the anchor…</p>}
    </div>
  );
}
