import * as React from "react";
import FilterContext from "./filtering-context";
import { useParams } from "react-router-dom";
import { Combobox } from "components/ui/combobox";
import { getEventTypesByEndpoint } from "hooks/event-types";

interface EventTypeFilteringProps {
  /** Initial selection to seed the multi-select from (typically URL-derived). Optional. */
  initialValue?: string[];
}

const EventTypeFiltering = (props: EventTypeFilteringProps) => {
  const params = useParams();

  const ctx = React.useContext(FilterContext);
  const [eventTypes, setEventTypes] = React.useState<string[]>([]);
  const [selectedEventTypes, setSelectedEventTypes] = React.useState<string[]>(
    () => props.initialValue ?? [],
  );

  React.useEffect(() => {
    const fetchData = async () => {
      const result = await getEventTypesByEndpoint(params.id!);

      const consumes =
        result.consumes
          ?.map((event) => event.events ?? [])
          .reduce((pre, cur) => pre.concat(cur), [])
          .map((event) => event.name)
          .filter((name): name is string => Boolean(name)) ?? [];

      const produces =
        result.produces
          ?.map((event) => event.events ?? [])
          .reduce((pre, cur) => pre.concat(cur), [])
          .map((event) => event.name)
          .filter((name): name is string => Boolean(name)) ?? [];

      setEventTypes([...consumes, ...produces]);
    };
    fetchData();
  }, []);

  const handleChange = (newValue: string[]) => {
    setSelectedEventTypes(newValue);
    const filters = ctx.filterContext;
    filters.eventTypeId = [...newValue];
    ctx.setProjectContext(filters);
  };

  const eventTypeOptions = eventTypes.map((et) => ({
    value: et,
    label: et,
  }));

  return (
    <div className="w-full pl-4">
      <Combobox
        options={eventTypeOptions}
        value={selectedEventTypes}
        onChange={handleChange}
        placeholder="EventTypes"
        label="EventTypes"
        multiple={true}
      />
    </div>
  );
};

export default EventTypeFiltering;
