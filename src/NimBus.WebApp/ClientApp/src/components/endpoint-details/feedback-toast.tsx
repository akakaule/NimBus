import * as React from "react";
import { useToast } from "components/ui/toast";

export type toastProperties = {
  display: boolean | undefined;
  status: "success" | "error";
  title: string;
  description: string;
};

export interface IFeedbackToastProps {
  values: toastProperties;
}

export default function FeedbackToast(props: IFeedbackToastProps) {
  const { addToast } = useToast();

  React.useEffect(() => {
    if (props.values.display) {
      addToast({
        title: props.values.title,
        description: props.values.description,
        variant: props.values.status,
        duration: 3000,
      });
    }
  }, [props]);

  return <></>;
}
