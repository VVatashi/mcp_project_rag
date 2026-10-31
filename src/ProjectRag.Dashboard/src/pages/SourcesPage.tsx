import { useState, type FormEvent } from 'react';
import { Link } from 'react-router';

import { api } from '../api';
import { Empty, ErrorBanner, Panel, Progress } from '../components/Common';
import { RunsTable } from '../components/RunsTable';
import { formatDate, formatNumber, percent } from '../format';
import { useLoad } from '../hooks';
import type { CategoryRule, Source, SourceInput, TextReplacement } from '../types';

interface FormState {
  sourceId: number | null;
  name: string;
  description: string;
  rootPath: string;
  includeGlobs: string;
  excludeGlobs: string;
  categoryRules: string;
  replacements: string;
}

const emptyForm: FormState = { sourceId: null, name: '', description: '', rootPath: '', includeGlobs: '', excludeGlobs: '', categoryRules: '', replacements: '' };

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

  const edit = (source: Source) =>
    setForm({
      sourceId: source.sourceId,
      name: source.name,
      description: source.description,
      rootPath: source.rootPath,
      includeGlobs: source.includeGlobs.join('\n'),
      excludeGlobs: source.excludeGlobs.join('\n'),
      categoryRules: writeRules(source.categoryRules),
      replacements: source.replacements.length > 0 ? JSON.stringify(source.replacements, null, 2) : '',
    });

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

    const input: SourceInput = {
      name: form.name,
      description: form.description,
      rootPath: form.rootPath,
      includeGlobs: lines(form.includeGlobs),
      // An empty field keeps the default patterns for a new source.
      excludeGlobs: form.sourceId === null && !form.excludeGlobs.trim() ? null : lines(form.excludeGlobs),
      categoryRules: readRules(form.categoryRules),
      replacements,
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
          <button type="button" className="button button--primary" onClick={() => setForm(emptyForm)} disabled={form !== null}>
            Add a source
          </button>
        }
      >
        {form && (
          <form className="form" onSubmit={(event) => void submit(event)}>
            <div className="form__row">
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
                <input value={form.rootPath} onChange={(event) => setForm({ ...form, rootPath: event.target.value })} placeholder="C:\Docs\my-project" required />
                <small>The absolute path of a folder on this machine.</small>
              </label>
            </div>
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
                  placeholder={'Empty: Markdown, text and PDF\n**/*.md'}
                />
              </label>
              <label>
                Files to skip
                <textarea
                  rows={4}
                  value={form.excludeGlobs}
                  onChange={(event) => setForm({ ...form, excludeGlobs: event.target.value })}
                  placeholder={'Empty: .git, node_modules, bin, obj\n**/drafts/**'}
                />
              </label>
            </div>
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
                  <th>Folder</th>
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
                      <strong>{source.name}</strong>
                      {source.description && <div className="muted small">{source.description}</div>}
                    </td>
                    <td className="mono small">{source.rootPath}</td>
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
