import type { ReactNode } from 'react';

import { formatNumber } from '../format';
import type { RunStatus } from '../types';

/** The buttons of a long list that shows one page at a time. A list of one page shows no buttons. */
export function Pager({ offset, pageSize, total, onChange }: { offset: number; pageSize: number; total: number; onChange: (offset: number) => void }) {
  if (total <= pageSize) return null;
  return (
    <div className="pager">
      <button type="button" className="button button--small" disabled={offset === 0} onClick={() => onChange(Math.max(0, offset - pageSize))}>
        Previous
      </button>
      <span className="muted small">
        {formatNumber(Math.min(offset + 1, total))}–{formatNumber(Math.min(total, offset + pageSize))} of {formatNumber(total)}
      </span>
      <button type="button" className="button button--small" disabled={offset + pageSize >= total} onClick={() => onChange(offset + pageSize)}>
        Next
      </button>
    </div>
  );
}

export function Panel({ title, actions, children }: { title: string; actions?: ReactNode; children: ReactNode }) {
  return (
    <section className="panel">
      <header className="panel__header">
        <h2>{title}</h2>
        {actions && <div className="panel__actions">{actions}</div>}
      </header>
      <div className="panel__body">{children}</div>
    </section>
  );
}

export function ErrorBanner({ message }: { message: string | undefined }) {
  return message ? (
    <div className="banner banner--error" role="alert">
      {message}
    </div>
  ) : null;
}

export function Badge({ tone, children, title }: { tone: 'ok' | 'warn' | 'error' | 'muted' | 'info'; children: ReactNode; title?: string }) {
  return (
    <span className={`badge badge--${tone}`} title={title}>
      {children}
    </span>
  );
}

const runTones: Record<RunStatus, 'ok' | 'warn' | 'error' | 'info'> = {
  running: 'info',
  succeeded: 'ok',
  failed: 'error',
  canceled: 'warn',
};

export function RunBadge({ status, warning }: { status: RunStatus; warning?: string | null }) {
  return (
    <Badge tone={status === 'succeeded' && warning ? 'warn' : runTones[status]} title={warning ?? undefined}>
      {status}
      {status === 'succeeded' && warning ? ' · warning' : ''}
    </Badge>
  );
}

export function Progress({ value, label }: { value: number; label?: string }) {
  return (
    <div className="progress" role="progressbar" aria-valuemin={0} aria-valuemax={100} aria-valuenow={value} aria-label={label}>
      <div className="progress__bar" style={{ width: `${Math.min(100, Math.max(0, value))}%` }} />
    </div>
  );
}

export function Empty({ children }: { children: ReactNode }) {
  return <p className="empty">{children}</p>;
}
