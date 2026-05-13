import { Link } from "react-router-dom";
import { Tooltip } from "components/ui/tooltip";
import { cn } from "lib/utils";
import * as api from "api-client";

interface IEventTypeCardProps {
  eventType: api.EventType;
  producerCount?: number;
  consumerCount?: number;
  /** Optional "Last published" timestamp display string (e.g. "4 s ago"). */
  lastPublishedDisplay?: string;
}

/**
 * Event-type catalog card. Design rec §09: make the grid scan like an API
 * catalog — surface a producer → consumer flow chip in place of opaque P:N/C:N
 * counts, so the operator's actual question ("is this contract alive and who
 * owns it?") is answered pre-attentively.
 *
 * The current `EventType` API exposes only producer/consumer *counts*, so the
 * flow chip uses pluralised count labels rather than real endpoint names —
 * upgrading to real names requires a backend change and can land later
 * without touching this component's contract.
 */
const EventTypeCard: React.FC<IEventTypeCardProps> = ({
  eventType,
  producerCount = 0,
  consumerCount = 0,
  lastPublishedDisplay,
}) => {
  const name = eventType.name || "Unknown";
  const description = eventType.description || "No description available";
  const pluralize = (n: number, w: string) => `${n} ${w}${n === 1 ? "" : "s"}`;

  return (
    <Link
      to={`/EventTypes/Details/${eventType.id}`}
      className={cn(
        "no-underline group block",
        "bg-card border border-border rounded-nb-md",
        "px-4 py-3.5 min-h-[140px] flex flex-col gap-2",
        "transition-colors duration-100",
        "hover:border-primary hover:shadow-nb-sm",
      )}
    >
      <Tooltip content={name} position="top">
        <h4
          className={cn(
            "m-0 font-bold text-[14px] truncate",
            "text-status-info dark:text-blue-300",
          )}
        >
          {name}
        </h4>
      </Tooltip>
      <p
        className={cn(
          "m-0 text-[12.5px] text-muted-foreground leading-snug",
          "line-clamp-2 flex-1",
        )}
      >
        {description}
      </p>
      <div className="flex items-center gap-2 mt-auto pt-1 flex-wrap font-mono text-[10.5px]">
        <span
          className={cn(
            "inline-flex items-center gap-1.5",
            "bg-background border border-border rounded-nb-sm px-2 py-0.5",
          )}
        >
          <span className="text-status-success font-semibold">
            {pluralize(producerCount, "producer")}
          </span>
          <span className="text-muted-foreground">→</span>
          <span className="text-status-info font-semibold">
            {pluralize(consumerCount, "consumer")}
          </span>
        </span>
        {lastPublishedDisplay && (
          <span className="ml-auto italic text-muted-foreground">
            {lastPublishedDisplay}
          </span>
        )}
      </div>
    </Link>
  );
};

export default EventTypeCard;
