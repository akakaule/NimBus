import { Link } from "react-router-dom";
import { Badge } from "components/ui/badge";

interface IEventTypeEndpointsListProps {
  title: string;
  endpoints: string[];
  colorScheme: "green" | "blue";
}

const EventTypeEndpointsList: React.FC<IEventTypeEndpointsListProps> = ({
  title,
  endpoints,
  colorScheme,
}) => {
  const borderColor =
    colorScheme === "green" ? "border-l-green-500" : "border-l-blue-500";

  return (
    <div
      className={`p-4 border border-l-4 ${borderColor} rounded-md bg-card text-card-foreground min-h-[120px]`}
    >
      <div className="flex items-center gap-2 mb-3">
        <h3 className="text-sm font-semibold">{title}</h3>
        <Badge variant={colorScheme === "green" ? "success" : "info"} size="sm">
          {endpoints.length}
        </Badge>
      </div>
      {endpoints.length === 0 ? (
        <p className="text-muted-foreground text-sm">
          No {title.toLowerCase()} configured
        </p>
      ) : (
        <div className="flex flex-col items-start gap-1">
          {endpoints.map((endpoint) => (
            <Link
              key={endpoint}
              to={`/Endpoints/Details/${endpoint}`}
              className="text-blue-600 text-sm hover:underline"
            >
              {endpoint}
            </Link>
          ))}
        </div>
      )}
    </div>
  );
};

export default EventTypeEndpointsList;
