import { BrowserRouter, Link, Navigate, NavLink, Route, Routes, useParams, useSearchParams } from 'react-router';

import { getToken, setToken } from './api';
import { numberOrNull } from './format';
import { AnchorPage } from './pages/AnchorPage';
import { ChunkPage } from './pages/ChunkPage';
import { CodeDiagramPage } from './pages/CodeDiagramPage';
import { CodeFilePage } from './pages/CodeFilePage';
import { CodePage } from './pages/CodePage';
import { CodeSymbolPage } from './pages/CodeSymbolPage';
import { DocumentPage } from './pages/DocumentPage';
import { DocumentsPage } from './pages/DocumentsPage';
import { SearchPage } from './pages/SearchPage';
import { SourcesPage } from './pages/SourcesPage';
import { StatusPage } from './pages/StatusPage';

const sections = [
  { route: '/status', label: 'Status' },
  { route: '/search', label: 'Search' },
  { route: '/code', label: 'Code' },
  { route: '/sources', label: 'Sources' },
  { route: '/documents', label: 'Documents' },
] as const;

// The pages take numbers. A route with a wrong number goes to the list of documents.

function ChunkRoute() {
  const chunkId = numberOrNull(useParams().chunkId);
  return chunkId === null ? <Navigate to="/documents" replace /> : <ChunkPage key={chunkId} chunkId={chunkId} />;
}

function DocumentRoute() {
  const { documentId, tab } = useParams();
  const id = numberOrNull(documentId);
  return id === null ? <Navigate to="/documents" replace /> : <DocumentPage key={id} documentId={id} tab={tab} />;
}

function AnchorRoute() {
  const id = numberOrNull(useParams().documentId);
  const [parameters] = useSearchParams();
  const name = parameters.get('name');
  return id === null || !name ? <Navigate to="/documents" replace /> : <AnchorPage documentId={id} name={name} />;
}

function CodeFileRoute() {
  const id = numberOrNull(useParams().documentId);
  const [parameters] = useSearchParams();
  const lineValue = numberOrNull(parameters.get('line'));
  const endValue = numberOrNull(parameters.get('end'));
  const line = lineValue !== null && lineValue >= 1 ? Math.floor(lineValue) : null;
  const end = line !== null && endValue !== null && endValue > line ? Math.floor(endValue) : null;
  return id === null ? <Navigate to="/code" replace /> : <CodeFilePage key={`${id}:${line}:${end}`} documentId={id} line={line} end={end} />;
}

function CodeSymbolRoute() {
  const [parameters] = useSearchParams();
  const sourceId = numberOrNull(parameters.get('source'));
  const docId = parameters.get('id');
  const project = parameters.get('project');
  if (sourceId === null || !docId) return <Navigate to="/code?tab=symbols" replace />;
  return <CodeSymbolPage key={`${sourceId}:${docId}:${project ?? ''}`} sourceId={sourceId} docId={docId} project={project} />;
}

function DocumentsRoute() {
  const [parameters] = useSearchParams();
  return <DocumentsPage sourceId={numberOrNull(parameters.get('source'))} category={parameters.get('category')} />;
}

export function App() {
  const askToken = () => {
    const token = window.prompt('The token of the server (Server:Token). Leave it empty to remove it.', getToken());
    if (token !== null) {
      setToken(token.trim());
      window.location.reload();
    }
  };

  return (
    <BrowserRouter>
      <div className="app">
        <header className="topbar">
          <Link className="topbar__brand" to="/status">
            ProjectRag
          </Link>
          <nav className="topbar__nav">
            {sections.map((item) => (
              <NavLink key={item.route} to={item.route} className={({ isActive }) => (isActive ? 'topbar__link topbar__link--on' : 'topbar__link')}>
                {item.label}
              </NavLink>
            ))}
          </nav>
          <button type="button" className="button button--small topbar__token" onClick={askToken}>
            Token{getToken() ? ' ✓' : ''}
          </button>
        </header>
        <main className="main">
          <Routes>
            <Route path="/" element={<Navigate to="/status" replace />} />
            <Route path="/status" element={<StatusPage />} />
            <Route path="/search" element={<SearchPage />} />
            <Route path="/code" element={<CodePage />} />
            <Route path="/code/symbol" element={<CodeSymbolRoute />} />
            <Route path="/code/diagram" element={<CodeDiagramPage />} />
            <Route path="/code/files/:documentId" element={<CodeFileRoute />} />
            <Route path="/sources" element={<SourcesPage />} />
            <Route path="/documents" element={<DocumentsRoute />} />
            <Route path="/documents/:documentId/anchors" element={<AnchorRoute />} />
            <Route path="/documents/:documentId/:tab?" element={<DocumentRoute />} />
            <Route path="/chunks/:chunkId" element={<ChunkRoute />} />
            <Route path="*" element={<Navigate to="/status" replace />} />
          </Routes>
        </main>
      </div>
    </BrowserRouter>
  );
}
