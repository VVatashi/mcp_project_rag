import { GlobalWorkerOptions, getDocument, type PDFDocumentProxy } from 'pdfjs-dist';
import workerUrl from 'pdfjs-dist/build/pdf.worker.min.mjs?url';
import { useEffect, useRef, useState } from 'react';

import { api } from '../api';

GlobalWorkerOptions.workerSrc = workerUrl;

// The parsed files by document, so a change of the page does not load the file again.
const documents = new Map<number, Promise<PDFDocumentProxy>>();

function load(documentId: number): Promise<PDFDocumentProxy> {
  let pdf = documents.get(documentId);
  if (!pdf) {
    pdf = api.fileBytes(documentId).then((data) => getDocument({ data }).promise);
    pdf.catch(() => documents.delete(documentId));
    documents.set(documentId, pdf);
  }

  return pdf;
}

/**
 * Draws a page of the original PDF file with pdf.js, next to its Markdown, so a reader can compare the import with the original.
 * The viewer of a browser ignores the page in the address of a blob in some browsers, and the file needs the token of the server.
 */
export function PdfPage({ documentId, page }: { documentId: number; page: number }) {
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const [error, setError] = useState<string>();
  const [count, setCount] = useState<number>();

  useEffect(() => {
    let cancelled = false;
    let task: { cancel: () => void } | undefined;
    const draw = async () => {
      try {
        const pdf = await load(documentId);
        if (cancelled) return;
        setCount(pdf.numPages);
        const pdfPage = await pdf.getPage(Math.min(Math.max(1, page), pdf.numPages));
        const canvas = canvasRef.current;
        if (cancelled || !canvas) return;

        // The page fills the width of its panel at the density of the screen.
        const width = canvas.parentElement?.clientWidth ?? 600;
        const viewport = pdfPage.getViewport({ scale: width / pdfPage.getViewport({ scale: 1 }).width });
        const ratio = window.devicePixelRatio || 1;
        canvas.width = Math.floor(viewport.width * ratio);
        canvas.height = Math.floor(viewport.height * ratio);
        canvas.style.width = `${Math.floor(viewport.width)}px`;
        canvas.style.height = `${Math.floor(viewport.height)}px`;
        const render = pdfPage.render({ canvas, viewport, transform: ratio === 1 ? undefined : [ratio, 0, 0, ratio, 0, 0] });
        task = render;
        await render.promise;
        setError(undefined);
      } catch (exception) {
        if (!cancelled && !(exception instanceof Error && exception.name === 'RenderingCancelledException'))
          setError(exception instanceof Error ? exception.message : String(exception));
      }
    };

    void draw();
    return () => {
      cancelled = true;
      task?.cancel();
    };
  }, [documentId, page]);

  return (
    <div className="pdf-page">
      {error && <p className="muted">{error}</p>}
      {!error && count === undefined && <p className="muted">Loading the PDF file…</p>}
      <canvas ref={canvasRef} />
    </div>
  );
}
