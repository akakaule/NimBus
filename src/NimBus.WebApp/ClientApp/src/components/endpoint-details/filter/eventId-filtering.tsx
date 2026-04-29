import * as React from "react";
import FilterContext from "./filtering-context";
import { Input } from "components/ui/input";

interface EventIdFilteringProps {
  /** Initial value to seed the input from (typically URL-derived). Optional. */
  initialValue?: string;
}

const EventIdFiltering = (props: EventIdFilteringProps) => {
  const ctx = React.useContext(FilterContext);
  const [eventId, setEventId] = React.useState<string>(
    () => props.initialValue ?? "",
  );

  React.useEffect(() => {
    const filters = ctx.filterContext;
    filters.eventId = eventId || undefined;
    ctx.setProjectContext(filters);
  }, [eventId]);

  return (
    <div className="w-full pl-4">
      <label className="block text-sm font-medium text-foreground mb-1">
        EventId
      </label>
      <Input
        type="text"
        placeholder="EventId"
        value={eventId}
        onChange={(event: React.ChangeEvent<HTMLInputElement>) => {
          setEventId(event.target.value);
        }}
      />
    </div>
  );
};

export default EventIdFiltering;
