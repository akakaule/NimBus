import {
  createContext,
  useContext,
  useEffect,
  useMemo,
  useState,
  type ReactNode,
} from "react";
import { createPortal } from "react-dom";
import { cn } from "lib/utils";

// The Settings side panel frame (Spec 038 §8). A settings component opts into
// the frame through the helpers below; rendered standalone (no frame above it)
// every helper falls back to today's behaviour, so the component works and
// tests the same either way.

interface PanelFrameValue {
  activeTab: string;
  reviewing: boolean;
  footer: HTMLElement | null;
  setDirty: (count: number) => void;
  setReviewing: (reviewing: boolean) => void;
}

const PanelFrameContext = createContext<PanelFrameValue | null>(null);

/** True when rendered inside the Settings panel frame. */
export const useInPanel = (): boolean => useContext(PanelFrameContext) !== null;

/**
 * One tab's worth of a settings component. Inside the frame it renders only on
 * its tab, and not while the review screen is showing; standalone it always renders.
 */
export function PanelSection({ tab, children }: { tab: string; children: ReactNode }) {
  const frame = useContext(PanelFrameContext);
  if (frame && (frame.reviewing || frame.activeTab !== tab)) return null;
  return <>{children}</>;
}

/**
 * The component's save/discard controls. Inside the frame they move into its
 * sticky footer; standalone they render in place. Submit buttons portalled
 * out of their form need `form="<form id>"`.
 */
export function PanelFooter({ children }: { children: ReactNode }) {
  const frame = useContext(PanelFrameContext);
  if (!frame) return <>{children}</>;
  return frame.footer ? createPortal(children, frame.footer) : null;
}

/** Reports the component's unsaved-change count to the frame (drives the close guard). */
export function usePanelDirty(count: number) {
  const frame = useContext(PanelFrameContext);
  const setDirty = frame?.setDirty;
  useEffect(() => {
    if (!setDirty) return;
    setDirty(count);
    return () => setDirty(0);
  }, [setDirty, count]);
}

/** While true, the frame hides its tabs and sections so the review screen replaces the body. */
export function usePanelReviewing(reviewing: boolean) {
  const frame = useContext(PanelFrameContext);
  const setReviewing = frame?.setReviewing;
  useEffect(() => {
    if (!setReviewing) return;
    setReviewing(reviewing);
    return () => setReviewing(false);
  }, [setReviewing, reviewing]);
}

export interface PanelTab {
  id: string;
  label: string;
}

export interface SettingsPanelFrameProps {
  title: string;
  /** Status badge shown beside the title. */
  status?: ReactNode;
  tabs: readonly PanelTab[];
  activeTab: string;
  onTabChange: (tab: string) => void;
  onClose: () => void;
  /** Reports the unsaved-change count, so the page can guard closing. */
  onDirtyChange: (count: number) => void;
  /** When saved changes take effect, e.g. "Applies within 30 s". */
  applies: string;
  children: ReactNode;
}

/** Header, tab bar, scrolling body and sticky footer around one settings component. */
export function SettingsPanelFrame({
  title,
  status,
  tabs,
  activeTab,
  onTabChange,
  onClose,
  onDirtyChange,
  applies,
  children,
}: SettingsPanelFrameProps) {
  const [footer, setFooter] = useState<HTMLElement | null>(null);
  const [dirty, setDirty] = useState(0);
  const [reviewing, setReviewing] = useState(false);

  useEffect(() => onDirtyChange(dirty), [dirty, onDirtyChange]);

  const value = useMemo<PanelFrameValue>(
    () => ({ activeTab, reviewing, footer, setDirty, setReviewing }),
    [activeTab, reviewing, footer],
  );

  return (
    <PanelFrameContext.Provider value={value}>
      <header className="border-b border-border px-6 pt-3">
        <div className="flex items-center gap-3">
          <div className="font-mono text-[11px] text-muted-foreground">Settings / {title}</div>
          <div className="flex-1" />
          <button
            type="button"
            aria-label={`Close ${title}`}
            onClick={onClose}
            className="inline-flex h-8 w-8 items-center justify-center rounded-nb-sm text-foreground hover:bg-muted"
          >
            <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" aria-hidden="true">
              <path d="M6 6l12 12M18 6L6 18" />
            </svg>
          </button>
        </div>
        <div className="mt-1 flex flex-wrap items-center gap-2.5 pb-3">
          <h2 className="m-0 text-[21px] font-bold">{title}</h2>
          {status}
        </div>
        {tabs.length > 1 && !reviewing && (
          <div role="tablist" aria-label={`${title} sections`} className="-mb-px flex gap-1 overflow-x-auto">
            {tabs.map((tab) => {
              const selected = tab.id === activeTab;
              return (
                <button
                  key={tab.id}
                  type="button"
                  role="tab"
                  aria-selected={selected}
                  tabIndex={selected ? 0 : -1}
                  onClick={() => onTabChange(tab.id)}
                  className={cn(
                    "whitespace-nowrap border-b-2 px-3 py-2 text-[13.5px] transition-colors",
                    selected
                      ? "border-primary font-semibold text-foreground"
                      : "border-transparent text-muted-foreground hover:text-foreground",
                  )}
                >
                  {tab.label}
                </button>
              );
            })}
          </div>
        )}
      </header>
      <div className="min-h-0 flex-1 overflow-y-auto p-6">{children}</div>
      <footer className="flex flex-wrap items-center gap-3 border-t border-border bg-card px-6 py-3">
        <p className="m-0 mr-auto text-sm text-muted-foreground">
          {dirty > 0 ? (
            <b className="font-semibold text-foreground">
              {dirty} unsaved {dirty === 1 ? "change" : "changes"}
            </b>
          ) : (
            "No unsaved changes"
          )}
          {" · "}
          {applies}
        </p>
        <div ref={setFooter} className="flex flex-wrap items-center justify-end gap-2" />
      </footer>
    </PanelFrameContext.Provider>
  );
}
