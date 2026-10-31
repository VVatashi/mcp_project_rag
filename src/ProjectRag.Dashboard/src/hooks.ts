import { useCallback, useEffect, useEffectEvent, useState } from 'react';

export interface Loaded<T> {
  data: T | undefined;
  error: string | undefined;
  reload: () => void;
}

/**
 * Loads data with a function and loads it again when the keys change.
 * With an interval the hook polls, and it skips a poll while the tab is hidden.
 */
export function useLoad<T>(load: () => Promise<T>, keys: readonly unknown[], intervalMs = 0): Loaded<T> {
  const [data, setData] = useState<T>();
  const [error, setError] = useState<string>();
  const [version, setVersion] = useState(0);
  const loadLatest = useEffectEvent(load);

  const reload = useCallback(() => setVersion((value) => value + 1), []);

  useEffect(() => {
    let active = true;
    const run = async () => {
      try {
        const value = await loadLatest();
        if (active) {
          setData(value);
          setError(undefined);
        }
      } catch (exception) {
        if (active) setError(exception instanceof Error ? exception.message : String(exception));
      }
    };

    void run();
    const timer = intervalMs > 0 ? window.setInterval(() => !document.hidden && void run(), intervalMs) : undefined;
    return () => {
      active = false;
      if (timer !== undefined) window.clearInterval(timer);
    };
    // The keys stand for the inputs of the load function.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [...keys, version, intervalMs]);

  return { data, error, reload };
}

/** Reads and writes the part of the location hash after "#/", for example "documents/12". */
export function useHashRoute(): [string, (route: string) => void] {
  const read = () => window.location.hash.replace(/^#\/?/, '');
  const [route, setRoute] = useState(read);

  useEffect(() => {
    const onChange = () => setRoute(read());
    window.addEventListener('hashchange', onChange);
    return () => window.removeEventListener('hashchange', onChange);
  }, []);

  const navigate = useCallback((next: string) => {
    window.location.hash = `/${next}`;
  }, []);

  return [route, navigate];
}
