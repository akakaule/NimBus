import * as React from "react";
const { useEffect, useState } = React;

import * as api from "api-client";
import { useParams } from "react-router-dom";
import Page from "components/page";
import EventsPanel from "components/endpoint-details/events-panel";
import NotFoundPage from "components/not-found-page";

export interface ComposeNewResponse {
  hasError: boolean;
  responseString: string;
}

type EndpointDetailsProps = {
  endpointState?: api.EndpointStatus;
};

const EndpointDetails = (props: EndpointDetailsProps) => {
  const client = new api.Client(api.CookieAuth());
  const params = useParams();

  const [endpointIsInvalid, setEndpointIsInvalid] = useState<boolean>(false);

  // Validate endpoint exists
  useEffect(() => {
    const fetchData = async () => {
      try {
        if (!props.endpointState) {
          await client.getApiEndpointstatusStatusEndpointName(params.id!);
        }
      } catch (e) {
        console.log("Failed to load endpoint details");
        if (e instanceof api.SwaggerException) {
          if (e.status === 404) {
            setEndpointIsInvalid(true);
          }
        }
      }
    };

    fetchData();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  return (
    <>
      {!endpointIsInvalid ? (
        <Page title={`${params.id} details`}>
          <EventsPanel endpointId={params.id!} />
        </Page>
      ) : (
        <NotFoundPage
          errMsg={"No endpoint found with name: " + params.id!}
        ></NotFoundPage>
      )}
    </>
  );
};

export default EndpointDetails;
