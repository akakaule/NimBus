import * as api from "api-client";
import { Badge } from "components/ui/badge";
import {
  Accordion,
  AccordionItem,
  AccordionTrigger,
  AccordionContent,
} from "components/ui/accordion";
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
          className="mb-2"
        >
          <AccordionTrigger
            itemId={group.namespace}
            className="bg-muted rounded-md data-[expanded]:rounded-b-none"
          >
            <span className="flex-1 text-left font-semibold">
              {group.namespace}
              <Badge
                variant="primary"
                size="sm"
                className="ml-2 bg-purple-100 text-purple-800"
              >
                {group.eventTypes.length}
              </Badge>
            </span>
          </AccordionTrigger>
          <AccordionContent
            itemId={group.namespace}
            className="bg-muted rounded-b-md"
          >
            <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4 gap-4">
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
