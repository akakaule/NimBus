import { createContext } from "react";

export interface CommandPaletteContextValue {
  isOpen: boolean;
  open: () => void;
  close: () => void;
  toggle: () => void;
}

/**
 * Internal context — consumers use the `useCommandPalette` hook instead of
 * touching this directly so the provider stays the single source of truth
 * for open/close state.
 */
export const CommandPaletteContext =
  createContext<CommandPaletteContextValue | null>(null);
