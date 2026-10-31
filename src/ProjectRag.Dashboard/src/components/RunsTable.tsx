import { formatDate, formatDuration, formatNumber } from '../format';
import type { IndexRun } from '../types';
import { Empty, RunBadge } from './Common';

export function RunsTable({ runs }: { runs: IndexRun[] }) {
  if (runs.length === 0) return <Empty>The indexer has not run yet.</Empty>;

  return (
    <div className="table-wrap">
      <table className="table">
        <thead>
          <tr>
            <th>Run</th>
            <th>Source</th>
            <th>Status</th>
            <th>Started</th>
            <th>Duration</th>
            <th className="num">Files</th>
            <th className="num">Added</th>
            <th className="num">Updated</th>
            <th className="num">Removed</th>
            <th className="num">Failed</th>
            <th className="num">Vectors reused</th>
            <th className="num">Embedded</th>
          </tr>
        </thead>
        <tbody>
          {runs.map((run) => (
            <tr key={run.runId}>
              <td>#{run.runId}</td>
              <td>{run.source}</td>
              <td>
                <RunBadge status={run.status} warning={run.warning ?? run.error} />
              </td>
              <td>{formatDate(run.startedAt)}</td>
              <td>{formatDuration(run.startedAt, run.finishedAt)}</td>
              <td className="num">{formatNumber(run.filesSeen)}</td>
              <td className="num">{formatNumber(run.documentsAdded)}</td>
              <td className="num">{formatNumber(run.documentsUpdated)}</td>
              <td className="num">{formatNumber(run.documentsRemoved)}</td>
              <td className="num">{formatNumber(run.documentsFailed)}</td>
              <td className="num">{formatNumber(run.chunksReused)}</td>
              <td className="num">{formatNumber(run.chunksEmbedded)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
