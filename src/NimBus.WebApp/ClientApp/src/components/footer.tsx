import { Badge } from "components/ui/badge";
import {
  useNimBusVersion,
  usePlatformPackage,
  useStorageProvider,
} from "hooks/app-status";

const Footer = () => {
  const nimbusVersion = useNimBusVersion();
  const platformPackage = usePlatformPackage();
  const storageProvider = useStorageProvider();

  return (
    <div className="flex flex-row justify-between flex-nowrap px-7 py-3 border-t border-border text-xs font-mono text-muted-foreground uppercase tracking-wider">
      <div className="flex gap-3">
        {nimbusVersion && <span title="NimBus version">NimBus {nimbusVersion}</span>}
        {platformPackage && (
          <span title="Platform catalog package (endpoints and event types)">
            {platformPackage}
          </span>
        )}
        {storageProvider && (
          <Badge
            variant="secondary"
            className="bg-transparent text-muted-foreground"
            title="Active NimBus message-store provider"
          >
            store: {storageProvider}
          </Badge>
        )}
      </div>
      <span>@ 2026 · NimBus</span>
    </div>
  );
};

export default Footer;
