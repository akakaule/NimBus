import { Link } from "react-router-dom";
import { Badge } from "components/ui/badge";
import { Tooltip } from "components/ui/tooltip";
import * as api from "api-client";

interface IEventTypeCardProps {
  eventType: api.EventType;
  producerCount?: number;
  consumerCount?: number;
}

const EventTypeCard: React.FC<IEventTypeCardProps> = ({
  eventType,
  producerCount = 0,
  consumerCount = 0,
}) => {
  const name = eventType.name || "Unknown";
  const description = eventType.description || "No description available";
  const truncatedDescription =
    description.length > 100
      ? `${description.substring(0, 100)}...`
      : description;

  return (
    <Link to={`/EventTypes/Details/${eventType.id}`} className="no-underline">
      <div className="p-4 border rounded-md border-border bg-card text-card-foreground cursor-pointer min-h-[140px] flex flex-col transition-all duration-200 hover:border-blue-400 hover:shadow-md hover:-translate-y-0.5">
        <div className="flex flex-col items-start gap-2 flex-1">
          <Tooltip content={name} position="top">
            <p className="font-bold text-blue-600 text-base truncate w-full">
              {name}
            </p>
          </Tooltip>
          <p className="text-sm text-muted-foreground line-clamp-2 flex-1">
            {truncatedDescription}
          </p>
        </div>
        <div className="flex gap-2 mt-2">
          <Tooltip content="Producers">
            <Badge variant="success" className="bg-green-100 text-green-800">
              P: {producerCount}
            </Badge>
          </Tooltip>
          <Tooltip content="Consumers">
            <Badge variant="info" className="bg-blue-100 text-blue-800">
              C: {consumerCount}
            </Badge>
          </Tooltip>
        </div>
      </div>
    </Link>
  );
};

export default EventTypeCard;
