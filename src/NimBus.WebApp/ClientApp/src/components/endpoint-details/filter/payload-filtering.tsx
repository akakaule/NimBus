import * as React from "react";
import FilterContext from "./filtering-context";
import { Textarea } from "components/ui/textarea";

type PayloadFilteringProps = {};

const PayloadFiltering = (props: PayloadFilteringProps) => {
  const ctx = React.useContext(FilterContext);
  const [payload, setPayload] = React.useState<string>("");

  React.useEffect(() => {
    const filters = ctx.filterContext;
    filters.payload = payload || undefined;
    ctx.setProjectContext(filters);
  }, [payload]);

  return (
    <div className="w-full pl-4">
      <label className="block text-sm font-medium text-foreground mb-1">
        Payload
      </label>
      <Textarea
        placeholder="Payload"
        value={payload}
        onChange={(event: React.ChangeEvent<HTMLTextAreaElement>) => {
          setPayload(event.target.value);
        }}
        className="min-h-[76px]"
      />
    </div>
  );
};

export default PayloadFiltering;
