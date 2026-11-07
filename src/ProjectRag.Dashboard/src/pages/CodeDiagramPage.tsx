import { useState } from 'react';
import { Link, useSearchParams } from 'react-router';

import { api } from '../api';
import { symbolHref, useKinds, kindLabel } from '../components/Code';
import { Empty, ErrorBanner, Panel } from '../components/Common';
import { Diagram } from '../components/Diagram';
import { formatNumber } from '../format';
import { useLoad } from '../hooks';
import type { Diagram as DiagramModel, DiagramFormat, DiagramNode, DiagramParams, DiagramView, Source } from '../types';

const views: { name: DiagramView; label: string }[] = [
  { name: 'projects', label: 'Projects' },
  { name: 'namespaces', label: 'Namespaces' },
  { name: 'namespace', label: 'Namespace' },
  { name: 'type', label: 'Type' },
];

const memberModes = ['none', 'public', 'all'] as const;
const directions = ['both', 'uses', 'usedBy'] as const;
const directionLabels: Record<(typeof directions)[number], string> = { both: 'Both', uses: 'Uses', usedBy: 'Used by' };

const defaultMaxNodes: Record<DiagramView, number> = { projects: 0, namespaces: 60, namespace: 40, type: 40 };
const defaultMaxEdges = 60;

type MemberMode = (typeof memberModes)[number];
type Direction = (typeof directions)[number];

interface State {
  sourceId: number | null;
  view: DiagramView;
  project: string;
  namespace: string;
  id: string;
  tests: boolean;
  minWeight: number;
  maxNodes: number;
  maxEdges: number;
  neighborEdges: boolean;
  depth: number;
  direction: Direction;
  kinds: string[];
  external: boolean;
  members: MemberMode;
  neighborMembers: boolean;
}

const optionKeys: Record<DiagramView, string[]> = {
  projects: ['tests'],
  namespaces: ['tests', 'minWeight', 'maxNodes', 'maxEdges'],
  namespace: ['external', 'members', 'maxNodes', 'maxEdges'],
  type: ['depth', 'direction', 'kinds', 'tests', 'external', 'members', 'neighborMembers', 'neighborEdges', 'maxNodes'],
};

function integer(value: string | null, fallback: number, min: number, max: number): number {
  const number = Number(value);
  return value !== null && Number.isInteger(number) && number >= min && number <= max ? number : fallback;
}

function readState(parameters: URLSearchParams, firstSource: number | null): State {
  const viewName = parameters.get('view');
  const view = views.find((item) => item.name === viewName)?.name ?? 'projects';
  const source = Number(parameters.get('source'));
  const members = parameters.get('members');
  const direction = parameters.get('direction');
  const kinds = (parameters.get('kinds') ?? '').split(',').filter((kind) => useKinds.includes(kind));
  return {
    sourceId: Number.isInteger(source) && source > 0 ? source : firstSource,
    view,
    project: parameters.get('project') ?? '',
    namespace: parameters.get('namespace') ?? '',
    id: parameters.get('id') ?? '',
    tests: parameters.get('tests') !== '0',
    minWeight: integer(parameters.get('minWeight'), 1, 1, 100000),
    maxNodes: integer(parameters.get('maxNodes'), defaultMaxNodes[view], 1, 500),
    maxEdges: integer(parameters.get('maxEdges'), defaultMaxEdges, 5, 500),
    neighborEdges: parameters.get('neighborEdges') === '1',
    depth: integer(parameters.get('depth'), 1, 1, 3),
    direction: directions.find((item) => item === direction) ?? 'both',
    kinds,
    external: parameters.get('external') === '1',
    members: memberModes.find((item) => item === members) ?? (view === 'type' ? 'public' : 'none'),
    neighborMembers: parameters.get('neighborMembers') === '1',
  };
}

function requestParams(state: State): DiagramParams {
  const sourceId = state.sourceId;
  switch (state.view) {
    case 'projects':
      return { sourceId, includeTests: state.tests };
    case 'namespaces':
      return { sourceId, project: state.project, includeTests: state.tests, minWeight: state.minWeight, maxNodes: state.maxNodes, maxEdges: state.maxEdges };
    case 'namespace':
      return {
        sourceId,
        namespace: state.namespace,
        project: state.project,
        includeExternal: state.external,
        members: state.members,
        maxNodes: state.maxNodes,
        maxEdges: state.maxEdges,
      };
    case 'type':
      return {
        sourceId,
        docId: state.id,
        project: state.project,
        depth: state.depth,
        direction: state.direction,
        kinds: state.kinds.join(','),
        includeTests: state.tests,
        includeExternal: state.external,
        members: state.members,
        neighborMembers: state.neighborMembers,
        neighborEdges: state.neighborEdges,
        maxNodes: state.maxNodes,
      };
  }
}

function missingTarget(state: State): string | undefined {
  if (state.view === 'namespace' && !state.namespace) return 'Enter a namespace, or open one from the diagram of namespaces.';
  if (state.view === 'type' && !state.id) return 'Open a type from a namespace diagram or from the page of a symbol.';
  return undefined;
}

function slug(text: string): string {
  return text
    .replace(/[^A-Za-z0-9._-]+/g, '-')
    .replace(/^-+|-+$/g, '')
    .slice(0, 80);
}

function fileName(state: State, diagram: DiagramModel, format: DiagramFormat): string {
  const focus = diagram.nodes.find((node) => node.isFocus);
  const target = state.view === 'type' ? (focus?.label ?? state.id) : state.view === 'namespace' ? state.namespace : state.view === 'namespaces' ? state.project : '';
  const name = [state.view, slug(target)].filter(Boolean).join('-');
  return `${name}.${format === 'dot' ? 'dot' : 'puml'}`;
}

function saveText(name: string, text: string) {
  const url = URL.createObjectURL(new Blob([text], { type: 'text/plain;charset=utf-8' }));
  const link = document.createElement('a');
  link.href = url;
  link.download = name;
  document.body.append(link);
  link.click();
  link.remove();
  URL.revokeObjectURL(url);
}

function NumberOption({ label, value, min, max, onChange }: { label: string; value: number; min: number; max: number; onChange: (value: number) => void }) {
  const [draft, setDraft] = useState(String(value));
  const [syncedValue, setSyncedValue] = useState(value);
  if (syncedValue !== value) {
    setSyncedValue(value);
    setDraft(String(value));
  }

  const commit = () => {
    const parsed = Math.round(Number(draft));
    if (draft.trim() === '' || !Number.isFinite(parsed)) {
      setDraft(String(value));
      return;
    }
    const next = Math.min(max, Math.max(min, parsed));
    setDraft(String(next));
    if (next !== value) onChange(next);
  };

  return (
    <label className="inline">
      {label}
      <input
        type="number"
        className="diagram-number"
        value={draft}
        min={min}
        max={max}
        onChange={(event) => setDraft(event.target.value)}
        onBlur={commit}
        onKeyDown={(event) => {
          if (event.key === 'Enter') commit();
        }}
      />
    </label>
  );
}

function Check({ label, checked, onChange }: { label: string; checked: boolean; onChange: (checked: boolean) => void }) {
  return (
    <label className="inline">
      <input type="checkbox" checked={checked} onChange={(event) => onChange(event.target.checked)} />
      {label}
    </label>
  );
}

function MembersOption({ label, value, onChange }: { label: string; value: MemberMode; onChange: (value: MemberMode) => void }) {
  return (
    <label className="inline">
      {label}
      <select value={value} onChange={(event) => onChange(event.target.value as MemberMode)}>
        {memberModes.map((mode) => (
          <option key={mode} value={mode}>
            {mode}
          </option>
        ))}
      </select>
    </label>
  );
}

function Toolbar({
  state,
  change,
  projects,
  codeSources,
}: {
  state: State;
  change: (values: Record<string, string | null>, replace?: boolean) => void;
  projects: string[];
  codeSources: Source[];
}) {
  const selectedKinds = state.kinds.length === 0 ? useKinds : state.kinds;
  const flag = (value: boolean, on: string) => (value ? on : null);

  const toggleKind = (kind: string) => {
    const next = selectedKinds.includes(kind) ? selectedKinds.filter((item) => item !== kind) : useKinds.filter((item) => item === kind || selectedKinds.includes(item));
    if (next.length === 0) return;
    change({ kinds: next.length === useKinds.length ? null : next.join(',') });
  };

  return (
    <div className="panel diagram-toolbar">
      <div className="search__options">
        <div className="diagram-views" role="tablist">
          {views.map((item) => (
            <button
              key={item.name}
              type="button"
              role="tab"
              aria-selected={state.view === item.name}
              className={state.view === item.name ? 'button button--small button--primary' : 'button button--small'}
              onClick={() => change({ view: item.name })}
            >
              {item.label}
            </button>
          ))}
        </div>
        <label className="inline">
          Codebase
          <select value={state.sourceId ?? ''} onChange={(event) => change({ source: event.target.value, project: null, namespace: null, id: null })} disabled={codeSources.length === 0}>
            {codeSources.map((source) => (
              <option key={source.sourceId} value={source.sourceId}>
                {source.name}
              </option>
            ))}
          </select>
        </label>
      </div>

      <div className="search__options">
        {(state.view === 'namespaces' || state.view === 'namespace' || state.view === 'type') && (
          <label className="inline">
            Project
            <select value={state.project} onChange={(event) => change({ project: event.target.value || null })}>
              <option value="">{state.view === 'namespaces' ? 'All' : 'Any'}</option>
              {state.project && !projects.includes(state.project) && <option value={state.project}>{state.project}</option>}
              {projects.map((name) => (
                <option key={name} value={name}>
                  {name}
                </option>
              ))}
            </select>
          </label>
        )}
        {state.view === 'namespace' && (
          <label className="inline">
            Namespace
            <input
              key={state.namespace}
              className="mono"
              defaultValue={state.namespace}
              placeholder="MyApp.Core"
              size={28}
              onKeyDown={(event) => {
                if (event.key === 'Enter') change({ namespace: event.currentTarget.value.trim() || null });
              }}
              onBlur={(event) => {
                if (event.target.value.trim() !== state.namespace) change({ namespace: event.target.value.trim() || null });
              }}
            />
          </label>
        )}
        {state.view === 'type' && state.id && <span className="mono small muted diagram-id">{state.id}</span>}
        {state.view === 'namespaces' && <NumberOption label="Min weight" value={state.minWeight} min={1} max={100000} onChange={(value) => change({ minWeight: value === 1 ? null : String(value) }, true)} />}
        {state.view === 'type' && <NumberOption label="Depth" value={state.depth} min={1} max={3} onChange={(value) => change({ depth: value === 1 ? null : String(value) }, true)} />}
        {state.view !== 'projects' && (
          <NumberOption
            label="Max nodes"
            value={state.maxNodes}
            min={1}
            max={500}
            onChange={(value) => change({ maxNodes: value === defaultMaxNodes[state.view] ? null : String(value) }, true)}
          />
        )}
        {state.view === 'type' && (
          <label className="inline">
            Direction
            <select value={state.direction} onChange={(event) => change({ direction: event.target.value === 'both' ? null : event.target.value })}>
              {directions.map((item) => (
                <option key={item} value={item}>
                  {directionLabels[item]}
                </option>
              ))}
            </select>
          </label>
        )}
        {(state.view === 'namespace' || state.view === 'type') && (
          <MembersOption
            label="Members"
            value={state.members}
            onChange={(value) => change({ members: value === (state.view === 'type' ? 'public' : 'none') ? null : value })}
          />
        )}
        {(state.view === 'namespaces' || state.view === 'namespace') && (
          <NumberOption
            label="Max edges"
            value={state.maxEdges}
            min={5}
            max={500}
            onChange={(value) => change({ maxEdges: value === defaultMaxEdges ? null : String(value) }, true)}
          />
        )}
        {state.view === 'type' && <Check label="Neighbor edges" checked={state.neighborEdges} onChange={(checked) => change({ neighborEdges: flag(checked, '1') })} />}
        {state.view === 'type' && <Check label="Neighbor members" checked={state.neighborMembers} onChange={(checked) => change({ neighborMembers: flag(checked, '1') })} />}
        {(state.view === 'namespace' || state.view === 'type') && <Check label="External types" checked={state.external} onChange={(checked) => change({ external: flag(checked, '1') })} />}
        {state.view !== 'namespace' && <Check label="Include tests" checked={state.tests} onChange={(checked) => change({ tests: checked ? null : '0' })} />}
      </div>

      {state.view === 'type' && (
        <div className="search__options">
          <span className="muted small">Follow</span>
          {useKinds.map((kind) => (
            <label key={kind} className={`chip ${selectedKinds.includes(kind) ? 'chip--on' : ''}`}>
              <input type="checkbox" checked={selectedKinds.includes(kind)} onChange={() => toggleKind(kind)} />
              {kindLabel(kind)}
            </label>
          ))}
        </div>
      )}
    </div>
  );
}

function NodeList({ nodes, state }: { nodes: DiagramNode[]; state: State }) {
  const [text, setText] = useState('');
  const needle = text.trim().toLowerCase();
  const shown = needle ? nodes.filter((node) => `${node.label} ${node.fullName ?? ''}`.toLowerCase().includes(needle)) : nodes;
  const focus = nodes.find((node) => node.isFocus);

  return (
    <aside className="diagram-nodes">
      {state.view === 'type' && focus?.docId && state.sourceId !== null && (
        <Link className="button button--small" to={symbolHref({ sourceId: state.sourceId, docId: focus.docId, project: focus.project ?? '' })}>
          Open symbol page
        </Link>
      )}
      <input value={text} onChange={(event) => setText(event.target.value)} placeholder="Find a node" aria-label="Find a node" />
      <div className="muted small">
        {formatNumber(shown.length)} of {formatNumber(nodes.length)} nodes
      </div>
      <ul className="diagram-nodes__list">
        {shown.map((node) => (
          <li key={node.id} className={node.isFocus ? 'is-focus' : undefined}>
            {node.href ? (
              <Link className="link mono" to={node.href} title={node.fullName ?? undefined}>
                {node.label}
              </Link>
            ) : (
              <span className="mono muted" title={node.fullName ?? undefined}>
                {node.label}
              </span>
            )}
            <span className="muted small">
              {kindLabel(node.kind)}
              {node.count !== null ? ` · ${formatNumber(node.count)}` : ''}
              {node.isTest ? ' · test' : ''}
            </span>
          </li>
        ))}
      </ul>
    </aside>
  );
}

function Exports({ state, diagram }: { state: State; diagram: DiagramModel }) {
  const [message, setMessage] = useState('');

  const run = async (format: DiagramFormat, action: 'save' | 'copy') => {
    try {
      const text = await api.codeDiagramText(state.view, requestParams(state), format);
      if (action === 'save') {
        saveText(fileName(state, diagram, format), text);
        setMessage('Saved.');
      } else {
        await navigator.clipboard.writeText(text);
        setMessage('Copied.');
      }
    } catch (exception) {
      setMessage(exception instanceof Error ? exception.message : String(exception));
    }
  };

  return (
    <>
      {message && <span className="muted small">{message}</span>}
      <button type="button" className="button button--small" onClick={() => void run('dot', 'save')}>
        Download .dot
      </button>
      <button type="button" className="button button--small" onClick={() => void run('plantuml', 'save')}>
        Download .puml
      </button>
      <button type="button" className="button button--small" onClick={() => void run('dot', 'copy')}>
        Copy DOT
      </button>
      <button type="button" className="button button--small" onClick={() => void run('plantuml', 'copy')}>
        Copy PlantUML
      </button>
    </>
  );
}

interface Outcome {
  key: string;
  diagram: DiagramModel;
}

export function CodeDiagramPage() {
  const [parameters, setParameters] = useSearchParams();
  const sources = useLoad(api.sources, []);
  const codeSources = sources.data?.filter((source) => source.kind === 'code') ?? [];
  const firstSource = codeSources[0]?.sourceId ?? null;
  const state = readState(parameters, firstSource);
  const missing = missingTarget(state);
  const sourceId = state.sourceId;
  const params = requestParams(state);
  const key = JSON.stringify([state.view, params]);
  const ready = sourceId !== null && !missing;

  const graph = useLoad(() => (sourceId === null || state.view === 'projects' ? Promise.resolve(undefined) : api.codeGraph(sourceId)), [sourceId, state.view]);
  const projects = graph.data && graph.data.sourceId === sourceId ? graph.data.projects.map((project) => project.name) : [];

  const outcome = useLoad<Outcome | undefined>(async () => (ready ? { key, diagram: await api.codeDiagram(state.view, params) } : undefined), [key, ready]);
  const current = ready && outcome.data?.key === key ? outcome.data.diagram : undefined;
  const busy = ready && current === undefined && !outcome.error;

  const change = (values: Record<string, string | null>, replace = false) => {
    const next = new URLSearchParams(parameters);
    if (state.sourceId !== null && !next.has('source')) next.set('source', String(state.sourceId));
    for (const [name, value] of Object.entries(values)) {
      if (value === null || value === '') next.delete(name);
      else next.set(name, value);
    }
    if (values.view) {
      next.set('view', values.view);
      if (values.view === 'projects' || values.view === 'namespaces') {
        next.delete('namespace');
        next.delete('id');
      }
    }
    setParameters(next, { replace });
  };

  const mapHref = (href: string): string => {
    const target = new URL(href, window.location.origin);
    const targetView = views.find((item) => item.name === target.searchParams.get('view'))?.name;
    if (!targetView) return href;
    for (const name of optionKeys[targetView]) {
      const value = parameters.get(name);
      if (value !== null && !target.searchParams.has(name) && (name !== 'maxNodes' || targetView === state.view)) target.searchParams.set(name, value);
    }
    return `${target.pathname}${target.search}`;
  };

  return (
    <div className="page">
      <Link className="link" to={state.sourceId === null ? '/code?tab=projects' : `/code?tab=projects&source=${state.sourceId}`}>
        ← Code
      </Link>
      <Toolbar state={state} change={change} projects={projects} codeSources={codeSources} />
      <ErrorBanner message={sources.error ?? outcome.error} />
      {sources.data && firstSource === null && <Empty>No code sources yet.</Empty>}
      {missing && <Empty>{missing}</Empty>}
      {busy && <p className="muted">Building the diagram…</p>}
      {current && (
        <Panel title={current.title} actions={<Exports state={state} diagram={current} />}>
          {current.truncated && <div className="banner banner--warn">The diagram is cut. Raise the limit of nodes or narrow the view to see the rest.</div>}
          {current.notes.map((note) => (
            <p key={note} className="muted small diagram-note">
              {note}
            </p>
          ))}
          {current.nodes.length === 0 ? (
            <Empty>The diagram has no nodes.</Empty>
          ) : (
            <div className="diagram-layout">
              <Diagram diagram={current} height={640} mapHref={mapHref} />
              <NodeList nodes={current.nodes} state={state} />
            </div>
          )}
        </Panel>
      )}
    </div>
  );
}
