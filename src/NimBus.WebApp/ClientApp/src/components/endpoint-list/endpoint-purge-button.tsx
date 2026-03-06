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

// Delete icon
const DeleteIcon = () => (
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
      d="M19 7l-.867 12.142A2 2 0 0116.138 21H7.862a2 2 0 01-1.995-1.858L5 7m5 4v6m4-6v6m1-10V4a1 1 0 00-1-1h-4a1 1 0 00-1 1v3M4 7h16"
    />
  </svg>
);

interface IEndpontPurgeButtonProps {
  endpointId: string;
  refreshEndpoint: (endpointId: string) => {};
  startLoading: () => void;
  stopLoading: () => void;
}

export default function EndpointPurgeButton(props: IEndpontPurgeButtonProps) {
  const client = new api.Client(api.CookieAuth());
  const [isOpen, setIsOpen] = useState(false);

  return (
    <>
      <Tooltip content={`Purge everything on ${props.endpointId}`}>
        <Button
          size="xs"
          colorScheme="red"
          aria-label="Purge endpoint"
          onClick={(event) => {
            event.preventDefault();
            event.stopPropagation();
            setIsOpen(true);
          }}
        >
          <DeleteIcon />
        </Button>
      </Tooltip>
      <Modal isOpen={isOpen} onClose={() => setIsOpen(false)}>
        <ModalHeader onClose={() => setIsOpen(false)}>
          Purge {props.endpointId}?
        </ModalHeader>
        <ModalBody>
          Are your sure you want to delete all Failed, Deferred and Pending
          messages on {props.endpointId}?
        </ModalBody>
        <ModalFooter>
          <Button
            colorScheme="red"
            onClick={(event) => {
              props.startLoading();
              client
                .postEndpointPurge(props.endpointId)
                .then((result) => {
                  props.refreshEndpoint(props.endpointId);
                })
                .catch((err) => {
                  props.stopLoading();
                });
              setIsOpen(false);
            }}
          >
            Purge
          </Button>
        </ModalFooter>
      </Modal>
    </>
  );
}
