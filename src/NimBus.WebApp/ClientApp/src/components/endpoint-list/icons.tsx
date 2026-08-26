import type { ReactNode, SVGProps } from "react";

// Hand-drawn stroke glyphs for the Endpoints list — the status column and the
// row-actions menu. NimBus ships no icon library on purpose; these follow the
// same 24x24 / 2px-stroke geometry as the rest of the app's inline SVGs so
// they sit consistently beside them.
type IconProps = SVGProps<SVGSVGElement> & { className?: string };

const Glyph = ({
  children,
  className = "w-5 h-5",
  ...rest
}: IconProps & { children: ReactNode }) => (
  <svg
    viewBox="0 0 24 24"
    fill="none"
    stroke="currentColor"
    strokeWidth={2}
    strokeLinecap="round"
    strokeLinejoin="round"
    className={className}
    focusable="false"
    {...rest}
  >
    {children}
  </svg>
);

/* ---------- status glyphs ---------- */

export const CheckCircleIcon = (props: IconProps) => (
  <Glyph {...props}>
    <circle cx="12" cy="12" r="10" />
    <path d="m9 12 2 2 4-4" />
  </Glyph>
);

export const MinusCircleIcon = (props: IconProps) => (
  <Glyph {...props}>
    <circle cx="12" cy="12" r="10" />
    <path d="M8 12h8" />
  </Glyph>
);

export const XCircleIcon = (props: IconProps) => (
  <Glyph {...props}>
    <circle cx="12" cy="12" r="10" />
    <path d="m15 9-6 6" />
    <path d="m9 9 6 6" />
  </Glyph>
);

export const ClockIcon = (props: IconProps) => (
  <Glyph {...props}>
    <circle cx="12" cy="12" r="10" />
    <path d="M12 6v6l4 2" />
  </Glyph>
);

/** Disabled — a struck-through bolt. */
export const ZapOffIcon = (props: IconProps) => (
  <Glyph {...props}>
    <path d="M12.41 6.75 13 2l-2.43 2.92" />
    <path d="M18.57 12.91 21 10h-5.34" />
    <path d="M8 8 3 14h9l-1 8 5-6" />
    <path d="M2 2l20 20" />
  </Glyph>
);

/** Missing subscription — an unplugged connector. */
export const UnplugIcon = (props: IconProps) => (
  <Glyph {...props}>
    <path d="m19 5 3-3" />
    <path d="m2 22 3-3" />
    <path d="M6.3 20.3a2.4 2.4 0 0 0 3.4 0L12 18l-6-6-2.3 2.3a2.4 2.4 0 0 0 0 3.4Z" />
    <path d="M7.5 13.5 10 11" />
    <path d="M10.5 16.5 13 14" />
    <path d="m12 6 6 6 2.3-2.3a2.4 2.4 0 0 0 0-3.4l-2.6-2.6a2.4 2.4 0 0 0-3.4 0Z" />
  </Glyph>
);

/** Storage unavailable — a database that can't be reached. */
export const DatabaseZapIcon = (props: IconProps) => (
  <Glyph {...props}>
    <ellipse cx="12" cy="5" rx="9" ry="3" />
    <path d="M3 5v14a9 3 0 0 0 12 2.84" />
    <path d="M21 5v3" />
    <path d="M21 12 18 17h4l-3 5" />
    <path d="M3 12a9 3 0 0 0 11.59 2.87" />
  </Glyph>
);

/* ---------- action glyphs ---------- */

export const BellIcon = (props: IconProps) => (
  <Glyph {...props}>
    <path d="M6 8a6 6 0 0 1 12 0c0 7 3 9 3 9H3s3-2 3-9" />
    <path d="M10.3 21a1.94 1.94 0 0 0 3.4 0" />
  </Glyph>
);

export const MoreHorizontalIcon = (props: IconProps) => (
  <Glyph {...props}>
    <circle cx="12" cy="12" r="1" fill="currentColor" />
    <circle cx="19" cy="12" r="1" fill="currentColor" />
    <circle cx="5" cy="12" r="1" fill="currentColor" />
  </Glyph>
);

export const PowerIcon = (props: IconProps) => (
  <Glyph {...props}>
    <path d="M12 2v10" />
    <path d="M18.4 6.6a9 9 0 1 1-12.77.04" />
  </Glyph>
);

export const ShieldCheckIcon = (props: IconProps) => (
  <Glyph {...props}>
    <path d="M20 13c0 5-3.5 7.5-7.66 8.95a1 1 0 0 1-.67-.01C7.5 20.5 4 18 4 13V6a1 1 0 0 1 1-1c2 0 4.5-1.2 6.24-2.72a1.17 1.17 0 0 1 1.52 0C14.51 3.81 17 5 19 5a1 1 0 0 1 1 1z" />
    <path d="m9 12 2 2 4-4" />
  </Glyph>
);

export const TrashIcon = (props: IconProps) => (
  <Glyph {...props}>
    <path d="M3 6h18" />
    <path d="M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6" />
    <path d="M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2" />
    <path d="M10 11v6" />
    <path d="M14 11v6" />
  </Glyph>
);
