import * as React from "react";
import FilterContext from "./filtering-context";
import { Input } from "components/ui/input";

interface SessionFilteringProps {
  /** Initial value to seed the input from (typically URL-derived). Optional. */
  initialValue?: string;
}

const SessionFiltering = (props: SessionFilteringProps) => {
  const ctx = React.useContext(FilterContext);
  const [sessionId, setSessionId] = React.useState<string>(
    () => props.initialValue ?? "",
  );

  React.useEffect(() => {
    const filters = ctx.filterContext;
    filters.sessionId = sessionId || undefined;
    ctx.setProjectContext(filters);
  }, [sessionId]);

  return (
    <div className="w-full pl-4">
      <label className="block text-sm font-medium text-foreground mb-1">
        SessionId
      </label>
      <Input
        type="text"
        placeholder="SessionId"
        value={sessionId}
        onChange={(event: React.ChangeEvent<HTMLInputElement>) => {
          setSessionId(event.target.value);
        }}
      />
    </div>
  );
};

export default SessionFiltering;
