import { useCallback, useEffect, useEffectEvent, useRef, useState } from 'react';

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
  const [failure, setFailure] = useState<{ message: string; keys: readonly unknown[] }>();
  const [version, setVersion] = useState(0);
  const loadLatest = useEffectEvent(load);
  const lastKeys = useRef(keys);

  const reload = useCallback(() => setVersion((value) => value + 1), []);

  useEffect(() => {
    let active = true;
    let busy = false;
    if (!sameKeys(lastKeys.current, keys)) {
      lastKeys.current = keys;
      setFailure(undefined);
    }
    const run = async () => {
      // A slow server must not collect a pile of polls.
      if (busy) return;
      busy = true;
      try {
        const value = await loadLatest();
        if (active) {
          setData(value);
          setFailure(undefined);
        }
      } catch (exception) {
        if (active) setFailure({ message: exception instanceof Error ? exception.message : String(exception), keys });
      } finally {
        busy = false;
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

  const error = failure && sameKeys(failure.keys, keys) ? failure.message : undefined;
  return { data, error, reload };
}

function sameKeys(a: readonly unknown[], b: readonly unknown[]): boolean {
  return a.length === b.length && a.every((value, index) => Object.is(value, b[index]));
}
