import { emitToast } from "components/ui/toast";

export const notifySuccess = (msg: string) =>
  emitToast({ variant: "success", title: msg, duration: 3000 });

export const notifyError = (msg: string) =>
  emitToast({ variant: "error", title: msg, duration: 3000 });

export const notifyWarning = (msg: string) =>
  emitToast({ variant: "warning", title: msg, duration: 4000 });

export const notifyInfo = (msg: string) =>
  emitToast({ variant: "info", title: msg, duration: 3000 });

// Success toast with an inline "Undo" action. Used for reversible operator
// actions (e.g. marking an event reported) so a mis-click is one click to undo.
export const notifyWithUndo = (msg: string, onUndo: () => void) =>
  emitToast({
    variant: "success",
    title: msg,
    duration: 6000,
    action: { label: "Undo", onClick: onUndo },
  });
