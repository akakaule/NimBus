import { useState } from "react";
import { Badge } from "components/ui/badge";
import { EndpointStatusCount, MetadataShort } from "api-client";
import Loading from "components/loading/loading";
import { getEnv } from "hooks/app-status";
import { useTime } from "hooks/use-time";
import EndpointStatusCard from "./endpoint-status-card/endpoint-status-card";

type DashboardProps = {
  title: string;
  cards: EndpointStatusCount[];
  heartbeatDict: MetadataShort[];
};

const Dashboard = (props: DashboardProps) => {
  const loading = !props.cards;
  const time = useTime(1000);
  const [env] = useState(getEnv());

  return (
    <div className="p-20 bg-gray-700 text-gray-100 min-h-full">
      <div className="flex justify-between pb-8">
        <h1 className="text-3xl font-bold flex items-center gap-3">
          {props.title}{" "}
          <Badge variant="info">{env !== undefined ? env : getEnv()}</Badge>
        </h1>
        <h1 className="text-3xl font-bold">
          <span>{time.format("HH:mm:ss")}</span>
        </h1>
      </div>
      {loading ? (
        <div className="flex justify-center items-center mt-[40%]">
          <Loading diameter={100} />
        </div>
      ) : (
        <div className="grid items-center grid-flow-row auto-rows-auto grid-cols-[repeat(auto-fill,minmax(320px,1fr))] gap-6">
          {props.cards?.map((x) => {
            const heartbeatDict = props.heartbeatDict
              ? props.heartbeatDict[
                  props.heartbeatDict.findIndex(
                    (e) => e.endpointId === x.endpointId,
                  )
                ]
              : undefined;
            const heartbeatStatus = heartbeatDict?.heartbeatStatus ?? "Unknown";

            return (
              <EndpointStatusCard
                statusCount={x}
                heartbeatStatus={heartbeatStatus}
                key={x.endpointId}
              />
            );
          })}
        </div>
      )}
    </div>
  );
};

export default Dashboard;
