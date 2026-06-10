import { useCallback, useEffect, useMemo, useRef } from "react";
import { useSearchParams } from "react-router-dom";

/**
 * Filter values that can be serialised to URL query params.
 *
 * Supported shapes:
 *  - `string`        → single param (`?name=value`). Empty string means "use default" and is omitted from the URL.
 *  - `string[]`      → repeated param (`?status=Failed&status=Invalid`). Empty array means "use default".
 *  - `undefined`     → omitted.
 *
 * Other shapes (Date, number, enum) should be converted to/from string by the caller before passing in.
 */
export type FilterValue = string | string[] | undefined;
export type FilterValues = Record<string, FilterValue>;

interface UseUrlFiltersResult<T extends FilterValues> {
  /** Filter values derived from the URL, with `defaults` applied for any params that are absent. */
  applied: T;
  /** Push the given filter set to the URL. Adds a new history entry by default so browser Back returns here. */
  applyFilters: (next: Partial<T>) => void;
  /** Clear all filter params from the URL (preserving any non-filter query params). */
  resetFilters: () => void;
  /** Replace the URL params without adding a history entry — used for explicitly materialising defaults at mount. */
  setFiltersWithoutHistory: (next: Partial<T>) => void;
}

/**
 * Options for {@link useUrlFilters}.
 */
export interface UseUrlFiltersOptions {
  /**
   * When set, the applied filters are mirrored to `sessionStorage[persistKey]`
   * on every change, and restored on first mount IF the URL carries none of the
   * owned filter params. The URL always wins when present (so reload / Back /
   * shared links behave normally); sessionStorage only fills the gap when the
   * page is re-entered with a clean URL (e.g. via the sidebar).
   */
  persistKey?: string;
}

/**
 * Source-of-truth for filter state via the URL query string.
 *
 * Browser Back works for free — when the URL is restored, `applied` re-derives from the URL.
 * Callers should pass a STABLE `defaults` reference (define outside the component, or wrap in `useMemo(() => ({...}), [])`)
 * to avoid spurious re-runs of effects that depend on `applied`.
 */
export function useUrlFilters<T extends FilterValues>(
  defaults: T,
  options?: UseUrlFiltersOptions,
): UseUrlFiltersResult<T> {
  const [params, setParams] = useSearchParams();
  const persistKey = options?.persistKey;

  const applied = useMemo<T>(() => {
    const result = { ...defaults } as T;
    for (const key of Object.keys(defaults) as (keyof T & string)[]) {
      const def = defaults[key];
      if (Array.isArray(def)) {
        const values = params.getAll(key);
        if (values.length > 0) {
          (result as Record<string, FilterValue>)[key] = values;
        }
      } else {
        const value = params.get(key);
        if (value !== null) {
          (result as Record<string, FilterValue>)[key] = value;
        }
      }
    }
    return result;
  }, [params, defaults]);

  const buildNextParams = useCallback(
    (next: Partial<T>): URLSearchParams => {
      const out = new URLSearchParams();

      // Preserve any params not owned by this filter set (e.g. unrelated UI flags).
      for (const [k, v] of params.entries()) {
        if (!(k in defaults)) {
          out.append(k, v);
        }
      }

      for (const key of Object.keys(defaults) as (keyof T & string)[]) {
        const def = defaults[key];
        const value = next[key];

        if (Array.isArray(def)) {
          const arr = (Array.isArray(value) ? value : (def as string[])) as string[];
          const defArr = (def ?? []) as string[];
          // Only serialise when different from the default array.
          const sameAsDefault =
            arr.length === defArr.length &&
            arr.every((v, i) => v === defArr[i]);
          if (arr.length > 0 && !sameAsDefault) {
            for (const v of arr) out.append(key, v);
          }
        } else {
          const str = (typeof value === "string" ? value : (def as string | undefined)) ?? "";
          const defStr = (def as string | undefined) ?? "";
          // Only serialise when non-empty AND different from the default string.
          if (str !== "" && str !== defStr) {
            out.set(key, str);
          }
        }
      }

      return out;
    },
    [params, defaults],
  );

  const applyFilters = useCallback(
    (next: Partial<T>) => {
      setParams(buildNextParams(next));
    },
    [buildNextParams, setParams],
  );

  const setFiltersWithoutHistory = useCallback(
    (next: Partial<T>) => {
      setParams(buildNextParams(next), { replace: true });
    },
    [buildNextParams, setParams],
  );

  const resetFilters = useCallback(() => {
    const out = new URLSearchParams();
    for (const [k, v] of params.entries()) {
      if (!(k in defaults)) {
        out.append(k, v);
      }
    }
    if (persistKey) {
      try {
        sessionStorage.removeItem(persistKey);
      } catch {
        /* storage unavailable — ignore */
      }
    }
    setParams(out);
  }, [params, defaults, setParams, persistKey]);

  // Snapshot the persisted filters ONCE during the first render, before the
  // mirror effect below can overwrite sessionStorage with the (default) initial
  // `applied`. `null` = not yet captured; `{}` = captured but absent/invalid.
  const savedSnapshotRef = useRef<Partial<T> | null>(null);
  if (persistKey && savedSnapshotRef.current === null) {
    try {
      const raw = sessionStorage.getItem(persistKey);
      savedSnapshotRef.current = raw ? (JSON.parse(raw) as Partial<T>) : {};
    } catch {
      savedSnapshotRef.current = {};
    }
  }

  const isAllDefault = useCallback(
    (vals: T): boolean =>
      (Object.keys(defaults) as (keyof T & string)[]).every((key) => {
        const def = defaults[key];
        const val = vals[key];
        if (Array.isArray(def)) {
          const arr = (Array.isArray(val) ? val : def) as string[];
          return (
            arr.length === def.length &&
            arr.every((v, i) => v === (def as string[])[i])
          );
        }
        return (
          ((val as string | undefined) ?? "") ===
          ((def as string | undefined) ?? "")
        );
      }),
    [defaults],
  );

  // Mirror NON-default applied filters to sessionStorage so they survive a full
  // navigation away and back (the URL only survives reload / Back). Defaults are
  // never stored — they are the fallback anyway, so a reset cleanly forgets.
  useEffect(() => {
    if (!persistKey) return;
    try {
      if (isAllDefault(applied)) {
        sessionStorage.removeItem(persistKey);
      } else {
        sessionStorage.setItem(persistKey, JSON.stringify(applied));
      }
    } catch {
      /* quota / private mode — ignore */
    }
  }, [persistKey, applied, isAllDefault]);

  // On first mount, if the URL has none of our owned params, rehydrate the
  // last-used filters (from the render-time snapshot) by writing them to the
  // URL (no history entry). The URL takes precedence whenever it carries a param.
  const hydratedRef = useRef(false);
  useEffect(() => {
    if (!persistKey || hydratedRef.current) return;
    hydratedRef.current = true;
    const saved = savedSnapshotRef.current;
    if (!saved || Object.keys(saved).length === 0) return;
    const hasOwnedParam = (Object.keys(defaults) as string[]).some((k) =>
      params.has(k),
    );
    if (hasOwnedParam) return;
    setParams(buildNextParams(saved), { replace: true });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  return { applied, applyFilters, resetFilters, setFiltersWithoutHistory };
}
