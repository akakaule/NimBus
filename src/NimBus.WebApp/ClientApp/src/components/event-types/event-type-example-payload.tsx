import { useState } from "react";
import { Button } from "components/ui/button";
import { Textarea } from "components/ui/textarea";
import { CodeBlock } from "components/ui/code-block";
import {
  Modal,
  ModalHeader,
  ModalBody,
  ModalFooter,
} from "components/ui/modal";
import { useToast } from "components/ui/toast";
import { generateFakeEventPayload } from "lib/fake-event-data";
import * as api from "api-client";

const CopyIcon = () => (
  <svg className="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
    <path
      strokeLinecap="round"
      strokeLinejoin="round"
      strokeWidth={1.6}
      d="M8 16H6a2 2 0 01-2-2V6a2 2 0 012-2h8a2 2 0 012 2v2m-6 12h8a2 2 0 002-2v-8a2 2 0 00-2-2h-8a2 2 0 00-2 2v8a2 2 0 002 2z"
    />
  </svg>
);

const DownloadIcon = () => (
  <svg className="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
    <path
      strokeLinecap="round"
      strokeLinejoin="round"
      strokeWidth={1.6}
      d="M4 16v2a2 2 0 002 2h12a2 2 0 002-2v-2M7 10l5 5 5-5M12 15V3"
    />
  </svg>
);

const EditIcon = () => (
  <svg className="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
    <path
      strokeLinecap="round"
      strokeLinejoin="round"
      strokeWidth={1.6}
      d="M11 5H6a2 2 0 00-2 2v11a2 2 0 002 2h11a2 2 0 002-2v-5m-1.414-9.414a2 2 0 112.828 2.828L11.828 15H9v-2.828l8.586-8.586z"
    />
  </svg>
);

const SparklesIcon = () => (
  <svg className="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
    <path
      strokeLinecap="round"
      strokeLinejoin="round"
      strokeWidth={1.6}
      d="M9.5 3l1.4 3.6L14.5 8l-3.6 1.4L9.5 13 8.1 9.4 4.5 8l3.6-1.4L9.5 3zm9 7l.9 2.3 2.3.9-2.3.9-.9 2.3-.9-2.3-2.3-.9 2.3-.9.9-2.3zm-4 6l.7 1.7 1.7.7-1.7.7-.7 1.7-.7-1.7-1.7-.7 1.7-.7.7-1.7z"
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

  const handleDownload = () => {
    const blob = new Blob([exampleJson], { type: "application/json" });
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url;
    a.download = `${eventType.name || "payload"}.json`;
    a.click();
    URL.revokeObjectURL(url);
  };

  const handleComposeOpen = () => {
    setEditedJson(exampleJson);
    setResponseMessage({ hasError: false, text: "" });
    setIsOpen(true);
  };

  const handleGenerateFakeData = () => {
    setEditedJson(generateFakeEventPayload(eventType.properties));
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
      <CodeBlock
        title="Example Payload"
        subtitle="application/json"
        actions={
          <>
            <Button variant="outline" size="sm" onClick={handleCopy} leftIcon={<CopyIcon />}>
              Copy
            </Button>
            <Button variant="ghost" size="sm" onClick={handleDownload} leftIcon={<DownloadIcon />}>
              Download
            </Button>
            <Button variant="solid" size="sm" onClick={handleComposeOpen} leftIcon={<EditIcon />}>
              Compose Event
            </Button>
          </>
        }
      >
        {exampleJson}
      </CodeBlock>

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
              className={`mt-2 ${responseMessage.hasError ? "text-status-danger" : "text-status-success"}`}
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
            variant="ghost"
            onClick={handleGenerateFakeData}
            leftIcon={<SparklesIcon />}
          >
            Generate fake data
          </Button>
          <Button
            variant="solid"
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
