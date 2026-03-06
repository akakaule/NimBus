import * as React from "react";
import * as api from "api-client";
import FilterContext from "./filtering-context";
import { Combobox } from "components/ui/combobox";

interface StatusFilteringProps {
  initialValue?: api.ResolutionStatus[];
}

const StatusFiltering = (props: StatusFilteringProps) => {
  const ctx = React.useContext(FilterContext);
  const statusOptions = Object.values(api.ResolutionStatus).map((status) => ({
    value: status,
    label: status,
  }));

  const [selectedStatus, setSelectedStatus] = React.useState<string[]>(
    () => props.initialValue?.map((s) => s.toString()) ?? [],
  );

  // Sync with context when initialValue changes (e.g., on reset)
  React.useEffect(() => {
    if (props.initialValue) {
      const newValue = props.initialValue.map((s) => s.toString());
      setSelectedStatus(newValue);
      const filters = ctx.filterContext;
      filters.resolutionStatus = props.initialValue;
      ctx.setProjectContext(filters);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [props.initialValue]);

  const handleChange = (newValue: string[]) => {
    setSelectedStatus(newValue);
    const filters = ctx.filterContext;
    filters.resolutionStatus = newValue as api.ResolutionStatus[];
    ctx.setProjectContext(filters);
  };

  return (
    <div className="w-full pl-4">
      <Combobox
        options={statusOptions}
        value={selectedStatus}
        onChange={handleChange}
        placeholder="Status"
        label="Status"
        multiple={true}
      />
    </div>
  );
};

export default StatusFiltering;
