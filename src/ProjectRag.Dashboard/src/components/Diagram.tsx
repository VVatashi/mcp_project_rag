import { useCallback, useEffect, useRef, useState, type MouseEvent as ReactMouseEvent, type PointerEvent as ReactPointerEvent } from 'react';
import { useNavigate } from 'react-router';

import type { Diagram as DiagramModel } from '../types';

import type { Graphviz } from '@hpcc-js/wasm-graphviz';

let graphvizLoad: Promise<Graphviz> | undefined;

function loadGraphviz() {
  graphvizLoad ??= import('@hpcc-js/wasm-graphviz').then((module) => module.Graphviz.load());
  graphvizLoad.catch(() => {
    graphvizLoad = undefined;
  });
  return graphvizLoad;
}

interface Rendered {
  dot: string;
  svg?: string;
  error?: string;
}

interface View {
  x: number;
  y: number;
  k: number;
}

const minScale = 0.05;
const maxScale = 8;
const dragThreshold = 4;
/** The options of Graphviz unflatten: stagger the leaves of a star over a few ranks, so the layout does not become one wide row. */
const unflattenMinlen = 3;
const unflattenChain = 4;

/** Graphviz writes the tooltips as xlink:title on links and the ids of nodes as titles. Only the tooltips are useful. */
function prepare(host: HTMLElement): { width: number; height: number } | undefined {
  const svg = host.querySelector('svg');
  if (!svg) return undefined;

  for (const link of svg.querySelectorAll('a')) {
    const text = link.getAttribute('xlink:title') ?? link.getAttribute('title');
    if (!text) continue;
    const title = document.createElementNS('http://www.w3.org/2000/svg', 'title');
    title.textContent = text;
    link.prepend(title);
  }

  for (const title of svg.querySelectorAll('g.graph > title, g.node > title, g.edge > title, g.cluster > title')) title.remove();

  const box = svg.viewBox.baseVal;
  const width = box.width || svg.width.baseVal.value;
  const height = box.height || svg.height.baseVal.value;
  svg.setAttribute('width', String(width));
  svg.setAttribute('height', String(height));
  return { width, height };
}

interface DiagramProps {
  diagram: DiagramModel;
  height?: number;
  /** With "modified" the wheel zooms only with Ctrl or Cmd, so the page still scrolls over the diagram. */
  wheel?: 'always' | 'modified';
  /** Changes the address of a clicked node before the app opens it. */
  mapHref?: (href: string) => string;
}

export function Diagram({ diagram, height = 560, wheel = 'always', mapHref }: DiagramProps) {
  const navigate = useNavigate();
  const [rendered, setRendered] = useState<Rendered>();
  const stage = useRef<HTMLDivElement>(null);
  const canvas = useRef<HTMLDivElement>(null);
  const size = useRef<{ width: number; height: number }>(undefined);
  const view = useRef<View>({ x: 0, y: 0, k: 1 });
  const drag = useRef<{ pointerId: number; startX: number; startY: number; originX: number; originY: number; moved: boolean }>(undefined);
  const wasDragged = useRef(false);

  const apply = useCallback((next: View) => {
    view.current = next;
    if (canvas.current) canvas.current.style.transform = `translate(${next.x}px, ${next.y}px) scale(${next.k})`;
  }, []);

  const place = useCallback(
    (k: number) => {
      const area = stage.current;
      const content = size.current;
      if (!area || !content) return;
      apply({ k, x: (area.clientWidth - content.width * k) / 2, y: (area.clientHeight - content.height * k) / 2 });
    },
    [apply],
  );

  const fit = useCallback(() => {
    const area = stage.current;
    const content = size.current;
    if (!area || !content || area.clientWidth === 0 || area.clientHeight === 0) return;
    const padding = 16;
    const k = Math.min((area.clientWidth - padding) / content.width, (area.clientHeight - padding) / content.height, 1);
    place(Math.max(minScale, k));
  }, [place]);

  const zoomAt = useCallback(
    (factor: number, px: number, py: number) => {
      const current = view.current;
      const k = Math.min(maxScale, Math.max(minScale, current.k * factor));
      apply({ k, x: px - ((px - current.x) * k) / current.k, y: py - ((py - current.y) * k) / current.k });
    },
    [apply],
  );

  const zoomCenter = (factor: number) => {
    const area = stage.current;
    if (area) zoomAt(factor, area.clientWidth / 2, area.clientHeight / 2);
  };

  useEffect(() => {
    let active = true;
    loadGraphviz()
      .then((graphviz) => {
        const source = diagram.view === 'projects' ? diagram.dot : graphviz.unflatten(diagram.dot, unflattenMinlen, true, unflattenChain);
        const svg = graphviz.dot(source, 'svg');
        if (active) setRendered({ dot: diagram.dot, svg });
      })
      .catch((exception: unknown) => {
        if (active) setRendered({ dot: diagram.dot, error: exception instanceof Error ? exception.message : String(exception) });
      });
    return () => {
      active = false;
    };
  }, [diagram.dot, diagram.view]);

  useEffect(() => {
    const host = canvas.current;
    if (!host || !rendered) return;
    host.innerHTML = rendered.svg ?? '';
    size.current = rendered.svg ? prepare(host) : undefined;
    fit();
  }, [rendered, fit]);

  useEffect(() => {
    const area = stage.current;
    if (!area) return;
    const observer = new ResizeObserver(() => fit());
    observer.observe(area);
    return () => observer.disconnect();
  }, [fit]);

  useEffect(() => {
    const area = stage.current;
    if (!area) return;
    const onWheel = (event: WheelEvent) => {
      if (wheel === 'modified' && !event.ctrlKey && !event.metaKey) return;
      event.preventDefault();
      const bounds = area.getBoundingClientRect();
      zoomAt(Math.exp(-event.deltaY * 0.0015), event.clientX - bounds.left, event.clientY - bounds.top);
    };
    area.addEventListener('wheel', onWheel, { passive: false });
    return () => area.removeEventListener('wheel', onWheel);
  }, [wheel, zoomAt]);

  const onPointerDown = (event: ReactPointerEvent<HTMLDivElement>) => {
    if (event.button !== 0) return;
    wasDragged.current = false;
    drag.current = { pointerId: event.pointerId, startX: event.clientX, startY: event.clientY, originX: view.current.x, originY: view.current.y, moved: false };
  };

  const onPointerMove = (event: ReactPointerEvent<HTMLDivElement>) => {
    const state = drag.current;
    if (!state || state.pointerId !== event.pointerId) return;
    const dx = event.clientX - state.startX;
    const dy = event.clientY - state.startY;
    if (!state.moved) {
      if (Math.hypot(dx, dy) < dragThreshold) return;
      state.moved = true;
      event.currentTarget.setPointerCapture(event.pointerId);
    }
    apply({ ...view.current, x: state.originX + dx, y: state.originY + dy });
  };

  const onPointerEnd = (event: ReactPointerEvent<HTMLDivElement>) => {
    const state = drag.current;
    if (!state || state.pointerId !== event.pointerId) return;
    wasDragged.current = state.moved;
    drag.current = undefined;
  };

  const onClick = (event: ReactMouseEvent<HTMLDivElement>) => {
    if (wasDragged.current) {
      wasDragged.current = false;
      event.preventDefault();
      return;
    }

    const link = (event.target as Element).closest('a');
    if (!link) return;
    const href = link.getAttribute('xlink:href') ?? link.getAttribute('href');
    if (!href || !href.startsWith('/')) return;
    if (event.ctrlKey || event.metaKey || event.shiftKey || event.altKey || event.button !== 0) return;
    event.preventDefault();
    navigate(mapHref ? mapHref(href) : href);
  };

  const loading = rendered?.dot !== diagram.dot;
  const error = rendered?.dot === diagram.dot ? rendered.error : undefined;

  return (
    <div className="diagram">
      <div className="diagram__bar">
        <button type="button" className="button button--small" onClick={() => zoomCenter(1.25)} aria-label="Zoom in">
          +
        </button>
        <button type="button" className="button button--small" onClick={() => zoomCenter(0.8)} aria-label="Zoom out">
          −
        </button>
        <button type="button" className="button button--small" onClick={fit}>
          Fit
        </button>
        <button type="button" className="button button--small" onClick={() => place(1)}>
          100%
        </button>
      </div>
      <div
        ref={stage}
        className="diagram__stage"
        style={{ height }}
        onPointerDown={onPointerDown}
        onPointerMove={onPointerMove}
        onPointerUp={onPointerEnd}
        onPointerCancel={onPointerEnd}
        onClick={onClick}
      >
        <div ref={canvas} className="diagram__canvas" />
        {loading && <div className="diagram__state muted">Loading the diagram…</div>}
        {error && (
          <div className="diagram__state">
            <div className="banner banner--error" role="alert">
              The diagram cannot be drawn: {error}
            </div>
          </div>
        )}
      </div>
    </div>
  );
}
