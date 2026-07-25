import * as api from "api-client";
import { useEffect, useState, type Dispatch, type SetStateAction } from "react";
import { useParams } from "react-router-dom";
import DataTable, {
  ITableHeadAction,
  ITableHeadCell,
  ITableRow,
} from "components/data-table";
import FeedbackToast, { toastProperties } from "../feedback-toast";
import AlertModal from "../alert-modal";

interface ISubscriptionsTabProps {
  setIsTabEnabled: Dispatch<SetStateAction<boolean>>;
}

// Endpoint alert subscriptions (the "Alerts" tab). Lists who gets notified
// about failures on this endpoint; Info opens a read-only AlertModal, Delete
// removes a subscription (owner-only, enforced server-side).
const SubscriptionsTab = (props: ISubscriptionsTabProps) => {
  const [client] = useState(() => new api.Client(api.CookieAuth()));
  const params = useParams();
  const endpointId = params.id!;

  const [subscriptions, setSubscriptions] = useState<api.EndpointSubscription[]>(
    [],
  );
  const [isLoading, setIsLoading] = useState<boolean>(true);
  const [selectedSubscription, setSelectedSubscription] =
    useState<api.EndpointSubscription>();
  const [isSubscriptionModalOpen, setIsSubscriptionModalOpen] =
    useState<boolean>(false);

  const [feedbackToast, setFeedbackToast] = useState<toastProperties>({
    display: undefined,
    status: "success",
    description: "",
    title: "",
  });

  useEffect(() => {
    const fetchData = async () => {
      try {
        const tempSubscriptions = await client.getEndpointSubscribe(endpointId);
        setSubscriptions(tempSubscriptions);
        props.setIsTabEnabled(tempSubscriptions.length > 0);
      } catch (e) {
        console.error("Failed to load endpoint subscriptions", e);
      } finally {
        setIsLoading(false);
      }
    };
    fetchData();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [endpointId]);

  const closeSubscriptionModal = () => setIsSubscriptionModalOpen(false);

  const doActionSelectedRows = (rows: ITableRow[], actionName: string) => {
    rows.forEach((row) => {
      const action = row.bodyActions?.find((a) => a.name === actionName);
      action?.onClick();
    });
  };

  const deleteSubscription = async (
    subscription: api.EndpointSubscription,
  ): Promise<void> => {
    // Optimistic removal; the refetch below reconciles with the server.
    setSubscriptions((prev) => prev.filter((s) => s.id !== subscription.id));
    try {
      await client.deleteEndpointSubscribe(
        endpointId,
        new api.SubscriptionAuthor({
          id: subscription.id,
          author: subscription.authorId,
        }),
      );
      const fresh = await client.getEndpointSubscribe(endpointId);
      setSubscriptions(fresh);
      props.setIsTabEnabled(fresh.length > 0);
      setFeedbackToast({
        display: true,
        status: "success",
        description: "Subscription deleted successfully",
        title: "Successfully deleted",
      });
    } catch {
      const fresh = await client
        .getEndpointSubscribe(endpointId)
        .catch(() => null);
      if (fresh) setSubscriptions(fresh);
      setFeedbackToast({
        display: true,
        status: "error",
        description:
          "Unable to delete subscription. Logged in user is not owner of subscription.",
        title: "Unable to delete",
      });
    }
  };

  const rows = subscriptions.map((sub): ITableRow => {
    const recipient = sub.mail || sub.url || "—";
    return {
      id: sub.id!,
      bodyActions: [
        {
          name: "Info",
          onClick: () => {
            setSelectedSubscription(sub);
            setIsSubscriptionModalOpen(true);
            return true;
          },
        },
        {
          name: "Delete",
          onClick: () => {
            deleteSubscription(sub);
            return false;
          },
        },
      ],
      hoverText: recipient,
      data: new Map([
        ["author", { value: sub.authorId ?? "—", searchValue: sub.authorId ?? "" }],
        ["recipient", { value: recipient, searchValue: recipient }],
        ["type", { value: sub.type ?? "—", searchValue: sub.type ?? "" }],
      ]),
    };
  });

  const headCells: ITableHeadCell[] = [
    { id: "author", label: "Author", numeric: false },
    { id: "recipient", label: "Recipient", numeric: false },
    { id: "type", label: "Type", numeric: false },
  ];

  const headActions: ITableHeadAction[] = [
    {
      name: "Delete",
      onClick: (selectedRows: ITableRow[]) => {
        doActionSelectedRows(selectedRows, "Delete");
        return false;
      },
    },
  ];

  return (
    <div className="flex flex-col w-full">
      <DataTable
        headCells={headCells}
        headActions={headActions}
        rows={rows}
        withCheckboxes={true}
        noDataMessage="No subscriptions available"
        isLoading={isLoading}
        count={subscriptions.length}
        hideDense={true}
        fixedWidth={"-webkit-fill-available"}
      />
      <AlertModal
        isOpen={isSubscriptionModalOpen}
        onClose={closeSubscriptionModal}
        subscription={selectedSubscription}
        endpointId={endpointId}
        editable={false}
      />
      <FeedbackToast values={feedbackToast} />
    </div>
  );
};

export default SubscriptionsTab;
