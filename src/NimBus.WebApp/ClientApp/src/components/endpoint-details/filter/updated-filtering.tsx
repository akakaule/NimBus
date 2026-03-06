import * as React from "react";
import FilterContext from "./filtering-context";
import moment from "moment";

type UpdatedFilteringProps = {};

const UpdatedFiltering = (props: UpdatedFilteringProps) => {
  const ctx = React.useContext(FilterContext);
  const [updatedTo, setUpdatedTo] = React.useState<string>("");
  const [updatedFrom, setUpdatedFrom] = React.useState<string>("");

  React.useEffect(() => {
    const filters = ctx.filterContext;
    if (updatedTo) {
      filters.updatedAtTo = moment(updatedTo, "YYYY-MM-DD[T]HH:mm:ss");
    }
    if (updatedFrom) {
      filters.updateAtFrom = moment(updatedFrom, "YYYY-MM-DD[T]HH:mm:ss");
    }
    ctx.setProjectContext(filters);
  }, [updatedTo, updatedFrom]);

  return (
    <form className="flex flex-wrap gap-2" noValidate>
      <div className="w-48">
        <label className="block text-sm font-medium text-foreground mb-1">
          Added From
        </label>
        <input
          type="datetime-local"
          className="w-full px-3 py-2 border border-input rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-primary focus:border-primary"
          onChange={(event: React.ChangeEvent<HTMLInputElement>) => {
            setUpdatedFrom(event.target.value);
          }}
        />
      </div>
      <div className="w-48">
        <label className="block text-sm font-medium text-foreground mb-1">
          Added To
        </label>
        <input
          type="datetime-local"
          className="w-full px-3 py-2 border border-input rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-primary focus:border-primary"
          onChange={(event: React.ChangeEvent<HTMLInputElement>) => {
            setUpdatedTo(event.target.value);
          }}
        />
      </div>
    </form>
  );
};

export default UpdatedFiltering;
