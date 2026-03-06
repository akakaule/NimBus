import { createContext } from "react";
import { EventFilter } from "api-client";

export interface IFilterContext {
  filterContext: EventFilter;
  setProjectContext: (eventFilter: EventFilter) => void;
}

const defaults: EventFilter = new EventFilter();

const FilterContext = createContext({} as unknown as IFilterContext);

export default FilterContext;
