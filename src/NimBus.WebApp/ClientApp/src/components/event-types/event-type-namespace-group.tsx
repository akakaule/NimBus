import * as api from "api-client";
import {
  Accordion,
  AccordionItem,
  AccordionTrigger,
  AccordionContent,
} from "components/ui/accordion";
import { NamespacePill } from "components/ui/namespace-pill";
import { cn } from "lib/utils";
import EventTypeCard from "./event-type-card";

interface EventTypeWithCounts {
  eventType: api.EventType;
  producerCount: number;
  consumerCount: number;
}

interface INamespaceGroup {
  namespace: string;
  eventTypes: EventTypeWithCounts[];
}

interface IEventTypeNamespaceGroupProps {
  groups: INamespaceGroup[];
  defaultExpandedNamespaces?: string[];
}

/**
 * Renders event-type cards grouped by namespace. Each group sits inside a
 * surface-2 panel with a NamespacePill heading + count chip — design system §08.
 */
const EventTypeNamespaceGroup: React.FC<IEventTypeNamespaceGroupProps> = ({
  groups,
  defaultExpandedNamespaces,
}) => {
  const defaultExpanded = defaultExpandedNamespaces
    ? defaultExpandedNamespaces
    : groups.length > 0
      ? [groups[0].namespace]
      : [];

  return (
    <Accordion allowMultiple defaultExpandedItems={defaultExpanded}>
      {groups.map((group) => (
        <AccordionItem
          key={group.namespace}
          id={group.namespace}
          className="mb-3"
        >
          <AccordionTrigger
            itemId={group.namespace}
            className={cn(
              "bg-muted rounded-nb-md data-[expanded]:rounded-b-none",
              "px-4 py-3 hover:bg-muted",
            )}
          >
            <span className="flex-1 text-left flex items-center gap-2.5">
              <NamespacePill>{group.namespace}</NamespacePill>
              <span
                className={cn(
                  "inline-flex items-center justify-center font-mono",
                  "text-[11px] font-bold px-2 py-0.5 rounded-full",
                  "bg-nimbus-purple-50 text-nimbus-purple",
                  "dark:bg-purple-950/40 dark:text-purple-300",
                )}
              >
                {group.eventTypes.length}
              </span>
            </span>
          </AccordionTrigger>
          <AccordionContent
            itemId={group.namespace}
            className="bg-muted rounded-b-nb-md px-4 pb-4 pt-1"
          >
            <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4 gap-3">
              {group.eventTypes.map((item) => (
                <EventTypeCard
                  key={item.eventType.id}
                  eventType={item.eventType}
                  producerCount={item.producerCount}
                  consumerCount={item.consumerCount}
                />
              ))}
            </div>
          </AccordionContent>
        </AccordionItem>
      ))}
    </Accordion>
  );
};

export default EventTypeNamespaceGroup;
export type { INamespaceGroup, EventTypeWithCounts };
