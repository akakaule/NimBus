import * as React from "react";
import FilterContext from "./filtering-context";
import moment from "moment";

type EnqueuedFilteringProps = {};

const EnqueuedFiltering = (props: EnqueuedFilteringProps) => {
  const ctx = React.useContext(FilterContext);
  const [enqueuedTo, setEnqueuedTo] = React.useState<string>("");
  const [enqueuedFrom, setEnqueuedFrom] = React.useState<string>("");

  React.useEffect(() => {
    const filters = ctx.filterContext;
    if (enqueuedTo) {
      filters.enqueuedAtTo = moment(enqueuedTo, "YYYY-MM-DD[T]HH:mm:ss");
    }
    if (enqueuedFrom) {
      filters.enqueuedAtFrom = moment(enqueuedFrom, "YYYY-MM-DD[T]HH:mm:ss");
    }
    ctx.setProjectContext(filters);
  }, [enqueuedTo, enqueuedFrom]);

  return (
    <form className="flex flex-wrap gap-2" noValidate>
      <div className="w-48">
        <label className="block text-sm font-medium text-foreground mb-1">
          Updated From
        </label>
        <input
          type="datetime-local"
          className="w-full px-3 py-2 border border-input rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-primary focus:border-primary"
          onChange={(event: React.ChangeEvent<HTMLInputElement>) => {
            setEnqueuedFrom(event.target.value);
          }}
        />
      </div>
      <div className="w-48">
        <label className="block text-sm font-medium text-foreground mb-1">
          Updated To
        </label>
        <input
          type="datetime-local"
          className="w-full px-3 py-2 border border-input rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-primary focus:border-primary"
          onChange={(event: React.ChangeEvent<HTMLInputElement>) => {
            setEnqueuedTo(event.target.value);
          }}
        />
      </div>
    </form>
  );
};

export default EnqueuedFiltering;
