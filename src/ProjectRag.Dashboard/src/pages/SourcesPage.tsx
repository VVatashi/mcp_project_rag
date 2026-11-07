import { useState, type FormEvent } from 'react';
import { Link } from 'react-router';

import { api } from '../api';
import { Badge, Empty, ErrorBanner, Panel, Progress } from '../components/Common';
import { RunsTable } from '../components/RunsTable';
import { formatDate, formatNumber, percent } from '../format';
import { useLoad } from '../hooks';
import type { CategoryRule, Source, SourceInput, SourceKind, TextReplacement } from '../types';

const codeIncludeHint = '**/*';
const codeExcludeHint = '**/artifacts/**\n**/package-lock.json\n**/packages.lock.json\n**/yarn.lock\n**/pnpm-lock.yaml\n**/*.min.js\n**/*.min.css\n**/*.map';
const codeDotHint = '.github/**/*.yml\n.github/**/*.yaml\n.editorconfig\n.config/*.json';

interface FormState {
  sourceId: number | null;
  kind: SourceKind;
  name: string;
  description: string;
  rootPath: string;
  urls: string;
  includeGlobs: string;
  excludeGlobs: string;
  dotGlobs: string;
  categoryRules: string;
  replacements: string;
  buildProperties: string;
  buildPropertiesOriginal: string;
}

const emptyForm: FormState = {
  sourceId: null,
  kind: 'docs',
  name: '',
  description: '',
  rootPath: '',
  urls: '',
  includeGlobs: '',
  excludeGlobs: '',
  dotGlobs: '',
  categoryRules: '',
  replacements: '',
  buildProperties: '',
  buildPropertiesOriginal: '',
};

// A rule of a category is a line "pattern = category", for example "ECMA-33*.pdf = csharp".
function readRules(text: string): CategoryRule[] {
  return lines(text).map((line) => {
    const at = line.lastIndexOf('=');
    return at < 0 ? { pattern: line, category: '' } : { pattern: line.slice(0, at).trim(), category: line.slice(at + 1).trim() };
  });
}

// The replacements are JSON: a regular expression may hold any character, so no separator of a line would do.
function readReplacements(text: string): TextReplacement[] {
  if (!text.trim()) return [];
  let value: unknown;
  try {
    value = JSON.parse(text);
  } catch (exception) {
    throw new Error(`The replacements are not valid JSON: ${exception instanceof Error ? exception.message : String(exception)}`, { cause: exception });
  }

  const isReplacement = (item: unknown): item is TextReplacement =>
    typeof item === 'object' && item !== null && ['pattern', 'find', 'replace'].every((key) => typeof (item as Record<string, unknown>)[key] === 'string');
  if (!Array.isArray(value) || !value.every(isReplacement)) throw new Error('The replacements must be an array of objects with the texts "pattern", "find" and "replace".');
  return value;
}

function readBuildProperties(text: string): Record<string, string> {
  const result: Record<string, string> = {};
  for (const line of lines(text)) {
    const at = line.indexOf('=');
    const name = at < 0 ? line : line.slice(0, at).trim();
    if (at < 0 || !/^[A-Za-z0-9_.-]+$/.test(name)) throw new Error(`Wrong line "${line}". Write Name=value, for example Configuration=Release.`);
    result[name] = line.slice(at + 1).trim();
  }

  return result;
}

function writeBuildProperties(properties: Record<string, string>): string {
  return Object.entries(properties)
    .map(([name, value]) => `${name}=${value}`)
    .join('\n');
}

function writeRules(rules: CategoryRule[]): string {
  return rules.map((rule) => `${rule.pattern} = ${rule.category}`).join('\n');
}

function lines(text: string): string[] {
  return text
    .split('\n')
    .map((line) => line.trim())
    .filter((line) => line.length > 0);
}

export function SourcesPage() {
  const sources = useLoad(api.sources, [], 4000);
  const runs = useLoad(() => api.runs(null, 15), [], 4000);
  const [form, setForm] = useState<FormState | null>(null);
  const [error, setError] = useState<string>();
  const [propertiesError, setPropertiesError] = useState<string>();
  const [busy, setBusy] = useState(false);

  const act = async (action: () => Promise<unknown>) => {
    setError(undefined);
    setBusy(true);
    try {
      await action();
      sources.reload();
      runs.reload();
      return true;
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : String(exception));
      return false;
    } finally {
      setBusy(false);
    }
  };

  const edit = (source: Source) => {
    setPropertiesError(undefined);
    setForm({
      sourceId: source.sourceId,
      kind: source.kind,
      name: source.name,
      description: source.description,
      rootPath: source.rootPath,
      urls: source.urls.join('\n'),
      includeGlobs: source.includeGlobs.join('\n'),
      excludeGlobs: source.excludeGlobs.join('\n'),
      dotGlobs: source.dotGlobs.join('\n'),
      categoryRules: writeRules(source.categoryRules),
      replacements: source.replacements.length > 0 ? JSON.stringify(source.replacements, null, 2) : '',
      buildProperties: writeBuildProperties(source.buildProperties ?? {}),
      buildPropertiesOriginal: writeBuildProperties(source.buildProperties ?? {}),
    });
  };

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (!form) return;
    let replacements: TextReplacement[];
    try {
      replacements = readReplacements(form.replacements);
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : String(exception));
      return;
    }

    const isCode = form.kind === 'code';
    let buildProperties: Record<string, string> | null = null;
    if (isCode && form.buildProperties !== form.buildPropertiesOriginal) {
      try {
        buildProperties = readBuildProperties(form.buildProperties);
      } catch (exception) {
        setPropertiesError(exception instanceof Error ? exception.message : String(exception));
        return;
      }
    }
    setPropertiesError(undefined);
    const input: SourceInput = {
      kind: form.kind,
      name: form.name,
      description: form.description,
      rootPath: form.rootPath,
      urls: isCode ? [] : lines(form.urls),
      includeGlobs: lines(form.includeGlobs),
      // An empty field keeps the default patterns for a new source.
      excludeGlobs: form.sourceId === null && !form.excludeGlobs.trim() ? null : lines(form.excludeGlobs),
      dotGlobs: isCode ? (form.sourceId === null && !form.dotGlobs.trim() ? null : lines(form.dotGlobs)) : null,
      categoryRules: readRules(form.categoryRules),
      replacements,
      buildProperties,
    };
    const saved = await act(() => (form.sourceId === null ? api.addSource(input) : api.updateSource(form.sourceId, input)));
    if (saved) setForm(null);
  };

  const remove = (source: Source) => {
    if (window.confirm(`Delete the source ${source.name} with its ${source.documentCount} documents from the knowledge base? The files stay on the disk.`)) {
      void act(() => api.deleteSource(source.sourceId));
    }
  };

  return (
    <div className="page">
      <ErrorBanner message={error ?? sources.error} />
      <Panel
        title="Sources"
        actions={
          <button type="button" className="button button--primary" onClick={() => {
              setPropertiesError(undefined);
              setForm(emptyForm);
            }} disabled={form !== null}>
            Add a source
          </button>
        }
      >
        {form && (
          <form className="form" onSubmit={(event) => void submit(event)}>
            <div className="form__row">
              <label>
                Kind
                <select value={form.kind} disabled={form.sourceId !== null} onChange={(event) => setForm({ ...form, kind: event.target.value as SourceKind })}>
                  <option value="docs">Docs</option>
                  <option value="code">Code</option>
                </select>
                <small>{form.sourceId === null ? 'Docs are read as text and searched by meaning. Code is searched for exact text.' : 'The kind cannot change.'}</small>
              </label>
              <label>
                Name
                <input
                  value={form.name}
                  onChange={(event) => setForm({ ...form, name: event.target.value })}
                  placeholder="axion"
                  pattern="[a-z0-9][a-z0-9._\-]{0,63}"
                  required
                />
                <small>Lowercase letters, digits, ".", "_" and "-". Agents use this name.</small>
              </label>
              <label className="form__wide">
                Folder
                <input value={form.rootPath} onChange={(event) => setForm({ ...form, rootPath: event.target.value })} placeholder="C:\Docs\my-project" />
                <small>
                  {form.kind === 'code'
                    ? 'The absolute path of the root folder of the code.'
                    : 'The absolute path of a folder on this machine. A source of web pages only leaves it empty.'}
                </small>
              </label>
            </div>
            {form.kind === 'docs' && (
              <label>
                Web pages
                <textarea
                  rows={3}
                  value={form.urls}
                  onChange={(event) => setForm({ ...form, urls: event.target.value })}
                  placeholder="https://registry.khronos.org/KTX/specs/2.0/ktxspec.v2.html"
                />
                <small>One address per line. The indexer reads each page next to the files and requests it again after Indexing:PageRefreshHours, 24 hours by default.</small>
              </label>
            )}
            <label>
              Description
              <input
                value={form.description}
                onChange={(event) => setForm({ ...form, description: event.target.value })}
                placeholder="The documentation of the Axion engine"
              />
            </label>
            <div className="form__row">
              <label>
                Files to read
                <textarea
                  rows={4}
                  value={form.includeGlobs}
                  onChange={(event) => setForm({ ...form, includeGlobs: event.target.value })}
                  placeholder={form.kind === 'code' ? `Empty: ${codeIncludeHint}` : 'Empty: Markdown, text, PDF and HTML\n**/*.md'}
                />
              </label>
              <label>
                Files to skip
                <textarea
                  rows={4}
                  value={form.excludeGlobs}
                  onChange={(event) => setForm({ ...form, excludeGlobs: event.target.value })}
                  placeholder={form.kind === 'code' ? `Empty:\n${codeExcludeHint}` : 'Empty: .git, node_modules, bin, obj\n**/drafts/**'}
                />
              </label>
            </div>
            {form.kind === 'code' && (
              <label>
                Dot files
                <textarea
                  rows={4}
                  value={form.dotGlobs}
                  onChange={(event) => setForm({ ...form, dotGlobs: event.target.value })}
                  placeholder={`Empty:\n${codeDotHint}`}
                />
                <small>One pattern per line. Files and folders that start with a dot are skipped, except the ones that match these patterns.</small>
              </label>
            )}
            {form.kind === 'code' && (
              <label>
                Build properties
                <textarea
                  rows={3}
                  value={form.buildProperties}
                  onChange={(event) => {
                    setPropertiesError(undefined);
                    setForm({ ...form, buildProperties: event.target.value });
                  }}
                  placeholder={'Configuration=Release\nDefineConstants=WINDOWS'}
                  aria-invalid={propertiesError !== undefined}
                />
                {propertiesError ? (
                  <small className="field-error" role="alert">
                    {propertiesError}
                  </small>
                ) : (
                  <small>One Name=value per line. These global MSBuild properties set the view of the build for the code graph.</small>
                )}
              </label>
            )}
            {form.kind === 'docs' && (
              <>
                <label>
                  Categories
                  <textarea
                    rows={4}
                    value={form.categoryRules}
                    onChange={(event) => setForm({ ...form, categoryRules: event.target.value })}
                    placeholder={'ECMA-33*.pdf = csharp\nvkspec.pdf = vulkan'}
                  />
                  <small>A line "pattern = category" gives the matching documents a category. The first matching line wins. Other documents take the name of the source.</small>
                </label>
                <label>
                  Replacements
                  <textarea
                    rows={4}
                    className="mono"
                    value={form.replacements}
                    onChange={(event) => setForm({ ...form, replacements: event.target.value })}
                    placeholder={'[{ "pattern": "Metal-*.pdf", "find": "sparse_color-(Tv?)\\\\b", "replace": "sparse_color<$1>" }]'}
                  />
                  <small>
                    JSON: a pattern of the files, a regular expression of .NET and its replacement. They fix errors of a published file in its Markdown before the chunks. A change reads the matching files again.
                  </small>
                </label>
              </>
            )}
            <div className="form__actions">
              <button type="submit" className="button button--primary" disabled={busy}>
                {form.sourceId === null ? 'Add and index' : 'Save and index'}
              </button>
              <button type="button" className="button" onClick={() => setForm(null)}>
                Cancel
              </button>
            </div>
          </form>
        )}

        {sources.data && sources.data.length === 0 && !form && <Empty>No sources yet. Add a folder with the documentation of a project.</Empty>}
        {sources.data && sources.data.length > 0 && (
          <div className="table-wrap">
            <table className="table">
              <thead>
                <tr>
                  <th>Name</th>
                  <th>Folder and pages</th>
                  <th className="num">Documents</th>
                  <th className="num">Chunks</th>
                  <th>Vectors</th>
                  <th>Indexed</th>
                  <th />
                </tr>
              </thead>
              <tbody>
                {sources.data.map((source) => (
                  <tr key={source.sourceId}>
                    <td>
                      <strong>{source.name}</strong> <Badge tone={source.kind === 'code' ? 'info' : 'muted'}>{source.kind}</Badge>
                      {source.description && <div className="muted small">{source.description}</div>}
                    </td>
                    <td className="mono small">
                      {source.rootPath}
                      {source.urls.map((url) => (
                        <div key={url}>{url}</div>
                      ))}
                    </td>
                    <td className="num">
                      <Link className="link" to={`/documents?source=${source.sourceId}`}>
                        {formatNumber(source.documentCount)}
                      </Link>
                    </td>
                    <td className="num">{formatNumber(source.chunkCount)}</td>
                    <td className="vectors">
                      <Progress value={percent(source.embeddedCount, source.chunkCount)} label="Chunks with vectors" />
                      <span className="small muted">{percent(source.embeddedCount, source.chunkCount)}%</span>
                    </td>
                    <td className="small">{formatDate(source.indexedAt)}</td>
                    <td className="row-actions">
                      <button type="button" className="button button--small" disabled={busy} onClick={() => void act(() => api.indexSource(source.sourceId))}>
                        Index
                      </button>
                      <button type="button" className="button button--small" disabled={busy || form !== null} onClick={() => edit(source)}>
                        Edit
                      </button>
                      <button type="button" className="button button--small button--danger" disabled={busy} onClick={() => remove(source)}>
                        Delete
                      </button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </Panel>

      <Panel title="Runs of the indexer">{runs.data && <RunsTable runs={runs.data} />}</Panel>
    </div>
  );
}
