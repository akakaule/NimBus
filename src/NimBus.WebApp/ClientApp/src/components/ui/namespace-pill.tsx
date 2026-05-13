import { type ReactNode } from "react";
import { cn } from "lib/utils";

export interface NamespacePillProps {
  children: ReactNode;
  className?: string;
  size?: "sm" | "md";
}

/**
 * Mono-font pill identifying a contract namespace (e.g. CrmErpDemo.Contracts.Events).
 * Purple is reserved for namespace/type metadata — distinct from primary (action)
 * and status hues so a glance instantly classifies the chrome.
 */
export const NamespacePill = ({
  children,
  className,
  size = "md",
}: NamespacePillProps) => (
  <span
    className={cn(
      "inline-flex items-center font-mono font-semibold tracking-wide",
      "bg-nimbus-purple-50 text-nimbus-purple rounded-full",
      "dark:bg-purple-950/40 dark:text-purple-300",
      size === "sm" ? "text-[10.5px] px-2 py-0.5" : "text-[12px] px-2.5 py-1",
      className,
    )}
  >
    {children}
  </span>
);
