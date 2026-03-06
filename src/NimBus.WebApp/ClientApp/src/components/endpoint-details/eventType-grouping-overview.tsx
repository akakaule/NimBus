import { useState } from "react";
import { Link } from "react-router-dom";
import { Button } from "components/ui/button";
import * as api from "api-client";

interface IEventTypeGroupingOverviewProps {
  show: boolean;
  namespace: string;
  events: api.EventType[];
  triggerHandler: (event: api.EventType) => void;
}

const EventTypeGroupingOverview = (props: IEventTypeGroupingOverviewProps) => {
  const [show, setShow] = useState(props.show);

  if (!props.events || props.events?.length! < 1)
    return <div id={props.namespace}>"No events"</div>;
  else
    return (
      <div id={props.namespace}>
        <b>{props.namespace}</b>{" "}
        <Button size="xs" colorScheme="blue" onClick={() => setShow(!show)}>
          {show ? "Hide events" : "Show events"}
        </Button>
        <hr className="my-2 border-border" />
        {show && (
          <div className="transition-all">
            {props.events?.map((e) => {
              return (
                <p key={e.id}>
                  <Link
                    to={"/EventTypes/Details/" + e.id}
                    className="text-blue-500 hover:underline"
                  >
                    {e.id}
                  </Link>
                  <Button
                    size="xs"
                    onClick={(ev) => props.triggerHandler(e)}
                    className="ml-2"
                  >
                    Trigger event
                  </Button>
                </p>
              );
            })}
            <hr className="my-2 border-border" />
          </div>
        )}
      </div>
    );
};

export default EventTypeGroupingOverview;
