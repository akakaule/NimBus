import { Route, Routes, useLocation } from "react-router-dom";
import Header from "components/header";
import EndpointDetails from "pages/endpoint-details";
import EventDetails from "pages/event-details";
import EndpointsList from "pages/endpoints-list";
import EventTypesList from "pages/event-types-list";
import EventTypeDetails from "pages/event-type-details";
import MessagesList from "pages/messages-list";
import Admin from "pages/admin";
import Metrics from "pages/metrics";
import Insights from "pages/insights";
import AuditsList from "pages/audits-list";
import Footer from "components/footer";
import { Navigation } from "models/navigation";
import { ToastProvider } from "components/ui/toast";
import { ThemeProvider } from "hooks/use-theme";

const EVENT_TYPES_ROUTE = "/EventTypes";

const navigation: Navigation = [
  {
    name: "EndpointDetails",
    path: "/Endpoints/Details/:id",
    header: false,
    render: () => <EndpointDetails />,
  },
  {
    name: "EventDetails",
    path: "/Message/Index/:endpointId/:id",
    header: false,
    render: () => <EventDetails />,
  },
  {
    name: "EventDetails",
    path: "/Message/Index/:endpointId/:id/:backindex",
    header: false,
    render: () => <EventDetails />,
  },
  {
    name: "Endpoints",
    path: "/Endpoints",
    header: true,
    render: () => <EndpointsList />,
  },
  {
    name: "Event Types",
    path: "/EventTypes",
    header: true,
    render: () => <EventTypesList />,
  },
  {
    name: "EventTypeDetails",
    path: "/EventTypes/Details/:id",
    header: false,
    render: () => <EventTypeDetails />,
  },
  {
    name: "Messages",
    path: "/Messages",
    header: true,
    render: () => <MessagesList />,
  },
  {
    name: "Metrics",
    path: "/Metrics",
    header: true,
    render: () => <Metrics />,
  },
  {
    name: "Insights",
    path: "/Insights",
    header: true,
    render: () => <Insights />,
  },
  {
    name: "Audit Log",
    path: "/Audits",
    header: true,
    render: () => <AuditsList />,
  },
  {
    name: "Admin",
    path: "/Admin",
    header: true,
    render: () => <Admin />,
  },
  {
    name: "Front",
    path: "/",
    header: false,
    render: () => <EndpointsList />,
  },
];

function App() {
  const location = useLocation();
  const isEventTypes = location.pathname === EVENT_TYPES_ROUTE;

  return (
    <ThemeProvider>
      <ToastProvider>
        <div className="flex flex-col min-h-screen">
          <Header links={navigation.filter((x) => x.header)} />

          <Routes>
            {navigation
              .filter((x) => x.render)
              .map((route) => (
                <Route
                  key={route.path}
                  path={route.path}
                  element={route.render!()}
                />
              ))}
          </Routes>

          {!isEventTypes && <Footer />}
        </div>
      </ToastProvider>
    </ThemeProvider>
  );
}

export default App;
