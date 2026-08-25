import { useState } from "react";
import { useNavigate } from "react-router-dom";
import * as api from "api-client";
import {
  Button,
  DropdownItem,
  DropdownMenu,
  DropdownSeparator,
  Modal,
  ModalBody,
  ModalFooter,
  ModalHeader,
  Toggle,
  useToast,
} from "components/ui";
import { isOwnerRole, useAccess } from "hooks/use-access";
import EndpointAlertsModal from "./endpoint-alerts-modal";
import EndpointAccessModal from "./endpoint-access-modal";
import {
  BellIcon,
  ExternalLinkIcon,
  MoreHorizontalIcon,
  PowerIcon,
  ShieldCheckIcon,
  TrashIcon,
} from "./icons";

interface IEndpointRowActionsProps {
  endpointId: string;
  /** "active" | "disabled" | "not-found" — as returned with the status counts. */
  subscriptionStatus?: string;
  failed: number;
  deferred: number;
  pending: number;
  storageAvailable: boolean;
  /** Deployment environment, from /api/app/stats. Gates purge outside dev. */
  env?: string;
  refreshEndpoint: (endpointId: string) => unknown;
  startLoading: () => void;
  stopLoading: () => void;
}

// Per-row endpoint controls: an enable/disable switch plus the overflow menu
// (alerts, open, access, purge). Every mutation here is Owner-gated server
// side, so the menu only offers what the current user may actually do.
export default function EndpointRowActions(props: IEndpointRowActionsProps) {
  const [client] = useState(() => new api.Client(api.CookieAuth()));
  const navigate = useNavigate();
  const { addToast } = useToast();
  const { access } = useAccess();

  const [disableOpen, setDisableOpen] = useState(false);
  const [purgeOpen, setPurgeOpen] = useState(false);
  const [alertsOpen, setAlertsOpen] = useState(false);
  const [accessOpen, setAccessOpen] = useState(false);
  const [purgeConfirmText, setPurgeConfirmText] = useState("");

  const { endpointId, subscriptionStatus, storageAvailable } = props;
  const subscriptionMissing = subscriptionStatus === "not-found";
  const enabled = subscriptionStatus !== "disabled" && !subscriptionMissing;

  // PostEndpointSubscriptionstatus / Purge / Subscribe all require Owner on the
  // endpoint (site Owners hold it implicitly), so mirror that rule rather than
  // offering controls that would come back 403.
  const isSiteOwner = isOwnerRole(access?.siteRole);
  const canManage =
    isSiteOwner ||
    (access?.endpointRoles ?? []).some(
      (role) => role.endpointId === endpointId && isOwnerRole(role.role),
    );
  // Purge is refused in prod/stag unless the caller is a site Owner — the same
  // rule PostEndpointPurgeAsync applies.
  const envIsProtected = props.env === "prod" || props.env === "stag";
  const purgeAllowed = canManage && (isSiteOwner || !envIsProtected);

  const fmt = (n: number) => (storageAvailable ? n.toLocaleString() : "—");
  const totalMessages = props.failed + props.deferred + props.pending;

  const setSubscriptionStatus = (action: "enable" | "disable") => {
    props.startLoading();
    return client
      .postEndpointSubscriptionstatus(endpointId, action)
      .then(() => {
        props.refreshEndpoint(endpointId);
        return true;
      })
      .catch(() => {
        props.stopLoading();
        addToast({
          variant: "error",
          title: `Could not ${action} ${endpointId}.`,
          duration: 6000,
        });
        return false;
      });
  };

  const enableEndpoint = (title: string) =>
    setSubscriptionStatus("enable").then((ok) => {
      if (ok) {
        addToast({ variant: "success", title, duration: 3000 });
      }
    });

  const handleToggle = (next: boolean) => {
    if (next) {
      // Enabling — no friction, just turn it back on.
      void enableEndpoint(`${endpointId} enabled.`);
    } else {
      setDisableOpen(true);
    }
  };

  const confirmDisable = () => {
    setDisableOpen(false);
    void setSubscriptionStatus("disable").then((ok) => {
      if (!ok) return;
      addToast({
        variant: "warning",
        title: `${endpointId} disabled — it has stopped processing.`,
        duration: 6000,
        action: {
          label: "Undo",
          onClick: () => {
            void enableEndpoint(`${endpointId} re-enabled.`);
          },
        },
      });
    });
  };

  const confirmPurge = () => {
    setPurgeOpen(false);
    setPurgeConfirmText("");
    props.startLoading();
    client
      .postEndpointPurge(endpointId)
      .then(() => {
        props.refreshEndpoint(endpointId);
        addToast({
          variant: "warning",
          title: `Purged all data from ${endpointId}.`,
          duration: 4000,
        });
      })
      .catch(() => {
        props.stopLoading();
        addToast({
          variant: "error",
          title: `Could not purge ${endpointId}.`,
          duration: 6000,
        });
      });
  };

  const purgeArmed = purgeConfirmText.trim() === endpointId;

  const impactRow = (label: string, value: string, danger = false) => (
    <div className="flex justify-between font-mono text-[11.5px]">
      <span className="text-muted-foreground">{label}</span>
      <b className={danger ? "font-semibold text-status-danger" : "font-semibold"}>
        {value}
      </b>
    </div>
  );

  return (
    <span
      className="inline-flex items-center justify-end gap-2.5"
      onClick={(e) => e.stopPropagation()}
    >
      <Toggle
        checked={enabled}
        disabled={!canManage || subscriptionMissing}
        onChange={handleToggle}
        aria-label={`${enabled ? "Disable" : "Enable"} ${endpointId}`}
      />

      <DropdownMenu
        trigger={<MoreHorizontalIcon className="h-4 w-4" />}
        triggerLabel={`More actions for ${endpointId}`}
      >
        {canManage && (
          <DropdownItem icon={<BellIcon />} onSelect={() => setAlertsOpen(true)}>
            Configure alerts…
          </DropdownItem>
        )}
        <DropdownItem
          icon={<ExternalLinkIcon />}
          onSelect={() => navigate(`/Endpoints/Details/${endpointId}`)}
        >
          Open endpoint
        </DropdownItem>
        {canManage && <DropdownSeparator />}
        {canManage && (
          <DropdownItem
            icon={<ShieldCheckIcon />}
            onSelect={() => setAccessOpen(true)}
          >
            Manage access…
          </DropdownItem>
        )}
        {purgeAllowed && (
          <DropdownItem
            destructive
            icon={<TrashIcon />}
            trailing={envIsProtected ? undefined : "dev only"}
            onSelect={() => {
              setPurgeConfirmText("");
              setPurgeOpen(true);
            }}
          >
            Purge data…
          </DropdownItem>
        )}
      </DropdownMenu>

      {/* Disable confirmation */}
      <Modal isOpen={disableOpen} onClose={() => setDisableOpen(false)}>
        <ModalHeader onClose={() => setDisableOpen(false)}>
          <span className="inline-flex items-center gap-2">
            <span className="inline-flex h-7 w-7 items-center justify-center rounded-nb-sm bg-status-warning-50 text-status-warning">
              <PowerIcon className="h-4 w-4" />
            </span>
            Disable {endpointId}?
          </span>
        </ModalHeader>
        <ModalBody>
          <p className="text-sm text-muted-foreground m-0">
            The subscription goes to{" "}
            <span className="font-mono">ReceiveDisabled</span> and the endpoint
            stops processing. Pending messages stay queued; configuration is
            preserved.
          </p>
          <div className="mt-3 flex flex-col gap-1 rounded-nb-md border border-border bg-background px-3 py-2.5">
            {impactRow("Pending messages", fmt(props.pending))}
            {impactRow("Deferred messages", fmt(props.deferred))}
            {impactRow("Failed messages", fmt(props.failed))}
          </div>
        </ModalBody>
        <ModalFooter>
          <Button
            variant="ghost"
            colorScheme="gray"
            onClick={() => setDisableOpen(false)}
          >
            Cancel
          </Button>
          <Button colorScheme="primary" onClick={confirmDisable}>
            Disable endpoint
          </Button>
        </ModalFooter>
      </Modal>

      {/* Purge confirmation — armed by typing the endpoint name */}
      <Modal isOpen={purgeOpen} onClose={() => setPurgeOpen(false)}>
        <ModalHeader onClose={() => setPurgeOpen(false)}>
          <span className="inline-flex items-center gap-2">
            <span className="inline-flex h-7 w-7 items-center justify-center rounded-nb-sm bg-status-danger-50 text-status-danger">
              <TrashIcon className="h-4 w-4" />
            </span>
            Purge all data on {endpointId}
          </span>
        </ModalHeader>
        <ModalBody>
          <p className="text-sm text-muted-foreground m-0">
            Deletes every queued, deferred and failed message. Endpoint
            configuration is preserved.{" "}
            <b className="text-status-danger">This action cannot be undone.</b>
          </p>
          <div className="mt-3 flex flex-col gap-1 rounded-nb-md border border-border bg-background px-3 py-2.5">
            {impactRow("Messages to be deleted", fmt(totalMessages), true)}
            {impactRow("Includes", "failed, deferred, pending")}
            {props.env &&
              impactRow(
                "Environment",
                `${props.env.toUpperCase()} — purge allowed`,
              )}
          </div>
          <label
            htmlFor={`purge-confirm-${endpointId}`}
            className="mt-3.5 block text-xs font-semibold"
          >
            Type{" "}
            <code className="rounded bg-muted px-1.5 py-0.5 font-mono text-[12px]">
              {endpointId}
            </code>{" "}
            to confirm
          </label>
          <input
            id={`purge-confirm-${endpointId}`}
            autoComplete="off"
            value={purgeConfirmText}
            onChange={(e) => setPurgeConfirmText(e.currentTarget.value)}
            className="mt-1.5 w-full rounded-nb-md border-[1.5px] border-border-strong bg-background px-3 py-2.5 font-mono text-sm focus:border-status-danger focus:outline-none focus:ring-[3px] focus:ring-status-danger-50"
          />
        </ModalBody>
        <ModalFooter>
          <Button
            variant="ghost"
            colorScheme="gray"
            onClick={() => setPurgeOpen(false)}
          >
            Cancel
          </Button>
          <Button colorScheme="red" disabled={!purgeArmed} onClick={confirmPurge}>
            Purge data
          </Button>
        </ModalFooter>
      </Modal>

      {canManage && (
        <EndpointAlertsModal
          endpointId={endpointId}
          isOpen={alertsOpen}
          onClose={() => setAlertsOpen(false)}
        />
      )}

      {canManage && (
        <EndpointAccessModal
          endpointId={endpointId}
          isOpen={accessOpen}
          onClose={() => setAccessOpen(false)}
        />
      )}
    </span>
  );
}
