import { useEffect, useRef, useState } from "react";
import * as api from "api-client";
import {
  Button,
  Input,
  Modal,
  ModalBody,
  ModalFooter,
  ModalHeader,
  Radio,
  RadioGroup,
  useToast,
} from "components/ui";
import { Combobox, type ComboboxOption } from "components/ui/combobox";
import { getEventTypesByEndpoint } from "hooks/event-types";

interface IEndpointAlertsModalProps {
  endpointId: string;
  isOpen: boolean;
  onClose: () => void;
}

const EMAIL_PATTERN = /^[^\s@]+@[^\s@]+\.[^\s@]{2,}$/;

const isHttpUrl = (value: string): boolean => {
  try {
    const url = new URL(value);
    return url.protocol === "http:" || url.protocol === "https:";
  } catch {
    return false;
  }
};

// Create an alert subscription on an endpoint. NimBus subscriptions carry a
// mail address OR a webhook url — there are no managed destinations like
// upstream DIS, so the type radio picks between those two. The author is
// recorded server-side from the authenticated principal; POST requires Owner
// on the endpoint, which is why the caller only offers this to managers.
export default function EndpointAlertsModal(props: IEndpointAlertsModalProps) {
  const { addToast } = useToast();
  const [client] = useState(() => new api.Client(api.CookieAuth()));

  const [notificationType, setNotificationType] = useState("mail");
  const [mail, setMail] = useState("");
  const [url, setUrl] = useState("");
  const [frequency, setFrequency] = useState("86400");
  const [eventTypes, setEventTypes] = useState<ComboboxOption[]>([]);
  const [selectedEventTypes, setSelectedEventTypes] = useState<string[]>([]);
  const [saving, setSaving] = useState(false);
  const eventTypesLoaded = useRef(false);

  useEffect(() => {
    if (!props.isOpen || eventTypesLoaded.current) return;
    eventTypesLoaded.current = true;

    const load = async () => {
      const result = await getEventTypesByEndpoint(props.endpointId);
      const names = (groups: typeof result.consumes) =>
        groups
          ?.map((group) => group.events ?? [])
          .reduce((all, events) => all.concat(events), [])
          .map((event) => event.name)
          .filter((name): name is string => Boolean(name)) ?? [];

      setEventTypes(
        [...names(result.consumes), ...names(result.produces)].map((name) => ({
          label: name,
          value: name,
        })),
      );
    };

    load().catch(() => {
      // Event-type filtering is optional refinement — a failed catalog lookup
      // must not block subscribing to everything on the endpoint.
      setEventTypes([]);
    });
  }, [props.isOpen, props.endpointId]);

  const recipient = notificationType === "mail" ? mail : url;
  const recipientValid =
    notificationType === "mail"
      ? EMAIL_PATTERN.test(mail.trim())
      : isHttpUrl(url.trim());

  const subscribe = () => {
    setSaving(true);
    const body = new api.EndpointSubscription({
      type: notificationType,
      mail: notificationType === "mail" ? mail.trim() : undefined,
      url: notificationType === "webhook" ? url.trim() : undefined,
      eventTypes: selectedEventTypes,
      frequency: parseInt(frequency, 10),
    });

    client
      .postEndpointSubscribe(props.endpointId, body)
      .then(() => {
        addToast({
          variant: "success",
          title: `Subscribed to alerts on ${props.endpointId}.`,
          description: recipient.trim(),
          duration: 4000,
        });
        setMail("");
        setUrl("");
        setSelectedEventTypes([]);
        props.onClose();
      })
      .catch((error: unknown) => {
        addToast({
          variant: "error",
          title: `Could not subscribe to alerts on ${props.endpointId}.`,
          description:
            error instanceof Error ? error.message : "The request was rejected.",
          duration: 6000,
        });
      })
      .finally(() => setSaving(false));
  };

  return (
    <Modal isOpen={props.isOpen} onClose={props.onClose} size="lg">
      <ModalHeader onClose={props.onClose}>
        Subscribe to alerts from {props.endpointId}
      </ModalHeader>
      <ModalBody>
        <p className="text-sm text-muted-foreground m-0">
          Get notified when {props.endpointId} is affected. Alerts go to an email
          address or a webhook, no more often than the frequency you pick.
        </p>

        <p className="font-bold text-sm mt-4 mb-2">Type</p>
        <RadioGroup
          name="alert-type"
          value={notificationType}
          onChange={setNotificationType}
        >
          <Radio value="mail">Mail</Radio>
          <Radio value="webhook">Webhook</Radio>
        </RadioGroup>

        {notificationType === "mail" ? (
          <div className="mt-4">
            <p className="font-bold text-sm mb-1">Email</p>
            <Input
              type="email"
              autoComplete="off"
              placeholder="example@email.com"
              value={mail}
              error={mail.length > 0 && !recipientValid}
              onChange={(e) => setMail(e.currentTarget.value)}
            />
          </div>
        ) : (
          <div className="mt-4">
            <p className="font-bold text-sm mb-1">Webhook</p>
            <Input
              type="url"
              autoComplete="off"
              placeholder="https://example.com/hooks/nimbus"
              value={url}
              error={url.length > 0 && !recipientValid}
              onChange={(e) => setUrl(e.currentTarget.value)}
            />
          </div>
        )}

        <p className="font-bold text-sm mt-4 mb-2">Frequency</p>
        <RadioGroup
          name="alert-frequency"
          value={frequency}
          onChange={setFrequency}
        >
          <Radio value="3600">Hourly</Radio>
          <Radio value="86400">Daily</Radio>
          <Radio value="604800">Weekly</Radio>
        </RadioGroup>

        <p className="font-bold text-sm mt-4 mb-2">Event filtering</p>
        <Combobox
          options={eventTypes}
          value={selectedEventTypes}
          onChange={setSelectedEventTypes}
          placeholder="Type an event"
        />
        <p className="mt-1 text-xs text-muted-foreground">
          Leave empty to be alerted about every event type on this endpoint.
        </p>
      </ModalBody>
      <ModalFooter>
        <Button variant="ghost" colorScheme="gray" onClick={props.onClose}>
          Cancel
        </Button>
        <Button
          colorScheme="primary"
          disabled={!recipientValid || saving}
          onClick={subscribe}
        >
          Subscribe
        </Button>
      </ModalFooter>
    </Modal>
  );
}
