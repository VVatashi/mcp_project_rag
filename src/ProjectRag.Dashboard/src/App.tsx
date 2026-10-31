import { getToken, setToken } from './api';
import { useHashRoute } from './hooks';
import { DocumentPage } from './pages/DocumentPage';
import { DocumentsPage } from './pages/DocumentsPage';
import { SearchPage } from './pages/SearchPage';
import { SourcesPage } from './pages/SourcesPage';
import { StatusPage } from './pages/StatusPage';

const sections = [
  { route: 'status', label: 'Status' },
  { route: 'search', label: 'Search' },
  { route: 'sources', label: 'Sources' },
  { route: 'documents', label: 'Documents' },
] as const;

function parseRoute(route: string): { path: string[]; parameters: URLSearchParams } {
  const [path = '', search = ''] = route.split('?');
  return { path: path.split('/').filter(Boolean), parameters: new URLSearchParams(search) };
}

function numberOrNull(value: string | null | undefined): number | null {
  const parsed = value ? Number(value) : NaN;
  return Number.isFinite(parsed) ? parsed : null;
}

export function App() {
  const [route, navigate] = useHashRoute();
  const { path, parameters } = parseRoute(route);
  const section = path[0] ?? 'status';

  const askToken = () => {
    const token = window.prompt('The token of the server (Server:Token). Leave it empty to remove it.', getToken());
    if (token !== null) {
      setToken(token.trim());
      window.location.reload();
    }
  };

  let page;
  if (section === 'search') {
    page = <SearchPage onOpenHit={(documentId, offset) => navigate(`documents/${documentId}?offset=${offset}`)} />;
  } else if (section === 'sources') {
    page = <SourcesPage onOpenDocuments={(sourceId) => navigate(`documents?source=${sourceId}`)} />;
  } else if (section === 'documents' && numberOrNull(path[1]) !== null) {
    const documentId = numberOrNull(path[1]) ?? 0;
    page = <DocumentPage key={route} documentId={documentId} offset={numberOrNull(parameters.get('offset'))} onBack={() => navigate('documents')} />;
  } else if (section === 'documents') {
    page = (
      <DocumentsPage
        sourceId={numberOrNull(parameters.get('source'))}
        onSourceChange={(sourceId) => navigate(sourceId === null ? 'documents' : `documents?source=${sourceId}`)}
        onOpen={(documentId) => navigate(`documents/${documentId}`)}
      />
    );
  } else {
    page = <StatusPage />;
  }

  return (
    <div className="app">
      <header className="topbar">
        <a className="topbar__brand" href="#/status">
          ProjectRag
        </a>
        <nav className="topbar__nav">
          {sections.map((item) => (
            <a key={item.route} href={`#/${item.route}`} className={section === item.route ? 'topbar__link topbar__link--on' : 'topbar__link'}>
              {item.label}
            </a>
          ))}
        </nav>
        <button type="button" className="button button--small topbar__token" onClick={askToken}>
          Token{getToken() ? ' ✓' : ''}
        </button>
      </header>
      <main className="main">{page}</main>
    </div>
  );
}
