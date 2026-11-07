import { Link, useSearchParams } from 'react-router';

import { api } from '../api';
import { diagramHref } from '../components/Code';
import { Badge, Empty, ErrorBanner, Panel } from '../components/Common';
import { formatDate, formatNumber } from '../format';
import { useLoad } from '../hooks';
import type { CodeGraphStatus } from '../types';

function Status({ graph }: { graph: CodeGraphStatus }) {
  if (graph.builtAt === null) {
    return (
      <>
        {graph.error && <div className="banner banner--warn">The build failed: {graph.error}</div>}
        <Empty>No graph yet.</Empty>
      </>
    );
  }

  return (
    <>
      {graph.error && <div className="banner banner--warn">The last build failed, and the older graph stays: {graph.error}</div>}
      <dl className="facts">
        <dt>Built</dt>
        <dd>{formatDate(graph.builtAt)}</dd>
        <dt>Duration</dt>
        <dd>{graph.durationMs === null ? '—' : `${formatNumber(Math.round(graph.durationMs / 100) / 10)} s`}</dd>
        <dt>SDK</dt>
        <dd>{graph.sdkVersion ?? '—'}</dd>
        <dt>Projects</dt>
        <dd>{formatNumber(graph.projectCount)}</dd>
        <dt>Symbols</dt>
        <dd>{formatNumber(graph.symbolCount)}</dd>
        <dt>References</dt>
        <dd>{formatNumber(graph.referenceCount)}</dd>
      </dl>
    </>
  );
}

function Graph({ graph }: { graph: CodeGraphStatus }) {
  const names = new Map(graph.projects.map((project) => [project.projectId, project.name]));
  const nameOf = (projectId: number) => names.get(projectId) ?? String(projectId);

  return (
    <>
      <Panel title={`Graph of ${graph.source}`}>
        <Status graph={graph} />
      </Panel>

      {graph.projects.length > 0 && (
        <Panel title="Projects">
          <div className="table-wrap">
            <table className="table">
              <thead>
                <tr>
                  <th>Name</th>
                  <th>Path</th>
                  <th>Framework</th>
                  <th>Test</th>
                  <th className="num">Files</th>
                  <th className="num">Symbols</th>
                  <th className="num">Errors</th>
                  <th>References</th>
                </tr>
              </thead>
              <tbody>
                {graph.projects.map((project) => (
                  <tr key={project.projectId}>
                    <td>
                      <strong>{project.name}</strong>
                    </td>
                    <td className="mono small">{project.path}</td>
                    <td className="small">{project.targetFramework ?? '—'}</td>
                    <td>{project.isTest ? <Badge tone="muted">test</Badge> : ''}</td>
                    <td className="num">{formatNumber(project.documentCount)}</td>
                    <td className="num">{formatNumber(project.symbolCount)}</td>
                    <td className="num">{project.errorCount > 0 ? <Badge tone="error">{formatNumber(project.errorCount)}</Badge> : 0}</td>
                    <td className="small">{project.references.map(nameOf).join(', ') || '—'}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </Panel>
      )}

      {graph.solutions.length > 0 && (
        <Panel title="Solutions">
          <ul className="solutions">
            {graph.solutions.map((solution) => (
              <li key={solution.solutionId}>
                <div className="mono">{solution.path}</div>
                <div className="muted small">{solution.projectIds.map(nameOf).join(', ') || 'No projects'}</div>
              </li>
            ))}
          </ul>
        </Panel>
      )}

      {graph.diagnostics.length > 0 && (
        <Panel title={`Diagnostics (${formatNumber(graph.diagnostics.length)})`}>
          <div className="table-wrap">
            <table className="table">
              <thead>
                <tr>
                  <th>Severity</th>
                  <th>Code</th>
                  <th>Message</th>
                  <th>Project</th>
                  <th>Location</th>
                </tr>
              </thead>
              <tbody>
                {graph.diagnostics.map((diagnostic, index) => (
                  <tr key={index}>
                    <td>
                      <Badge tone={diagnostic.severity === 'error' ? 'error' : 'warn'}>{diagnostic.severity}</Badge>
                    </td>
                    <td className="mono small">{diagnostic.code}</td>
                    <td className="small">{diagnostic.message}</td>
                    <td className="small">{diagnostic.project ?? '—'}</td>
                    <td className="mono small">{diagnostic.path ? `${diagnostic.path}${diagnostic.line ? `:${diagnostic.line}` : ''}` : '—'}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </Panel>
      )}
    </>
  );
}

export function CodeProjectsTab() {
  const [parameters, setParameters] = useSearchParams();
  const sources = useLoad(api.sources, []);
  const codeSources = sources.data?.filter((source) => source.kind === 'code') ?? [];
  const requested = Number(parameters.get('source'));
  const sourceId = codeSources.find((source) => source.sourceId === requested)?.sourceId ?? codeSources[0]?.sourceId ?? null;
  const graph = useLoad(() => (sourceId === null ? Promise.resolve(undefined) : api.codeGraph(sourceId)), [sourceId]);
  const current = graph.data && graph.data.sourceId === sourceId ? graph.data : undefined;

  const select = (value: string) => {
    const next = new URLSearchParams(parameters);
    next.set('tab', 'projects');
    next.set('source', value);
    setParameters(next);
  };

  return (
    <>
      <div className="search__options">
        <label className="inline">
          Codebase
          <select value={sourceId ?? ''} onChange={(event) => select(event.target.value)} disabled={codeSources.length === 0}>
            {codeSources.map((source) => (
              <option key={source.sourceId} value={source.sourceId}>
                {source.name}
              </option>
            ))}
          </select>
        </label>
        {sourceId !== null && (
          <Link className="button button--small" to={diagramHref(sourceId, 'projects')}>
            Diagram
          </Link>
        )}
      </div>
      <ErrorBanner message={sources.error ?? graph.error} />
      {sources.data && codeSources.length === 0 && <Empty>No code sources yet.</Empty>}
      {current && <Graph graph={current} />}
    </>
  );
}
