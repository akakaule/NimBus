import { Route, Routes } from "react-router-dom";
import Sidebar from "components/sidebar";
import Topbar from "components/topbar";
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
  return (
    <ThemeProvider>
      <ToastProvider>
        <div className="flex min-h-screen bg-background">
          <Sidebar />
          <div className="flex flex-col flex-1 min-w-0">
            <Topbar />
            <main className="flex-1 flex flex-col min-w-0">
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
            </main>
            <Footer />
          </div>
        </div>
      </ToastProvider>
    </ThemeProvider>
  );
}

export default App;
