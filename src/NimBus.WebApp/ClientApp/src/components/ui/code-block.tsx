import { type ReactNode, useMemo } from "react";
import { cn } from "lib/utils";

export interface CodeBlockProps {
  /** Header title shown on the left. */
  title?: ReactNode;
  /** Optional mime-type or note rendered next to title in muted mono. */
  subtitle?: ReactNode;
  /** Right-aligned action buttons (Copy, Download, Compose, etc.). */
  actions?: ReactNode;
  /** Raw text/JSON content to render in the pre block. */
  children?: string;
  /** Pre-rendered children when you need custom inline elements. */
  customBody?: ReactNode;
  /** Apply JSON syntax highlighting; ignored when customBody is set. */
  highlight?: "json" | "none";
  /** Make GUID-shaped values clickable using this builder. */
  linkifyGuid?: (guid: string) => string | undefined;
  className?: string;
}

// GUID format: 8-4-4-4-12 hex chars.
const GUID_RE = /([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})/;

/**
 * Dark code block with JSON syntax highlighting (design rec §09 "code").
 * Coral keys, green strings, blue numbers — same palette as the design system.
 * GUID-shaped string values can optionally render as links so IDs become
 * first-class navigation bridges between pages.
 */
export const CodeBlock: React.FC<CodeBlockProps> = ({
  title,
  subtitle,
  actions,
  children,
  customBody,
  highlight = "json",
  linkifyGuid,
  className,
}) => {
  const highlighted = useMemo(() => {
    if (customBody || !children) return null;
    if (highlight === "none") {
      return <span className="text-[#E5DFCE]">{children}</span>;
    }
    return tokenizeJson(children, linkifyGuid);
  }, [children, customBody, highlight, linkifyGuid]);

  return (
    <div
      className={cn(
        "bg-card border border-border rounded-nb-md overflow-hidden",
        className,
      )}
    >
      {(title || actions) && (
        <div className="flex items-center justify-between gap-3 px-4 py-3 border-b border-border">
          <div className="flex items-baseline gap-2 min-w-0">
            {title && (
              <span className="font-bold text-[13px] text-foreground truncate">
                {title}
              </span>
            )}
            {subtitle && (
              <span className="font-mono text-[11px] text-muted-foreground">
                {subtitle}
              </span>
            )}
          </div>
          {actions && (
            <div className="flex items-center gap-2 shrink-0">{actions}</div>
          )}
        </div>
      )}
      <pre
        className={cn(
          "m-0 px-5 py-4 bg-[#1A1814] text-[#E5DFCE]",
          "font-mono text-[12.5px] leading-[1.65]",
          "overflow-auto max-h-[420px]",
        )}
      >
        {customBody ?? highlighted}
      </pre>
    </div>
  );
};

/**
 * Minimal JSON tokenizer for syntax highlighting. We do a line-by-line pass
 * with regexes — not a full parser, but it handles the canonical NimBus
 * payload shape (keys, strings, numbers, booleans, null, punctuation) and
 * promotes GUID-shaped string values to links when `linkifyGuid` is provided.
 */
function tokenizeJson(
  src: string,
  linkifyGuid?: (guid: string) => string | undefined,
): ReactNode {
  const lines = src.split("\n");
  return lines.map((line, lineIdx) => {
    const nodes: ReactNode[] = [];
    let rest = line;
    let key = 0;

    while (rest.length > 0) {
      // Key — "key": (preceding whitespace already absorbed by ` rest`).
      const keyMatch = rest.match(/^(\s*)"([^"\\]*(?:\\.[^"\\]*)*)"(\s*:)/);
      if (keyMatch) {
        const [full, lead, name, colon] = keyMatch;
        if (lead) nodes.push(lead);
        nodes.push(
          <span key={key++} className="text-[#E8743C]">
            {`"${name}"`}
          </span>,
        );
        nodes.push(colon);
        rest = rest.slice(full.length);
        continue;
      }

      // String value.
      const strMatch = rest.match(/^(\s*)"([^"\\]*(?:\\.[^"\\]*)*)"/);
      if (strMatch) {
        const [full, lead, content] = strMatch;
        if (lead) nodes.push(lead);
        const guidMatch = content.match(GUID_RE);
        const route = guidMatch && linkifyGuid?.(guidMatch[1]);
        if (route) {
          nodes.push(
            <span key={key++} className="text-[#8FD3A8]">
              {'"'}
            </span>,
            <a
              key={key++}
              href={route}
              className="text-[#8FD3A8] underline decoration-[#8FD3A8]/40 hover:decoration-[#8FD3A8]"
            >
              {content}
            </a>,
            <span key={key++} className="text-[#8FD3A8]">
              {'"'}
            </span>,
          );
        } else {
          nodes.push(
            <span key={key++} className="text-[#8FD3A8]">
              {`"${content}"`}
            </span>,
          );
        }
        rest = rest.slice(full.length);
        continue;
      }

      // Number / boolean / null.
      const litMatch = rest.match(
        /^(\s*)(-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?|true|false|null)/,
      );
      if (litMatch) {
        const [full, lead, lit] = litMatch;
        if (lead) nodes.push(lead);
        nodes.push(
          <span key={key++} className="text-[#BFD8F2]">
            {lit}
          </span>,
        );
        rest = rest.slice(full.length);
        continue;
      }

      // Punctuation / whitespace passthrough.
      const punctMatch = rest.match(/^(\s*)([{}\[\],:])/);
      if (punctMatch) {
        const [full, lead, punct] = punctMatch;
        if (lead) nodes.push(lead);
        nodes.push(
          <span key={key++} className="text-[#8A8473]">
            {punct}
          </span>,
        );
        rest = rest.slice(full.length);
        continue;
      }

      // Fallback — advance one char.
      nodes.push(rest[0]);
      rest = rest.slice(1);
    }

    return (
      <span key={lineIdx}>
        {nodes}
        {lineIdx < lines.length - 1 ? "\n" : ""}
      </span>
    );
  });
}
