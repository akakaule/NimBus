import { useEffect, useRef, useState } from 'react';

const HIGHLIGHT_MS = 2600;
const NONE: ReadonlySet<string> = new Set();

/**
 * True for a moment after `value` changes (never on first render), so an update that arrives
 * from Business Central catches the eye without a reload.
 */
export function useChangeHighlight(value: string | number | null | undefined): boolean {
  const previous = useRef(value);
  const [highlight, setHighlight] = useState(false);

  useEffect(() => {
    if (previous.current === value) return undefined;
    previous.current = value;
    setHighlight(true);
    const timer = setTimeout(() => setHighlight(false), HIGHLIGHT_MS);
    return () => clearTimeout(timer);
  }, [value]);

  return highlight;
}

/**
 * The ids that appeared after the first loaded render (e.g. new timeline entries), for a moment.
 * Pass `loaded = false` until the data has arrived, so the initial load does not count as new.
 */
export function useNewItems(ids: readonly string[], loaded = true): ReadonlySet<string> {
  const seen = useRef<Set<string> | null>(null);
  const [fresh, setFresh] = useState<ReadonlySet<string>>(NONE);
  const signature = ids.join('|');

  useEffect(() => {
    if (!loaded) return undefined;
    const current = signature ? signature.split('|') : [];
    if (seen.current === null) {
      seen.current = new Set(current);
      return undefined;
    }
    const known = seen.current;
    const added = current.filter((id) => !known.has(id));
    added.forEach((id) => known.add(id));
    if (added.length > 0) setFresh(new Set(added));
    const timer = setTimeout(() => setFresh(NONE), HIGHLIGHT_MS);
    return () => clearTimeout(timer);
  }, [signature, loaded]);

  return fresh;
}
