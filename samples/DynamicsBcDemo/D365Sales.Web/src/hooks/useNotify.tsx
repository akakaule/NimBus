import { useMemo, type ReactNode } from 'react';
import { Toast, ToastBody, ToastTitle, useToastController, type ToastIntent } from '@fluentui/react-components';

export const TOASTER_ID = 'sales-hub-toaster';

export interface Notify {
  success: (title: ReactNode, body?: ReactNode) => void;
  info: (title: ReactNode, body?: ReactNode) => void;
  warning: (title: ReactNode, body?: ReactNode) => void;
  error: (title: ReactNode, body?: ReactNode) => void;
}

/** Toasts in the app-wide toaster; they outlive navigation (qualify → opportunity form). */
export function useNotify(): Notify {
  const { dispatchToast } = useToastController(TOASTER_ID);
  return useMemo(() => {
    const show = (intent: ToastIntent, timeout: number) => (title: ReactNode, body?: ReactNode) =>
      dispatchToast(
        <Toast>
          <ToastTitle>{title}</ToastTitle>
          {body !== undefined && <ToastBody>{body}</ToastBody>}
        </Toast>,
        // Long enough for an audience to read along.
        { intent, timeout },
      );
    return {
      success: show('success', 7000),
      info: show('info', 7000),
      warning: show('warning', 9000),
      error: show('error', 10000),
    };
  }, [dispatchToast]);
}
