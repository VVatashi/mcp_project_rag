import { api } from '../api';
import { Badge, ErrorBanner, Panel, Progress } from '../components/Common';
import { RunsTable } from '../components/RunsTable';
import { formatDuration, formatNumber, percent } from '../format';
import { useLoad } from '../hooks';

export function StatusPage() {
  const status = useLoad(api.status, [], 3000);
  const runs = useLoad(() => api.runs(null, 10), [], 5000);
  const data = status.data;
  const counts = data?.counts;
  const current = data?.indexer.current;

  return (
    <div className="page">
      <ErrorBanner message={status.error ?? runs.error} />
      {data && (
        <div className="cards">
          <div className="card">
            <h3>Database</h3>
            {data.database ? <Badge tone="error">down</Badge> : <Badge tone="ok">ready</Badge>}
            {data.database && <p className="card__note">{data.database}</p>}
          </div>
          <div className="card">
            <h3>Embedding model</h3>
            {data.embedding.problem ? <Badge tone="error">not ready</Badge> : <Badge tone="ok">ready</Badge>}
            <p className="card__value">{data.embedding.name}</p>
            <p className="card__note">
              {data.embedding.dims ? `${data.embedding.dims} dimensions · model ${data.embedding.modelId} · ` : ''}
              {data.embedding.url}
            </p>
            {data.embedding.problem && <p className="card__note card__note--error">{data.embedding.problem}</p>}
          </div>
          <div className="card">
            <h3>Reranker</h3>
            {!data.reranker.enabled ? (
              <Badge tone="muted">off</Badge>
            ) : data.reranker.healthy ? (
              <Badge tone="ok">ready</Badge>
            ) : (
              <Badge tone="warn">no answer</Badge>
            )}
            <p className="card__note">{data.reranker.url}</p>
          </div>
          {counts && (
            <div className="card">
              <h3>Content</h3>
              <p className="card__value">
                {formatNumber(counts.documents)} documents · {formatNumber(counts.chunks)} chunks
              </p>
              <Progress value={percent(counts.embeddedChunks, counts.chunks)} label="Chunks with vectors" />
              <p className="card__note">
                {formatNumber(counts.embeddedChunks)} of {formatNumber(counts.chunks)} chunks have vectors ({percent(counts.embeddedChunks, counts.chunks)}%) ·{' '}
                {formatNumber(counts.sources)} sources
              </p>
            </div>
          )}
        </div>
      )}

      <Panel title="Indexer">
        {current ? (
          <div className="current-run">
            <p>
              <Badge tone="info">running</Badge> Run #{current.runId} over <strong>{current.source}</strong> for {formatDuration(current.startedAt, null)}.
            </p>
            <p className="muted">
              {formatNumber(current.filesSeen)} files · {formatNumber(current.documentsAdded)} added · {formatNumber(current.documentsUpdated)} updated ·{' '}
              {formatNumber(current.documentsUnchanged)} unchanged · {formatNumber(current.chunksReused)} vectors reused · {formatNumber(current.chunksEmbedded)} chunks embedded
            </p>
          </div>
        ) : (
          <p className="muted">The indexer waits.{data && data.indexer.queued.length > 0 ? ` ${data.indexer.queued.length} sources in the queue.` : ''}</p>
        )}
      </Panel>

      <Panel title="Last runs">{runs.data && <RunsTable runs={runs.data} />}</Panel>
    </div>
  );
}
