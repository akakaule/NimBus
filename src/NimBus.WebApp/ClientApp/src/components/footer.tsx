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
          // Labelled because the catalog is often called "NimBus" at the same version
          // as the product itself (the bundled sample catalog in a local build), and
          // two unlabelled "NimBus 0.0.0" chips are indistinguishable.
          <span title="Platform catalog package (endpoints and event types)">
            config: {platformPackage}
          </span>
        )}
        {storageProvider && (
          // A plain span, not a Badge: the badge's pill is transparent here anyway,
          // and its font-semibold made this item render heavier than the version
          // text beside it. Sharing the row's typography keeps the three items one
          // line of metadata rather than one label and two values.
          <span title="Active NimBus message-store provider">
            store: {storageProvider}
          </span>
        )}
      </div>
      <span>@ 2026 · NimBus</span>
    </div>
  );
};

export default Footer;
