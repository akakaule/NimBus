import PlatformServicesCard from "./platform-services-card";
import HeartbeatCard from "./heartbeat-card";

/**
 * Admin → Health. Two questions, in the order an operator asks them: is the
 * platform itself running, and are the adapters on the other side of each
 * endpoint answering?
 */
export default function Health() {
  return (
    <div className="w-full space-y-4">
      <PlatformServicesCard />
      <HeartbeatCard />
    </div>
  );
}
