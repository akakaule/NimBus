import { useState } from "react";
import { Badge } from "components/ui/badge";
import { getPlatformVersion } from "hooks/app-status";

const Footer = () => {
  const [platformVersion] = useState(getPlatformVersion());

  return (
    <div className="flex flex-row justify-between flex-nowrap mx-[10%] mt-8 mb-4 border-t border-border box-border text-base text-left pt-2">
      <Badge
        variant="secondary"
        className="bg-transparent text-muted-foreground"
      >
        {platformVersion !== undefined ? platformVersion : getPlatformVersion()}
      </Badge>
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
