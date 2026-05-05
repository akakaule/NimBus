import type { APIRequestContext } from "@playwright/test";
import { request } from "@playwright/test";
import { ServiceUrls } from "./service-urls.js";

export interface ErpCustomer {
  id: string;
  customerNumber: string;
  legalName: string;
  taxId?: string | null;
  countryCode: string;
  crmAccountId?: string | null;
  origin: string;
  isDeleted: boolean;
  createdAt: string;
  updatedAt?: string | null;
}

export interface CreateErpCustomerRequest {
  legalName: string;
  taxId?: string | null;
  countryCode: string;
  customerNumber?: string | null;
}

export interface ServiceModeState {
  enabled: boolean;
  changedAt: string;
}

export interface HandoffModeRequest {
  enabled: boolean;
  durationSeconds: number;
  failureRate: number;
}

export interface HandoffModeState {
  enabled: boolean;
  durationSeconds: number;
  failureRate: number;
  changedAt: string;
}

export interface HandoffJob {
  eventId: string;
  sessionId: string;
  messageId: string;
  originatingMessageId?: string | null;
  eventTypeId: string;
  correlationId?: string | null;
  externalJobId: string;
  dueAt: string;
  payloadJson: string;
  registeredAt: string;
}

export class ErpApiClient {
  private constructor(private readonly api: APIRequestContext) {}

  static async create(): Promise<ErpApiClient> {
    const api = await request.newContext({
      baseURL: ServiceUrls.erpApi,
      ignoreHTTPSErrors: true,
    });
    return new ErpApiClient(api);
  }

  async dispose(): Promise<void> {
    await this.api.dispose();
  }

  // ─── Customers ────────────────────────────────────────────────

  async createCustomer(req: CreateErpCustomerRequest): Promise<ErpCustomer> {
    const res = await this.api.post("/api/customers", { data: req });
    if (!res.ok()) throw new Error(`ERP POST /api/customers → ${res.status()} ${await res.text()}`);
    return (await res.json()) as ErpCustomer;
  }

  async getCustomer(id: string): Promise<ErpCustomer | null> {
    const res = await this.api.get(`/api/customers/${id}`);
    if (res.status() === 404) return null;
    if (!res.ok()) throw new Error(`ERP GET /api/customers/${id} → ${res.status()}`);
    return (await res.json()) as ErpCustomer;
  }

  async listCustomers(): Promise<ErpCustomer[]> {
    const res = await this.api.get("/api/customers");
    if (!res.ok()) throw new Error(`ERP GET /api/customers → ${res.status()}`);
    return (await res.json()) as ErpCustomer[];
  }

  async findCustomerByCrmAccountId(crmAccountId: string): Promise<ErpCustomer | null> {
    const all = await this.listCustomers();
    return all.find((c) => c.crmAccountId === crmAccountId && !c.isDeleted) ?? null;
  }

  async updateCustomer(id: string, req: { legalName: string; taxId?: string | null; countryCode: string }): Promise<ErpCustomer> {
    const res = await this.api.put(`/api/customers/${id}`, { data: req });
    if (!res.ok()) throw new Error(`ERP PUT /api/customers/${id} → ${res.status()}`);
    return (await res.json()) as ErpCustomer;
  }

  // ─── Failure-mode toggles ─────────────────────────────────────

  async getServiceMode(): Promise<ServiceModeState> {
    const res = await this.api.get("/api/admin/service-mode");
    if (!res.ok()) throw new Error(`ERP GET service-mode → ${res.status()}`);
    return (await res.json()) as ServiceModeState;
  }

  async setServiceMode(enabled: boolean): Promise<ServiceModeState> {
    const res = await this.api.put("/api/admin/service-mode", { data: { enabled } });
    if (!res.ok()) throw new Error(`ERP PUT service-mode → ${res.status()}`);
    return (await res.json()) as ServiceModeState;
  }

  async getErrorMode(): Promise<ServiceModeState> {
    const res = await this.api.get("/api/admin/error-mode");
    if (!res.ok()) throw new Error(`ERP GET error-mode → ${res.status()}`);
    return (await res.json()) as ServiceModeState;
  }

  async setErrorMode(enabled: boolean): Promise<ServiceModeState> {
    const res = await this.api.put("/api/admin/error-mode", { data: { enabled } });
    if (!res.ok()) throw new Error(`ERP PUT error-mode → ${res.status()}`);
    return (await res.json()) as ServiceModeState;
  }

  /** Restore both failure modes to off; safe to call from afterEach hooks. */
  async resetFailureModes(): Promise<void> {
    await this.setErrorMode(false);
    await this.setServiceMode(false);
  }

  // ─── Handoff mode (PendingHandoff showcase) ───────────────────

  async getHandoffMode(): Promise<HandoffModeState> {
    const res = await this.api.get("/api/admin/handoff-mode");
    if (!res.ok()) throw new Error(`ERP GET handoff-mode → ${res.status()}`);
    return (await res.json()) as HandoffModeState;
  }

  async setHandoffMode(req: HandoffModeRequest): Promise<HandoffModeState> {
    const res = await this.api.put("/api/admin/handoff-mode", { data: req });
    if (!res.ok()) throw new Error(`ERP PUT handoff-mode → ${res.status()} ${await res.text()}`);
    return (await res.json()) as HandoffModeState;
  }

  /** Disable handoff mode with sensible defaults; safe to call from afterEach hooks. */
  async resetHandoffMode(): Promise<void> {
    await this.setHandoffMode({ enabled: false, durationSeconds: 10, failureRate: 0 });
  }

  async getHandoffJobs(): Promise<HandoffJob[]> {
    const res = await this.api.get("/api/internal/handoff-jobs");
    if (!res.ok()) throw new Error(`ERP GET handoff-jobs → ${res.status()}`);
    return (await res.json()) as HandoffJob[];
  }
}
