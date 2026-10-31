import react from '@vitejs/plugin-react';
import { defineConfig } from 'vite';

// The build writes the static files of the server. The dev server sends the calls of the web API to the running server.
export default defineConfig({
  plugins: [react()],
  build: {
    outDir: '../ProjectRag.Server/wwwroot',
    emptyOutDir: true,
  },
  server: {
    port: 5173,
    strictPort: true,
    proxy: {
      '/api': 'http://127.0.0.1:5180',
    },
  },
});
