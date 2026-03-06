import * as React from "react";
import * as api from "api-client";
import { Button } from "components/ui/button";
import {
  Modal,
  ModalHeader,
  ModalBody,
  ModalFooter,
} from "components/ui/modal";
import { Select } from "components/ui/select";
import { Textarea } from "components/ui/textarea";
import { formatMoment } from "functions/endpoint.functions";
import { Link } from "react-router-dom";
import { useEffect, useState } from "react";

interface IMessageListingProps {
  eventDetails: api.Event | undefined;
  eventTypes: api.EventType[];
  skipEvent: (eventId: string, messageId: string) => Promise<void>;
  resubmitEvent: (eventId: string, messageId: string) => Promise<void>;
  resubmitEventWithChanges: (
    eventId: string,
    messageId: string,
    body: api.ResubmitWithChanges,
  ) => Promise<void>;
}

interface IButtonState {
  isDisabled: boolean;
  text: string;
}

export default function MessageListing(props: IMessageListingProps) {
  const [showErrorDetails, setShowErrorDetails] = useState(false);
  const handleErrorDetailsToggle = () => setShowErrorDetails(!showErrorDetails);
  const [isOpen, setIsOpen] = useState(false);
  const onOpen = () => setIsOpen(true);
  const onClose = () => setIsOpen(false);
  const [textAreaValue, setTextAreaValue] = useState(
    props.eventDetails?.messageContent?.eventContent?.eventJson,
  );
  const [eventTypeIdValue, setEventTypeIdValue] = useState(
    props.eventDetails?.eventTypeId,
  );

  useEffect(() => {
    setTextAreaValue(
      props.eventDetails?.messageContent?.eventContent?.eventJson,
    );
    setShowErrorDetails(false);
  }, [props.eventDetails]);

  const resubmitWithChanges: IButtonState = {
    isDisabled: false,
    text: "Resubmit with changes",
  };
  const [resubmitWithChangesButton, setResubmitWithChangesButton] =
    useState(resubmitWithChanges);

  const resubmit: IButtonState = { isDisabled: false, text: "Resubmit" };
  const [resubmitButton, setResubmitButton] = useState(resubmit);

  const skip: IButtonState = { isDisabled: false, text: "Skip" };
  const [skipButton, setSkipButton] = useState(skip);

  const handleInputChange = (e: React.ChangeEvent<HTMLTextAreaElement>) => {
    const inputValue = e.target.value;
    const sanitizedValue = inputValue.replace(/\n/g, "");
    setTextAreaValue(sanitizedValue);
  };

  const handleEventTypeIdChange = (e: React.ChangeEvent<HTMLSelectElement>) => {
    setEventTypeIdValue(e.target.value);
  };

  const isFailedMessage = (status: string | undefined): boolean => {
    if (!status) return false;
    const lowerStatus = status.toLowerCase();
    return (
      lowerStatus === "failed" ||
      lowerStatus === "unsupported" ||
      lowerStatus === "deadlettered"
    );
  };

  const isDeadletteredMessage = (status: string | undefined): boolean => {
    if (!status) return false;
    const lowerStatus = status.toLowerCase();
    return lowerStatus === "deadlettered";
  };

  const skipEventClick = async () => {
    setSkipButton({ text: "Skipping...", isDisabled: true });
    try {
      await props.skipEvent(
        props.eventDetails?.eventId!,
        props.eventDetails?.lastMessageId!,
      );
      setSkipButton({ text: "Skipped", isDisabled: true });
    } catch {
      setSkipButton({ text: "Skip failed", isDisabled: false });
    }
  };

  const resubmitEventClick = async () => {
    setResubmitButton({ text: "Resubmitting...", isDisabled: true });
    try {
      await props.resubmitEvent(
        props.eventDetails?.eventId!,
        props.eventDetails?.lastMessageId!,
      );
      setResubmitButton({ text: "Resubmitted", isDisabled: true });
    } catch {
      setResubmitButton({ text: "Resubmit failed", isDisabled: false });
    }
  };

  const resubmitEventWithChangesClick = async () => {
    onClose();
    setResubmitWithChangesButton({ text: "Resubmitting...", isDisabled: true });
    const body: api.ResubmitWithChanges = api.ResubmitWithChanges.fromJS({
      eventTypeId: eventTypeIdValue,
      eventContent: textAreaValue,
    });
    try {
      await props.resubmitEventWithChanges(
        props.eventDetails?.eventId!,
        props.eventDetails?.lastMessageId!,
        body,
      );
      setResubmitWithChangesButton({ text: "Resubmitted", isDisabled: true });
    } catch {
      setResubmitWithChangesButton({
        text: "Resubmit failed",
        isDisabled: false,
      });
    }
  };

  return (
    <div className="w-full">
      <h4 className="text-lg font-semibold flex items-center gap-4">
        Details
        {isFailedMessage(props.eventDetails?.resolutionStatus) && (
          <div className="flex gap-2">
            <Button
              size="xs"
              colorScheme="blue"
              disabled={resubmitButton.isDisabled}
              onClick={resubmitEventClick}
            >
              {resubmitButton.text}
            </Button>
            <Button
              size="xs"
              colorScheme="green"
              disabled={resubmitWithChangesButton.isDisabled}
              onClick={onOpen}
            >
              {resubmitWithChangesButton.text}
            </Button>
            <Button
              size="xs"
              colorScheme="red"
              variant="outline"
              disabled={skipButton.isDisabled}
              onClick={skipEventClick}
            >
              {skipButton.text}
            </Button>
          </div>
        )}
      </h4>
      <br />
      <div className="flex flex-col">
        <table className="w-full flex-1 text-sm mr-4">
          <tbody>
            <tr className="hover:bg-accent">
              <td className="py-2 pr-4">
                <b>EventId</b>
              </td>
              <td className="py-2">{props.eventDetails?.eventId}</td>
            </tr>
            <tr className="hover:bg-accent">
              <td className="py-2 pr-4">
                <b>EventTypeId</b>
              </td>
              <td className="py-2">{props.eventDetails?.eventTypeId}</td>
            </tr>
            <tr className="hover:bg-accent">
              <td className="py-2 pr-4">
                <b>SessionId</b>
              </td>
              <td className="py-2">{props.eventDetails?.sessionId}</td>
            </tr>
            <tr className="hover:bg-accent">
              <td className="py-2 pr-4">
                <b>Source Endpoint</b>
              </td>
              <td className="py-2">
                {(props.eventDetails?.originatingFrom ?? props.eventDetails?.from) && (
                  <Link
                    to={`/Endpoints/Details/${props.eventDetails?.originatingFrom ?? props.eventDetails?.from}`}
                    className="text-primary hover:underline"
                  >
                    {props.eventDetails?.originatingFrom ?? props.eventDetails?.from}
                  </Link>
                )}
              </td>
            </tr>
            <tr className="hover:bg-accent">
              <td className="py-2 pr-4">
                <b>Status</b>
              </td>
              <td className="py-2">{props.eventDetails?.resolutionStatus}</td>
            </tr>
            <tr className="hover:bg-accent">
              <td className="py-2 pr-4">
                <b>MessageId</b>
              </td>
              <td className="py-2">{props.eventDetails?.lastMessageId}</td>
            </tr>
            <tr className="hover:bg-accent">
              <td className="py-2 pr-4">
                <b>Enqueued Time (UTC)</b>
              </td>
              <td className="py-2">
                {formatMoment(props.eventDetails?.enqueuedTimeUtc)}
              </td>
            </tr>
            <tr className="hover:bg-accent">
              <td className="py-2 pr-4">
                <b>From</b>
              </td>
              <td className="py-2">{props.eventDetails?.from}</td>
            </tr>
            <tr className="hover:bg-accent">
              <td className="py-2 pr-4">
                <b>To</b>
              </td>
              <td className="py-2">{props.eventDetails?.to}</td>
            </tr>
            <tr className="hover:bg-accent">
              <td className="py-2 pr-4">
                <b>OriginatingMessageId</b>
              </td>
              <td className="py-2">
                {props.eventDetails?.originatingMessageId}
              </td>
            </tr>
          </tbody>
        </table>
      </div>
      <br />
      {isFailedMessage(props.eventDetails?.resolutionStatus) && (
        <>
          <h4 className="text-lg font-semibold mt-4">Error</h4>
          <br />
          {props.eventDetails?.messageContent?.errorContent && (
            <table className="text-sm">
              {!isDeadletteredMessage(props.eventDetails?.resolutionStatus) ? (
                <tbody>
                  <tr className="hover:bg-accent">
                    <td className="py-2 pr-4">
                      <b>Error Text</b>
                    </td>
                    <td className="py-2">
                      {
                        props.eventDetails?.messageContent?.errorContent
                          ?.errorText
                      }
                    </td>
                  </tr>
                  <tr className="hover:bg-accent">
                    <td className="py-2 pr-4">
                      <b>Error Type</b>
                    </td>
                    <td className="py-2">
                      {
                        props.eventDetails?.messageContent?.errorContent
                          ?.errorType
                      }
                    </td>
                  </tr>
                  <tr className="hover:bg-accent">
                    <td className="py-2 pr-4">
                      <b>Exception Source</b>
                    </td>
                    <td className="py-2">
                      {
                        props.eventDetails?.messageContent?.errorContent
                          ?.exceptionSource
                      }
                    </td>
                  </tr>
                  <tr className="hover:bg-accent">
                    <td className="py-2 pr-4">
                      <b>Exception</b>
                    </td>
                    <td className="py-2">
                      {
                        props.eventDetails?.messageContent?.errorContent
                          ?.exceptionStackTrace
                      }
                    </td>
                  </tr>
                </tbody>
              ) : (
                <tbody>
                  <tr className="hover:bg-accent">
                    <td className="py-2 pr-4">
                      <b>DeadLetter Reason</b>
                    </td>
                    <td className="py-2">
                      {props.eventDetails?.deadLetterReason}
                    </td>
                  </tr>
                  <tr className="hover:bg-accent">
                    <td className="py-2 pr-4">
                      <b>DeadLetter Error Description</b>
                    </td>
                    <td className="py-2">
                      {props.eventDetails?.deadLetterErrorDescription}
                    </td>
                  </tr>
                </tbody>
              )}
            </table>
          )}
          <br />
        </>
      )}

      {props.eventDetails?.messageContent?.eventContent?.eventJson && (
        <>
          <h4 className="text-lg font-semibold mt-4">Payload</h4>
          <br />
          <pre className="bg-muted p-4 rounded text-sm overflow-x-auto w-full">
            {JSON.stringify(
              JSON.parse(
                props.eventDetails.messageContent.eventContent.eventJson,
              ),
              null,
              2,
            )}
          </pre>
        </>
      )}

      <Modal isOpen={isOpen} onClose={onClose} size="2xl">
        <ModalHeader onClose={onClose}>Resubmit with changes</ModalHeader>
        <ModalBody>
          <div className="mb-4">
            <label className="block text-sm font-medium text-foreground mb-2">
              Original event:
            </label>
            <pre className="bg-muted p-4 rounded text-sm overflow-x-auto max-h-96">
              {props.eventDetails?.messageContent?.eventContent?.eventJson
                ? JSON.stringify(
                    JSON.parse(
                      props.eventDetails?.messageContent?.eventContent
                        ?.eventJson,
                    ),
                    null,
                    2,
                  )
                : ""}
            </pre>
          </div>
          <div className="mb-4">
            <label className="block text-sm font-medium text-foreground mb-2">
              Event type:
            </label>
            <Select
              onChange={handleEventTypeIdChange}
              defaultValue={props.eventDetails?.eventTypeId}
            >
              {props.eventTypes.map((et) => (
                <option key={et.id} value={et.id}>
                  {et.id}
                </option>
              ))}
            </Select>
          </div>
          <div>
            <label className="block text-sm font-medium text-foreground mb-2">
              Modified event:
            </label>
            <Textarea
              value={textAreaValue}
              onChange={handleInputChange}
              className="min-h-[300px]"
            />
          </div>
        </ModalBody>
        <ModalFooter>
          <Button variant="outline" onClick={onClose}>
            Close
          </Button>
          <Button onClick={resubmitEventWithChangesClick}>Resubmit</Button>
        </ModalFooter>
      </Modal>
    </div>
  );
}
