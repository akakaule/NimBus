import { Badge } from "components/ui/badge";
import { Tooltip } from "components/ui/tooltip";
import * as api from "api-client";

interface IEventTypePropertiesTableProps {
  properties: api.EventTypeProperty[];
}

const EventTypePropertiesTable: React.FC<IEventTypePropertiesTableProps> = ({
  properties,
}) => {
  // Filter out internal MessageMetadata property
  const displayProperties = properties.filter(
    (p) => p.name !== "MessageMetadata",
  );

  if (displayProperties.length === 0) {
    return (
      <div className="p-4 border rounded-md bg-card text-card-foreground">
        <h3 className="text-sm font-semibold mb-3">Properties</h3>
        <p className="text-muted-foreground text-sm">No properties defined</p>
      </div>
    );
  }

  return (
    <div className="p-4 border rounded-md bg-card text-card-foreground">
      <h3 className="text-sm font-semibold mb-3">Properties</h3>
      <table className="w-full text-sm">
        <thead>
          <tr className="border-b border-border">
            <th className="text-left py-2 pr-4 font-semibold">Name</th>
            <th className="text-left py-2 pr-4 font-semibold">Type</th>
            <th className="text-left py-2 pr-4 font-semibold">Required</th>
            <th className="text-left py-2 font-semibold">Description</th>
          </tr>
        </thead>
        <tbody>
          {displayProperties.map((prop, index) => (
            <tr
              key={prop.name ?? `prop-${index}`}
              className="border-b border-border"
            >
              <td className="py-2 pr-4 font-medium">{prop.name || "-"}</td>
              <td className="py-2 pr-4">
                {prop.typeFullName && prop.typeFullName !== prop.typeName ? (
                  <Tooltip content={prop.typeFullName} position="top">
                    <span className="font-mono text-xs text-purple-600 cursor-help">
                      {prop.typeName}
                    </span>
                  </Tooltip>
                ) : (
                  <span className="font-mono text-xs text-purple-600">
                    {prop.typeName}
                  </span>
                )}
              </td>
              <td className="py-2 pr-4">
                <Badge
                  variant={prop.isRequired ? "error" : "default"}
                  size="sm"
                >
                  {prop.isRequired ? "Yes" : "No"}
                </Badge>
              </td>
              <td className="py-2 text-muted-foreground">
                {prop.description || "-"}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
};

export default EventTypePropertiesTable;
