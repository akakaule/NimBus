import { Badge } from "components/ui/badge";
import { getPlatformVersion, getStorageProvider } from "hooks/app-status";

const Footer = () => {
  const platformVersion = getPlatformVersion();
  const storageProvider = getStorageProvider();

  return (
    <div className="flex flex-row justify-between flex-nowrap px-7 py-3 border-t border-border text-xs font-mono text-muted-foreground uppercase tracking-wider">
      <div className="flex gap-3">
        {platformVersion && <span>{platformVersion}</span>}
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
