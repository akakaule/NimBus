import * as React from "react";
import * as api from "api-client";
import { Button } from "components/ui/button";
import {
  Accordion,
  AccordionItem,
  AccordionTrigger,
  AccordionContent,
} from "components/ui/accordion";
import EventTypeFiltering from "./eventType-filtering";
import FilterContext from "./filtering-context";
import SessionFiltering from "./session-filtering";
import EnqueuedFiltering from "./enqueued-filtering";
import UpdatedFiltering from "./updated-filtering";
import PayloadFiltering from "./payload-filtering";
import StatusFiltering from "./status-filtering";
import EventIdFiltering from "./eventId-filtering";

// Search icon
const SearchIcon = () => (
  <svg
    className="w-5 h-5"
    fill="none"
    stroke="currentColor"
    viewBox="0 0 24 24"
  >
    <path
      strokeLinecap="round"
      strokeLinejoin="round"
      strokeWidth={2}
      d="M21 21l-6-6m2-5a7 7 0 11-14 0 7 7 0 0114 0z"
    />
  </svg>
);

// Reset icon
const ResetIcon = () => (
  <svg
    className="w-5 h-5"
    fill="none"
    stroke="currentColor"
    viewBox="0 0 24 24"
  >
    <path
      strokeLinecap="round"
      strokeLinejoin="round"
      strokeWidth={2}
      d="M4 4v5h.582m15.356 2A8.001 8.001 0 004.582 9m0 0H9m11 11v-5h-.581m0 0a8.003 8.003 0 01-15.357-2m15.357 2H15"
    />
  </svg>
);

interface EventFilteringProps {
  handleFilterClicked: (eventFilter: api.EventFilter) => void;
  onReset?: () => void;
  onClearStatus?: () => void;
  /** Commit-on-change for status — persists chip add/remove to the URL immediately. */
  onStatusChange?: (next: api.ResolutionStatus[]) => void;
  initialStatuses?: api.ResolutionStatus[];
  initialEventTypes?: string[];
  initialEventId?: string;
  initialSessionId?: string;
  maxResults?: number;
  onMaxResultsChange?: (value: number) => void;
}

const EventFiltering = (props: EventFilteringProps) => {
  const ctx = React.useContext(FilterContext);
  const [statusKey, setStatusKey] = React.useState(0);

  const handleReset = () => {
    // Increment key to force StatusFiltering to re-render with initial values
    setStatusKey((prev) => prev + 1);
    props.onReset?.();
  };

  const handleClearStatus = () => {
    setStatusKey((prev) => prev + 1);
    props.onClearStatus?.();
  };

  return (
    <>
      <div className="grid grid-cols-[repeat(4,1fr)_auto] gap-3">
        <div>
          <EventTypeFiltering initialValue={props.initialEventTypes} />
        </div>
        <div>
          <StatusFiltering
            key={statusKey}
            initialValue={props.initialStatuses}
            onStatusChange={props.onStatusChange}
          />
        </div>
        <div>
          <EventIdFiltering initialValue={props.initialEventId} />
        </div>
        <div>
          <SessionFiltering initialValue={props.initialSessionId} />
        </div>
        <div className="flex items-end justify-end gap-2">
          {props.onMaxResultsChange && (
            <select
              className="h-9 rounded-md border border-input bg-background px-2 text-sm"
              value={props.maxResults ?? 100}
              onChange={(e) =>
                props.onMaxResultsChange?.(Number(e.target.value))
              }
            >
              <option value={50}>50</option>
              <option value={100}>100</option>
              <option value={250}>250</option>
              <option value={500}>500</option>
            </select>
          )}
          {props.onReset && (
            <Button
              variant="outline"
              rightIcon={<ResetIcon />}
              aria-label="reset"
              onClick={handleReset}
            >
              Reset
            </Button>
          )}
          {props.onClearStatus && (
            <Button
              variant="outline"
              aria-label="clear status filter"
              onClick={handleClearStatus}
            >
              All statuses
            </Button>
          )}
          <Button
            variant="outline"
            rightIcon={<SearchIcon />}
            aria-label="search"
            onClick={() => {
              props.handleFilterClicked(ctx.filterContext);
            }}
          >
            Search
          </Button>
        </div>
        <div className="pt-3 col-span-full">
          <Accordion>
            <AccordionItem id="advanced-filters">
              <AccordionTrigger
                itemId="advanced-filters"
                className="bg-muted rounded-md"
              >
                <span className="flex-1 text-center">Advanced Filters</span>
              </AccordionTrigger>
              <AccordionContent
                itemId="advanced-filters"
                className="bg-muted rounded-b-md"
              >
                <div className="grid grid-cols-3 gap-2">
                  <div className="col-span-1">
                    <EnqueuedFiltering />
                  </div>
                  <div className="col-span-1">
                    <UpdatedFiltering />
                  </div>
                  <div className="col-span-1">
                    <PayloadFiltering />
                  </div>
                </div>
              </AccordionContent>
            </AccordionItem>
          </Accordion>
        </div>
      </div>
    </>
  );
};

export default EventFiltering;
