import { useState } from "react";
import { Button } from "components/ui/button";
import { Textarea } from "components/ui/textarea";
import {
  Modal,
  ModalHeader,
  ModalBody,
  ModalFooter,
} from "components/ui/modal";
import { useToast } from "components/ui/toast";
import * as api from "api-client";

// Copy icon
const CopyIcon = () => (
  <svg
    className="w-4 h-4"
    fill="none"
    stroke="currentColor"
    viewBox="0 0 24 24"
  >
    <path
      strokeLinecap="round"
      strokeLinejoin="round"
      strokeWidth={2}
      d="M8 16H6a2 2 0 01-2-2V6a2 2 0 012-2h8a2 2 0 012 2v2m-6 12h8a2 2 0 002-2v-8a2 2 0 00-2-2h-8a2 2 0 00-2 2v8a2 2 0 002 2z"
    />
  </svg>
);

// Edit icon
const EditIcon = () => (
  <svg
    className="w-4 h-4"
    fill="none"
    stroke="currentColor"
    viewBox="0 0 24 24"
  >
    <path
      strokeLinecap="round"
      strokeLinejoin="round"
      strokeWidth={2}
      d="M11 5H6a2 2 0 00-2 2v11a2 2 0 002 2h11a2 2 0 002-2v-5m-1.414-9.414a2 2 0 112.828 2.828L11.828 15H9v-2.828l8.586-8.586z"
    />
  </svg>
);

interface IEventTypeExamplePayloadProps {
  eventType: api.EventType;
}

const EventTypeExamplePayload: React.FC<IEventTypeExamplePayloadProps> = ({
  eventType,
}) => {
  const [isOpen, setIsOpen] = useState(false);
  const { addToast } = useToast();

  // Generate example JSON from properties
  const generateExampleJson = (): string => {
    const obj: Record<string, string> = {};
    eventType.properties?.forEach((prop) => {
      if (prop.name && prop.name !== "MessageMetadata") {
        obj[prop.name] = prop.typeName || "unknown";
      }
    });
    return JSON.stringify(obj, null, 2);
  };

  const exampleJson = generateExampleJson();
  const [editedJson, setEditedJson] = useState(exampleJson);
  const [responseMessage, setResponseMessage] = useState<{
    hasError: boolean;
    text: string;
  }>({ hasError: false, text: "" });

  const handleCopy = async () => {
    try {
      await navigator.clipboard.writeText(exampleJson);
      addToast({
        title: "Copied to clipboard",
        variant: "success",
        duration: 2000,
      });
    } catch {
      addToast({
        title: "Failed to copy",
        variant: "error",
        duration: 2000,
      });
    }
  };

  const handleComposeOpen = () => {
    setEditedJson(exampleJson);
    setResponseMessage({ hasError: false, text: "" });
    setIsOpen(true);
  };

  const handleSendEvent = async () => {
    const client = new api.Client(api.CookieAuth());
    const body = new api.ResubmitWithChanges();
    body.eventTypeId = eventType.id;
    body.eventContent = editedJson;

    try {
      await client.postComposeNewEvent(body);
      setResponseMessage({
        hasError: false,
        text: "Event composed successfully.",
      });
      setTimeout(() => {
        setResponseMessage({ hasError: false, text: "" });
      }, 4000);
    } catch (err: unknown) {
      const errorMessage = err instanceof Error ? err.message : String(err);
      setResponseMessage({
        hasError: true,
        text: `Failed to compose event: ${errorMessage}`,
      });
      setTimeout(() => {
        setResponseMessage({ hasError: false, text: "" });
      }, 4000);
    }
  };

  return (
    <>
      <div className="p-4 border rounded-md bg-card text-card-foreground">
        <div className="flex justify-between items-center mb-3">
          <h3 className="text-sm font-semibold">Example Payload</h3>
          <div className="flex gap-2">
            <Button
              size="sm"
              variant="outline"
              onClick={handleCopy}
              leftIcon={<CopyIcon />}
            >
              Copy
            </Button>
            <Button
              size="sm"
              colorScheme="blue"
              onClick={handleComposeOpen}
              leftIcon={<EditIcon />}
            >
              Compose Event
            </Button>
          </div>
        </div>
        <pre className="p-4 bg-muted rounded-md text-sm font-mono overflow-x-auto whitespace-pre-wrap break-words">
          {exampleJson}
        </pre>
      </div>

      <Modal isOpen={isOpen} onClose={() => setIsOpen(false)} size="xl">
        <ModalHeader onClose={() => setIsOpen(false)}>
          Compose New Event
        </ModalHeader>
        <ModalBody className="min-h-[350px]">
          <p className="font-medium mb-2">Event Type: {eventType.name}</p>
          <Textarea
            value={editedJson}
            onChange={(e) => setEditedJson(e.target.value)}
            className="min-h-[300px] font-mono text-sm"
          />
          {responseMessage.text && (
            <p
              className={`mt-2 ${responseMessage.hasError ? "text-red-500" : "text-green-500"}`}
            >
              {responseMessage.text}
            </p>
          )}
        </ModalBody>
        <ModalFooter>
          <Button variant="outline" onClick={() => setIsOpen(false)}>
            Close
          </Button>
          <Button
            colorScheme="blue"
            onClick={handleSendEvent}
            disabled={responseMessage.hasError}
          >
            Send Event
          </Button>
        </ModalFooter>
      </Modal>
    </>
  );
};

export default EventTypeExamplePayload;
