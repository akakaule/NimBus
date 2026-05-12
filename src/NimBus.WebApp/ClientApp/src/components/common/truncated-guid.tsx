import { Tooltip } from "components/ui/tooltip";
import { useToast } from "components/ui/toast";
import { cn } from "lib/utils";

interface ITruncatedGuidProps {
  guid: string | undefined | null;
  displayLength?: number;
  /** When false, the trailing copy affordance won't render. Default: true. */
  withCopy?: boolean;
  className?: string;
}

/**
 * Renders a truncated machine ID as monospace + tabular-nums (design rec §07).
 * The copy icon fades in on row hover via the parent's `.group` class so
 * resting tables stay quiet — but click anywhere on the value still copies.
 */
export default function TruncatedGuid({
  guid,
  displayLength = 8,
  withCopy = true,
  className,
}: ITruncatedGuidProps) {
  const { addToast } = useToast();

  if (!guid) {
    return <span className="text-muted-foreground">—</span>;
  }

  const truncated =
    guid.length > displayLength
      ? `${guid.substring(0, displayLength)}…`
      : guid;

  const handleClick = async (e: React.MouseEvent) => {
    e.preventDefault();
    e.stopPropagation();

    try {
      await navigator.clipboard.writeText(guid);
      addToast({
        title: "Copied to clipboard",
        description: guid,
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
    <Tooltip content={guid} position="top">
      <span
        onClick={handleClick}
        className={cn(
          "inline-flex items-center gap-1 cursor-pointer font-mono text-[12px] tabular-nums",
          "text-foreground hover:text-primary",
          className,
        )}
      >
        {truncated}
        {withCopy && (
          <svg
            aria-hidden="true"
            width="11"
            height="11"
            viewBox="0 0 16 16"
            fill="none"
            className="opacity-0 group-hover:opacity-60 hover:!opacity-100 transition-opacity shrink-0"
          >
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
              d="M3 11V3.5C3 2.7 3.7 2 4.5 2H11"
              stroke="currentColor"
              strokeWidth="1.4"
              strokeLinecap="round"
            />
          </svg>
        )}
      </span>
    </Tooltip>
  );
}
