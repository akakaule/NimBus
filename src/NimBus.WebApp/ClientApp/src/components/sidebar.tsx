import { NavLink } from "react-router-dom";
import { cn } from "lib/utils";
import { getEnv } from "hooks/app-status";

interface NavItem {
  name: string;
  path: string;
  /** Match path prefixes so /Endpoints/Details/X keeps Endpoints highlighted. */
  matchPrefix?: string;
  icon: React.ReactNode;
  /** Optional trailing badge (e.g. "new") — rendered in the muted pill slot. */
  badge?: string;
}

interface NavGroup {
  label: string;
  items: NavItem[];
}

// Inline SVG glyphs sized 16px to match design's `.si` rule (w/h 14, opacity .95).
const Icon = {
  endpoints: (
    <svg className="w-4 h-4" viewBox="0 0 16 16" fill="none">
      <rect
        x="2"
        y="3"
        width="12"
        height="10"
        rx="1.5"
        stroke="currentColor"
        strokeWidth="1.4"
      />
      <path d="M2 6h12" stroke="currentColor" strokeWidth="1.4" />
    </svg>
  ),
  eventTypes: (
    <svg className="w-4 h-4" viewBox="0 0 16 16" fill="none">
      <path
        d="M2 4h6M2 8h12M2 12h9"
        stroke="currentColor"
        strokeWidth="1.4"
        strokeLinecap="round"
      />
    </svg>
  ),
  messages: (
    <svg className="w-4 h-4" viewBox="0 0 16 16" fill="none">
      <path
        d="M2 4h12v8H2z"
        stroke="currentColor"
        strokeWidth="1.4"
        strokeLinejoin="round"
      />
      <path
        d="M2 4l6 5 6-5"
        stroke="currentColor"
        strokeWidth="1.4"
        strokeLinejoin="round"
      />
    </svg>
  ),
  metrics: (
    <svg className="w-4 h-4" viewBox="0 0 16 16" fill="none">
      <path
        d="M2 13V3M2 13h12M5 10V7M8 10V5M11 10V8"
        stroke="currentColor"
        strokeWidth="1.4"
        strokeLinecap="round"
      />
    </svg>
  ),
  insights: (
    <svg className="w-4 h-4" viewBox="0 0 16 16" fill="none">
      <circle cx="8" cy="8" r="6" stroke="currentColor" strokeWidth="1.4" />
      <path
        d="M8 5v3l2 2"
        stroke="currentColor"
        strokeWidth="1.4"
        strokeLinecap="round"
      />
    </svg>
  ),
  audit: (
    <svg className="w-4 h-4" viewBox="0 0 16 16" fill="none">
      <path
        d="M3 2h7l3 3v9H3z"
        stroke="currentColor"
        strokeWidth="1.4"
        strokeLinejoin="round"
      />
      <path
        d="M5 8h6M5 11h4"
        stroke="currentColor"
        strokeWidth="1.4"
        strokeLinecap="round"
      />
    </svg>
  ),
  admin: (
    <svg className="w-4 h-4" viewBox="0 0 16 16" fill="none">
      <circle cx="8" cy="8" r="2.5" stroke="currentColor" strokeWidth="1.4" />
      <path
        d="M8 1.5v2M8 12.5v2M1.5 8h2M12.5 8h2M3.3 3.3l1.4 1.4M11.3 11.3l1.4 1.4M3.3 12.7l1.4-1.4M11.3 4.7l1.4-1.4"
        stroke="currentColor"
        strokeWidth="1.4"
        strokeLinecap="round"
      />
    </svg>
  ),
  topology: (
    <svg className="w-4 h-4" viewBox="0 0 16 16" fill="none">
      <circle cx="4" cy="4" r="2" stroke="currentColor" strokeWidth="1.4" />
      <circle cx="12" cy="4" r="2" stroke="currentColor" strokeWidth="1.4" />
      <circle cx="8" cy="12" r="2" stroke="currentColor" strokeWidth="1.4" />
      <path
        d="M5.6 5.4l1.5 5M10.4 5.4L8.9 10.4M6 4h4"
        stroke="currentColor"
        strokeWidth="1.4"
        strokeLinecap="round"
      />
    </svg>
  ),
  monitor: (
    <svg className="w-4 h-4" viewBox="0 0 16 16" fill="none">
      <rect
        x="1.5"
        y="3"
        width="13"
        height="9"
        rx="1.4"
        stroke="currentColor"
        strokeWidth="1.4"
      />
      <path
        d="M6 14h4M8 12v2"
        stroke="currentColor"
        strokeWidth="1.4"
        strokeLinecap="round"
      />
    </svg>
  ),
};

const NAV: NavGroup[] = [
  {
    label: "Observe",
    items: [
      {
        name: "Endpoints",
        path: "/Endpoints",
        matchPrefix: "/Endpoints",
        icon: Icon.endpoints,
      },
      {
        name: "Event Types",
        path: "/EventTypes",
        matchPrefix: "/EventTypes",
        icon: Icon.eventTypes,
      },
      {
        name: "Messages",
        path: "/Messages",
        matchPrefix: "/Message",
        icon: Icon.messages,
      },
      { name: "Metrics", path: "/Metrics", icon: Icon.metrics },
      {
        name: "Topology",
        path: "/Topology",
        icon: Icon.topology,
      },
      {
        name: "Monitor",
        path: "/Monitor",
        icon: Icon.monitor,
        badge: "wall",
      },
    ],
  },
  {
    label: "Diagnose",
    items: [
      { name: "Insights", path: "/Insights", icon: Icon.insights },
      { name: "Audit Log", path: "/Audits", icon: Icon.audit },
    ],
  },
  {
    label: "Manage",
    items: [{ name: "Admin", path: "/Admin", icon: Icon.admin }],
  },
];

// Logo mark — soft cloud silhouette + coral droplet (design system §01).
const LogoMark = ({ size = 22 }: { size?: number }) => (
  <svg
    width={size}
    height={size}
    viewBox="0 0 36 36"
    fill="none"
    className="inline-block"
    style={{ transform: "translateY(2px)" }}
  >
    <path
      d="M5 22c0-3.5 2.8-6.3 6.3-6.3.7 0 1.4.1 2 .3.8-3.7 4.1-6.5 8-6.5 4.2 0 7.6 3.2 8.1 7.2 2.7.4 4.8 2.7 4.8 5.5 0 3.1-2.5 5.5-5.5 5.5H11.3C7.8 27.7 5 25 5 22z"
      stroke="#F4F2EA"
      strokeWidth="2.5"
      strokeLinejoin="round"
    />
    <circle cx="29" cy="11" r="3" fill="#E8743C" />
  </svg>
);

const Sidebar = () => {
  const env = getEnv();

  return (
    <aside
      className={cn(
        "bg-[#1A1814] text-[#C9C1AB] flex flex-col gap-0.5 sticky top-0 h-screen",
        "px-3.5 pt-5 pb-5 border-r border-[#2A2620]",
        "w-[232px] shrink-0",
      )}
    >
      <a
        href="/Endpoints"
        className="flex items-baseline gap-1 px-2 pb-1.5 text-[#F4F2EA] font-extrabold text-xl tracking-tight no-underline hover:no-underline"
      >
        <LogoMark size={22} />
        <span className="ml-1">NimBus</span>
        <span className="text-primary">.</span>
      </a>
      <div className="px-2 pb-3.5 mb-2 border-b border-[#2A2620] font-mono text-[10px] text-[#6F6A5C] tracking-wider leading-tight">
        A nimbus for your Azure cloud.
        {env && (
          <span className="ml-1.5 inline-block align-middle bg-primary-tint text-primary-600 font-mono text-[9px] uppercase tracking-[0.14em] px-1.5 py-px rounded-nb-sm font-bold">
            {env}
          </span>
        )}
      </div>

      <div className="flex-1 overflow-y-auto">
        {NAV.map((group) => (
          <div key={group.label}>
            <div className="font-mono text-[9.5px] text-[#6F6A5C] tracking-widest uppercase mt-3.5 mb-1 mx-2.5">
              {group.label}
            </div>
            {group.items.map((item) => (
              <NavLink
                key={item.path}
                to={item.path}
                end={!item.matchPrefix}
                className={({ isActive }) => {
                  // For prefix-matched items, NavLink's default isActive
                  // already covers the prefix when `end` is false.
                  const active = isActive;
                  return cn(
                    "flex items-center gap-2.5 px-2.5 py-2 rounded-md text-[13px] font-medium no-underline",
                    "transition-colors duration-100",
                    active
                      ? "bg-primary/[0.16] text-primary"
                      : "text-[#C9C1AB] hover:bg-white/[0.04] hover:text-[#F4F2EA]",
                  );
                }}
              >
                <span className="opacity-90 shrink-0">{item.icon}</span>
                <span className="flex-1 truncate">{item.name}</span>
                {item.badge && (
                  <span
                    className={cn(
                      "ml-auto font-mono text-[10px] uppercase tracking-wider px-1.5 py-px rounded-full font-bold",
                      "bg-primary/[0.22] text-primary",
                    )}
                  >
                    {item.badge}
                  </span>
                )}
              </NavLink>
            ))}
          </div>
        ))}
      </div>
    </aside>
  );
};

export default Sidebar;
