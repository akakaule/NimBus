import { useState, type FormEvent } from "react";
import { cn } from "lib/utils";

export interface RoleCardProps {
  title: string;
  description: string;
  entries: string[];
  /** Accent class for the top rail (e.g. "bg-status-info"). */
  tone: string;
  busy?: boolean;
  onAdd: (entry: string) => void;
  onRemove: (entry: string) => void;
}

// One role list: rail-accented card with the granted entries and an add form.
// Entries are opaque — an email address or an Entra object id — validated
// server-side; the input only requires a non-empty value.
export function RoleCard({
  title,
  description,
  entries,
  tone,
  busy,
  onAdd,
  onRemove,
}: RoleCardProps) {
  const [value, setValue] = useState("");

  const submit = (e: FormEvent) => {
    e.preventDefault();
    const entry = value.trim();
    if (!entry) return;
    onAdd(entry);
    setValue("");
  };

  return (
    <div className="flex bg-card border border-border rounded-nb-md overflow-hidden">
      <div className={cn("w-1 shrink-0", tone)} aria-hidden="true" />
      <div className="flex-1 min-w-0 p-4">
        <div className="flex items-center gap-2">
          <span className="text-sm font-bold">{title}</span>
          <span className="font-mono text-[11px] text-muted-foreground">
            {entries.length} {entries.length === 1 ? "entry" : "entries"}
          </span>
        </div>
        <p className="text-[12px] text-muted-foreground mt-0.5 mb-3">
          {description}
        </p>

        <ul className="m-0 p-0 list-none space-y-1">
          {entries.length === 0 && (
            <li className="text-[12px] text-muted-foreground italic">
              No entries
            </li>
          )}
          {entries.map((entry) => (
            <li
              key={entry}
              className="flex items-center justify-between gap-2 text-[13px] font-mono bg-muted/40 rounded px-2 py-1"
            >
              <span className="truncate" title={entry}>
                {entry}
              </span>
              <button
                type="button"
                disabled={busy}
                onClick={() => onRemove(entry)}
                className="text-status-danger text-[12px] font-sans font-medium hover:underline disabled:opacity-50"
                aria-label={`Remove ${entry} from ${title}`}
              >
                Remove
              </button>
            </li>
          ))}
        </ul>

        <form onSubmit={submit} className="flex gap-2 mt-3">
          <input
            value={value}
            onChange={(e) => setValue(e.target.value)}
            placeholder="email or object id"
            disabled={busy}
            className="flex-1 min-w-0 text-[13px] font-mono border border-border rounded-nb-sm px-2 py-1.5 bg-background disabled:opacity-50"
            aria-label={`Add entry to ${title}`}
          />
          <button
            type="submit"
            disabled={busy || !value.trim()}
            className="text-[13px] font-medium px-3 py-1.5 rounded-nb-sm bg-primary text-white hover:opacity-90 disabled:opacity-50"
          >
            Add
          </button>
        </form>
      </div>
    </div>
  );
}
