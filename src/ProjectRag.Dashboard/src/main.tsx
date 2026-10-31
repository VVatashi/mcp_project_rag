import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';

import { App } from './App';
import './styles/main.scss';

const root = document.getElementById('root');
if (!root) throw new Error('The page has no element #root.');

createRoot(root).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
