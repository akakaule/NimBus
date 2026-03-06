import { useState } from "react";
import { Button } from "components/ui/button";
import { Tooltip } from "components/ui/tooltip";
import {
  Modal,
  ModalHeader,
  ModalBody,
  ModalFooter,
} from "components/ui/modal";
import * as api from "api-client";

// Check icon
const CheckIcon = () => (
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
      d="M5 13l4 4L19 7"
    />
  </svg>
);

// Close icon
const CloseIcon = () => (
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
      d="M6 18L18 6M6 6l12 12"
    />
  </svg>
);

interface IEndpontDisableButtonProps {
  endpointId: string;
  status: string;
  refreshEndpoint: (endpointId: string) => {};
  startLoading: () => void;
  stopLoading: () => void;
}

export default function EndpointDisableButton(
  props: IEndpontDisableButtonProps,
) {
  const client = new api.Client(api.CookieAuth());
  const [isOpen, setIsOpen] = useState(false);

  return (
    <>
      <Tooltip content={`Disable ${props.endpointId}`}>
        <Button
          size="xs"
          colorScheme={props.status === "enable" ? "green" : "red"}
          aria-label="Disable endpoint"
          onClick={(event) => {
            event.preventDefault();
            event.stopPropagation();
            setIsOpen(true);
          }}
        >
          {props.status === "enable" ? <CheckIcon /> : <CloseIcon />}
        </Button>
      </Tooltip>
      <Modal isOpen={isOpen} onClose={() => setIsOpen(false)}>
        <ModalHeader onClose={() => setIsOpen(false)}>
          {props.status} {props.endpointId}?
        </ModalHeader>
        <ModalBody>
          Are your sure you want to {props.status} {props.endpointId}?
        </ModalBody>
        <ModalFooter>
          <Button
            colorScheme="red"
            onClick={(event) => {
              props.startLoading();
              client
                .postEndpointSubscriptionstatus(props.endpointId, props.status)
                .then((result) => {
                  props.refreshEndpoint(props.endpointId);
                })
                .catch((err) => {
                  props.stopLoading();
                });
              setIsOpen(false);
            }}
          >
            Update
          </Button>
        </ModalFooter>
      </Modal>
    </>
  );
}
