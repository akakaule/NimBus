import { Badge } from "components/ui/badge";
import { getPlatformVersion, getStorageProvider } from "hooks/app-status";

const Footer = () => {
  const platformVersion = getPlatformVersion();
  const storageProvider = getStorageProvider();

  return (
    <div className="flex flex-row justify-between flex-nowrap mx-[10%] mt-8 mb-4 border-t border-border box-border text-base text-left pt-2">
      <div className="flex gap-2">
        <Badge
          variant="secondary"
          className="bg-transparent text-muted-foreground"
        >
          {platformVersion ?? ""}
        </Badge>
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
      <Badge
        variant="secondary"
        className="bg-transparent text-muted-foreground"
      >
        @ 2026 - Nimbus
      </Badge>
    </div>
  );
};

export default Footer;
