import { useState } from "react";
import {
  Modal,
  ModalHeader,
  ModalBody,
  ModalFooter,
} from "components/ui/modal";
import { Button } from "components/ui/button";
import { Input } from "components/ui/input";

interface ConfirmDestructiveActionProps {
  isOpen: boolean;
  onClose: () => void;
  onConfirm: () => void;
  title: string;
  description: string;
  confirmText: string;
  isLoading?: boolean;
}

export default function ConfirmDestructiveAction({
  isOpen,
  onClose,
  onConfirm,
  title,
  description,
  confirmText,
  isLoading = false,
}: ConfirmDestructiveActionProps) {
  const [inputValue, setInputValue] = useState("");
  const isMatch = inputValue.toLowerCase() === confirmText.toLowerCase();

  const handleClose = () => {
    setInputValue("");
    onClose();
  };

  const handleConfirm = () => {
    if (isMatch) {
      onConfirm();
      setInputValue("");
    }
  };

  return (
    <Modal isOpen={isOpen} onClose={handleClose} size="md">
      <ModalHeader onClose={handleClose}>{title}</ModalHeader>
      <ModalBody>
        <div className="space-y-4">
          <p className="text-sm text-muted-foreground">{description}</p>
          <div className="bg-red-50 border border-red-200 rounded-md p-3">
            <p className="text-sm text-red-800 font-medium">
              This action cannot be undone.
            </p>
          </div>
          <div>
            <label className="block text-sm font-medium text-foreground mb-1">
              Type <span className="font-mono font-bold">{confirmText}</span> to
              confirm
            </label>
            <Input
              value={inputValue}
              onChange={(e) => setInputValue(e.target.value)}
              placeholder={confirmText}
              disabled={isLoading}
            />
          </div>
        </div>
      </ModalBody>
      <ModalFooter>
        <Button
          variant="outline"
          colorScheme="gray"
          onClick={handleClose}
          disabled={isLoading}
        >
          Cancel
        </Button>
        <Button
          colorScheme="red"
          onClick={handleConfirm}
          disabled={!isMatch || isLoading}
          isLoading={isLoading}
        >
          Confirm
        </Button>
      </ModalFooter>
    </Modal>
  );
}
