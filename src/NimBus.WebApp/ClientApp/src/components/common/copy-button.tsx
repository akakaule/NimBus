import { copyToClipboard } from "lib/clipboard";
import { useToast } from "components/ui/toast";
import { cn } from "lib/utils";

export interface CopyButtonProps {
  /** Exact text placed on the clipboard. */
  text: string;
  /** Button label. Defaults to "Copy". */
  label?: string;
  /** What was copied, named in the success toast (e.g. "Payload"). */
  describes?: string;
  className?: string;
}

/**
 * Small copy-to-clipboard action sized for a {@link CodeBlock} header.
 *
 * Callers pass the text that is already on screen — for payloads that is the
 * server-masked copy, so a caller without the PII Reader role copies exactly
 * what they can see rather than the raw event content.
 */
export const CopyButton: React.FC<CopyButtonProps> = ({
  text,
  label = "Copy",
  describes,
  className,
}) => {
  const { addToast } = useToast();

  const copy = async () => {
    try {
      await copyToClipboard(text);
      addToast({
        title: describes ? `${describes} copied` : "Copied to clipboard",
        variant: "success",
        duration: 2000,
      });
    } catch {
      addToast({
        title: "Failed to copy",
        description: "Clipboard access not available",
        variant: "error",
        duration: 2000,
      });
    }
  };

  return (
    <button
      type="button"
      onClick={copy}
      aria-label={describes ? `Copy ${describes.toLowerCase()}` : "Copy to clipboard"}
      className={cn(
        "inline-flex items-center gap-1.5 cursor-pointer",
        "bg-transparent border border-border rounded-nb-sm px-2 py-1",
        "text-[11px] font-mono uppercase tracking-wider text-muted-foreground",
        "hover:text-foreground hover:border-muted-foreground transition-colors",
        className,
      )}
    >
      <svg aria-hidden="true" width="11" height="11" viewBox="0 0 16 16" fill="none">
        <rect
          x="5"
          y="5"
          width="9"
          height="9"
          rx="1.5"
          stroke="currentColor"
          strokeWidth="1.4"
        />
        <path
          d="M11 5V3.5A1.5 1.5 0 0 0 9.5 2h-6A1.5 1.5 0 0 0 2 3.5v6A1.5 1.5 0 0 0 3.5 11H5"
          stroke="currentColor"
          strokeWidth="1.4"
        />
      </svg>
      {label}
    </button>
  );
};
