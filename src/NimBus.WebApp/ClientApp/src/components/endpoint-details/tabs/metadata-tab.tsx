import * as api from "api-client";
import * as React from "react";
import { useParams } from "react-router-dom";
import HeartbeatColumn from "../metadata/heartbeat-column";
import MetadataColumn from "../metadata/metadata-column";

interface IMetadataTabProps {}

const MetadataTab = (props: IMetadataTabProps) => {
  const client = new api.Client(api.CookieAuth());
  const params = useParams();

  const [fetchDone, setFetchDone] = React.useState<boolean>(false);
  const [endpointMetadata, setendpointMetadata] =
    React.useState<api.Metadata>();

  React.useEffect(() => {
    const fetchData = async () => {
      try {
        const res = await client.getMetadataEndpoint(params.id!);
        setendpointMetadata(res);
        setFetchDone(true);
      } catch (error) {
        setFetchDone(true);
      }
    };

    fetchData();
  }, []);

  return (
    <div className="grid grid-cols-2 gap-6 w-full">
      <div className="w-full border border-input rounded">
        {fetchDone && <MetadataColumn metadata={endpointMetadata} />}
      </div>
      <div className="w-full border border-input p-3 rounded">
        {fetchDone && <HeartbeatColumn metadata={endpointMetadata} />}
      </div>
    </div>
  );
};

export default MetadataTab;
