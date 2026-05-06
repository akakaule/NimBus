import * as React from "react";
import moment from "moment";
import { Tooltip } from "components/ui/tooltip";
import * as api from "api-client";
import {
  EndpointStatus,
  getEndpointStatus,
  mapStatusToColor,
} from "functions/endpoint.functions";
import DataTable, { ITableRow, ITableHeadCell } from "components/data-table";
import Page from "components/page";
import { getApplicationStatus } from "hooks/app-status";
import EndpointPurgeButton from "components/endpoint-list/endpoint-purge-button";
import EndpointDisableButton from "components/endpoint-list/endpoint-disable-button";
import Cookies from "js-cookie";

// Status icons
const CheckCircleIcon = ({ className }: { className?: string }) => (
  <svg className={className} fill="currentColor" viewBox="0 0 24 24">
    <path
      fillRule="evenodd"
      d="M2.25 12c0-5.385 4.365-9.75 9.75-9.75s9.75 4.365 9.75 9.75-4.365 9.75-9.75 9.75S2.25 17.385 2.25 12zm13.36-1.814a.75.75 0 10-1.22-.872l-3.236 4.53L9.53 12.22a.75.75 0 00-1.06 1.06l2.25 2.25a.75.75 0 001.14-.094l3.75-5.25z"
      clipRule="evenodd"
    />
  </svg>
);

const NotAllowedIcon = ({ className }: { className?: string }) => (
  <svg className={className} fill="currentColor" viewBox="0 0 24 24">
    <path
      fillRule="evenodd"
      d="M12 2.25c-5.385 0-9.75 4.365-9.75 9.75s4.365 9.75 9.75 9.75 9.75-4.365 9.75-9.75S17.385 2.25 12 2.25zm-1.72 6.97a.75.75 0 10-1.06 1.06L10.94 12l-1.72 1.72a.75.75 0 101.06 1.06L12 13.06l1.72 1.72a.75.75 0 101.06-1.06L13.06 12l1.72-1.72a.75.75 0 10-1.06-1.06L12 10.94l-1.72-1.72z"
      clipRule="evenodd"
    />
  </svg>
);

const TimeIcon = ({ className }: { className?: string }) => (
  <svg className={className} fill="currentColor" viewBox="0 0 24 24">
    <path
      fillRule="evenodd"
      d="M12 2.25c-5.385 0-9.75 4.365-9.75 9.75s4.365 9.75 9.75 9.75 9.75-4.365 9.75-9.75S17.385 2.25 12 2.25zM12.75 6a.75.75 0 00-1.5 0v6c0 .414.336.75.75.75h4.5a.75.75 0 000-1.5h-3.75V6z"
      clipRule="evenodd"
    />
  </svg>
);

const WarningIcon = ({ className }: { className?: string }) => (
  <svg className={className} fill="currentColor" viewBox="0 0 24 24">
    <path
      fillRule="evenodd"
      d="M9.401 3.003c1.155-2 4.043-2 5.197 0l7.355 12.748c1.154 2-.29 4.5-2.599 4.5H4.645c-2.309 0-3.752-2.5-2.598-4.5L9.4 3.003zM12 8.25a.75.75 0 01.75.75v3.75a.75.75 0 01-1.5 0V9a.75.75 0 01.75-.75zm0 8.25a.75.75 0 100-1.5.75.75 0 000 1.5z"
      clipRule="evenodd"
    />
  </svg>
);

const WarningTwoIcon = ({ className }: { className?: string }) => (
  <svg className={className} fill="currentColor" viewBox="0 0 24 24">
    <path
      fillRule="evenodd"
      d="M2.25 12c0-5.385 4.365-9.75 9.75-9.75s9.75 4.365 9.75 9.75-4.365 9.75-9.75 9.75S2.25 17.385 2.25 12zM12 8.25a.75.75 0 01.75.75v3.75a.75.75 0 01-1.5 0V9a.75.75 0 01.75-.75zm0 8.25a.75.75 0 100-1.5.75.75 0 000 1.5z"
      clipRule="evenodd"
    />
  </svg>
);

type EndpointProps = {
  endpointIds?: string[];
  endpointStates?: api.EndpointStatus[];
};
type EndpointState = {
  endpointIds: string[];
  endpointStates: api.EndpointStatusCount[];
  metadatas: api.MetadataShort[];
  loading: boolean;
  checkedEndpoints: string[];
};

enum ITableData {
  name = "name",
  failed = "failed",
  deferred = "deferred",
  pending = "pending",
  lastUpdated = "lastUpdated",
  status = "status",
  subscriptionstatus = "subscription",
  actions = "actions",
}

export default class EndpointsList extends React.Component<
  EndpointProps,
  EndpointState
> {
  private client!: api.Client;
  private env: string | undefined;
  private cookieName = "endpointFilters";

  constructor(props: EndpointProps) {
    super(props);

    const mappedProps = this.props.endpointStates?.map((x) =>
      this.mapMomentInEndpointStatus(x),
    );

    this.state = {
      endpointIds: this.props.endpointIds ?? [],
      endpointStates: mappedProps ?? [],
      metadatas: [],
      loading: !mappedProps,
      checkedEndpoints: Cookies.get(this.cookieName)
        ? Cookies.get(this.cookieName)!.split(",")
        : [],
    };

    this.handleCheck = this.handleCheck.bind(this);
  }

  // componentDidMount gets called once we're client-side
  async componentDidMount() {
    const cookie = Cookies.get(this.cookieName)?.split(",");

    this.client = new api.Client(api.CookieAuth());
    // If we didn't rehydrate with endpoint states, call the api to get them
    const endPointIds = this.props.endpointIds
      ? this.props.endpointIds
      : await this.client.getEndpointsAll();
    let filteredEndpointIds: Array<string>;
    this.env = (await getApplicationStatus()).env;

    if (cookie) {
      filteredEndpointIds = cookie;
    } else {
      filteredEndpointIds = endPointIds;
    }

    const [endpoints, metadatas] = await Promise.all([
      this.client.postApiEndpointStatusCount(filteredEndpointIds),
      this.client.postApiMetadatashort(filteredEndpointIds),
    ]);

    for (const endpoint of endpoints) {
      this.state.endpointStates.push(this.mapMomentInEndpointStatus(endpoint));
    }

    for (const metadata of metadatas) {
      this.state.metadatas.push(metadata);
    }

    for (const endpointId of endPointIds) {
      this.state.endpointIds.push(endpointId);
    }

    this.setState({ ...this.state, loading: false });
  }

  async handleCheck(endpointId: string, newState: boolean) {
    let getAll: boolean = true;
    if (newState) {
      this.state.checkedEndpoints.push(endpointId);
      this.setState({ ...this.state });
    } else {
      let nCE = this.state.checkedEndpoints.filter((e) => e !== endpointId);
      let nCEs = this.state.endpointStates.filter(
        (e) => e.endpointId !== endpointId,
      );
      getAll = nCE.length !== 0;
      if (getAll) {
        this.setState({
          ...this.state,
          endpointStates: nCEs,
          checkedEndpoints: nCE,
        });
      } else {
        this.state.endpointStates.pop();
        this.state.checkedEndpoints.pop();
      }
    }

    if (getAll) {
      let loadedEndpointStates: string[] = [];
      for (const item of this.state.endpointStates) {
        loadedEndpointStates.push(item.endpointId!);
      }

      let endpointStatesToGet = this.state.checkedEndpoints.filter(
        (item) => loadedEndpointStates.indexOf(item) < 0,
      );
      let endpoints =
        await this.client.postApiEndpointStatusCount(endpointStatesToGet);

      for (const endpoint of endpoints) {
        this.state.endpointStates.push(
          this.mapMomentInEndpointStatus(endpoint),
        );
      }

      let newStates: api.EndpointStatusCount[] = [];
      for (const endpointId of this.state.checkedEndpoints) {
        const toPop = this.state.endpointStates.find(
          (es) => es.endpointId === endpointId,
        );
        if (toPop) {
          newStates.push(toPop);
        }
      }

      this.setState({ ...this.state, endpointStates: newStates });
    } else {
      this.setState({ ...this.state, loading: true });
      const endpoints = await this.client.postApiEndpointStatusCount(
        this.state.endpointIds,
      );

      for (const endpoint of endpoints) {
        this.state.endpointStates.push(
          this.mapMomentInEndpointStatus(endpoint),
        );
      }
      this.setState({ ...this.state, loading: false });
    }
  }

  stopLoading = (): void => {
    this.setState({ loading: false });
  };

  startLoading = (): void => {
    this.setState({ loading: true });
  };

  refreshEndpoint = async (endpointId: string): Promise<any> => {
    this.client = new api.Client(api.CookieAuth());

    const [endpoint, endpointMetadata] = await Promise.all([
      this.client.getEndpointStatusCountId(endpointId),
      this.client.getMetadataEndpoint(endpointId),
    ]);

    const mappedEndpoint = this.mapMomentInEndpointStatus(endpoint);
    const existingEndpoints = [...this.state.endpointStates];
    const existingMetadata = [...this.state.metadatas];

    const endpointIndex = existingEndpoints.findIndex(
      (e) => e.endpointId === endpointId,
    );
    const metadataIndex = existingMetadata.findIndex(
      (e) => e.endpointId === endpointId,
    );

    existingEndpoints[endpointIndex] = mappedEndpoint;
    existingMetadata[metadataIndex] = endpointMetadata;

    this.setState({
      endpointStates: [...existingEndpoints],
      metadatas: [...existingMetadata],
      loading: false,
    });
  };

  render() {
    return <Page title="Endpoints">{this.createTableNew()}</Page>;
  }

  transformStates(endpoints: api.EndpointStatus[]): api.EndpointStatus[] {
    const transformedStates = [...(endpoints ?? [])];
    transformedStates.forEach((x) => (x.eventTime = x.eventTime ?? moment()));
    return transformedStates;
  }

  mapMomentInEndpointStatus(endpointStatus: api.EndpointStatusCount) {
    return api.EndpointStatusCount.fromJS(endpointStatus);
  }

  createTableNew(): JSX.Element {
    const tableData = this.state.endpointStates?.map((x) =>
      this.mapEndpointStatusToTableRow(x),
    );

    const headCells: ITableHeadCell[] = [
      { id: ITableData.name, label: "Name", numeric: false },
      { id: ITableData.failed, label: "Failed", numeric: true },
      { id: ITableData.deferred, label: "Deferred", numeric: true },
      { id: ITableData.pending, label: "Pending", numeric: true },
      { id: ITableData.lastUpdated, label: "Last updated", numeric: false },
      { id: ITableData.status, label: "Status", numeric: false },
    ];

    return (
      <DataTable
        headCells={headCells}
        rows={tableData || []}
        noDataMessage="No endpoints available"
        isLoading={this.state.loading}
        orderBy={ITableData.name}
        styles={{ marginLeft: "-1rem", marginRight: "-1rem" }}
        dataRowsPerPage={20}
        count={tableData.length}
        endpointIds={this.state.endpointIds || []}
        checkedEndpointIds={this.state.checkedEndpoints}
        checked={this.handleCheck}
      />
    );
  }

  mapEndpointStatusToTableRow(
    endpointStatus: api.EndpointStatusCount,
  ): ITableRow {
    const endpointId = endpointStatus.endpointId || "";
    const failedEventsCount = endpointStatus.failedCount || 0;
    const deferredEventsCount = endpointStatus.deferredCount || 0;
    const pendingEventsCount = endpointStatus.pendingCount || 0;
    const lastUpdated = endpointStatus.eventTime?.fromNow() || "";
    const endpointStatusValue = getEndpointStatus(endpointStatus);
    const metadata =
      this.state.metadatas[
        this.state.metadatas.findIndex((e) => e.endpointId === endpointId)
      ] || undefined;

    return {
      id: endpointStatus.endpointId ?? "",
      route: `/Endpoints/Details/${endpointStatus.endpointId}`,
      data: new Map([
        [
          ITableData.name,
          {
            value: (
              <span className="text-delegate-blue-600 dark:text-delegate-blue-400 font-bold hover:underline">{endpointId}</span>
            ),
            searchValue: endpointId,
          },
        ],
        [
          ITableData.failed,
          {
            value: failedEventsCount,
            searchValue: failedEventsCount,
          },
        ],
        [
          ITableData.deferred,
          {
            value: deferredEventsCount,
            searchValue: deferredEventsCount,
          },
        ],
        [
          ITableData.pending,
          {
            value: pendingEventsCount,
            searchValue: pendingEventsCount,
          },
        ],
        [
          ITableData.lastUpdated,
          {
            value: lastUpdated,
            searchValue: lastUpdated,
          },
        ],
        [
          ITableData.status,
          {
            value: this.mapStatusToIcon(endpointStatusValue),
            searchValue: endpointStatusValue.toString(),
          },
        ],
      ]),
    };
  }

  buildPurgeButton = (endpointId: string): JSX.Element => {
    const isEnabled = !(this.env === "prod" || this.env === "stag");

    if (isEnabled)
      return (
        <EndpointPurgeButton
          refreshEndpoint={this.refreshEndpoint}
          stopLoading={this.stopLoading}
          startLoading={this.startLoading}
          endpointId={endpointId}
        />
      );
    else return <></>;
  };

  buildDisableButton = (endpointId: string, status: string): JSX.Element => {
    return (
      <EndpointDisableButton
        refreshEndpoint={this.refreshEndpoint}
        stopLoading={this.stopLoading}
        startLoading={this.startLoading}
        endpointId={endpointId}
        status={status}
      />
    );
  };

  mapStatusToIcon = (status: EndpointStatus): JSX.Element => {
    const colorClass = mapStatusToColor(status);
    let IconComponent: React.FC<{ className?: string }> = CheckCircleIcon;
    let colorTailwind = "text-green-500";

    if (status === EndpointStatus.Failed) {
      IconComponent = NotAllowedIcon;
      colorTailwind = "text-red-500";
    } else if (status === EndpointStatus.Impacted) {
      IconComponent = WarningTwoIcon;
      colorTailwind = "text-orange-500";
    } else if (status === EndpointStatus.Pending) {
      IconComponent = TimeIcon;
      colorTailwind = "text-yellow-500";
    } else if (status === EndpointStatus.Healthy) {
      IconComponent = CheckCircleIcon;
      colorTailwind = "text-green-500";
    } else if (status === EndpointStatus.Disabled) {
      IconComponent = WarningIcon;
      colorTailwind = "text-gray-500";
    } else if (status === EndpointStatus.MissingSubscription) {
      IconComponent = WarningIcon;
      colorTailwind = "text-slate-500";
    }

    return (
      <Tooltip content={status} position="top">
        <IconComponent className={`w-5 h-5 ${colorTailwind}`} />
      </Tooltip>
    );
  };
}
