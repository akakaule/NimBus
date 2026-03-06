import { Tooltip } from "components/ui/tooltip";
import { useToast } from "components/ui/toast";

interface ITruncatedGuidProps {
  guid: string | undefined | null;
  displayLength?: number;
}

export default function TruncatedGuid({
  guid,
  displayLength = 8,
}: ITruncatedGuidProps) {
  const { addToast } = useToast();

  if (!guid) {
    return <span>-</span>;
  }

  const truncated =
    guid.length > displayLength
      ? `${guid.substring(0, displayLength)}...`
      : guid;

  const handleClick = async (e: React.MouseEvent) => {
    e.preventDefault();
    e.stopPropagation();

    try {
      await navigator.clipboard.writeText(guid);
      addToast({
        title: "Copied to clipboard",
        description: guid,
        variant: "success",
        duration: 2000,
      });
    } catch {
      addToast({
        title: "Failed to copy",
        description: "Clipboard access not available",
        variant: "error",
        duration: 2000,
      });
    }
  };

  return (
    <Tooltip content={guid} position="top">
      <span className="cursor-pointer hover:underline" onClick={handleClick}>
        {truncated}
      </span>
    </Tooltip>
  );
}
