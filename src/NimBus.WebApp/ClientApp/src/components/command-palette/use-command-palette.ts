import { useContext } from "react";
import {
  CommandPaletteContext,
  type CommandPaletteContextValue,
} from "./command-palette-context";

/**
 * Read the command-palette state from anywhere in the tree.
 * Throws if used outside `CommandPaletteProvider` so missing wiring fails loudly.
 */
export function useCommandPalette(): CommandPaletteContextValue {
  const ctx = useContext(CommandPaletteContext);
  if (!ctx) {
    throw new Error(
      "useCommandPalette must be used inside <CommandPaletteProvider>",
    );
  }
  return ctx;
}
