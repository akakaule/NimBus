import type { APIRequestContext } from "@playwright/test";
import { request } from "@playwright/test";
import { ServiceUrls } from "./service-urls.js";

export interface CrmAccount {
  id: string;
  legalName: string;
  taxId?: string | null;
  countryCode: string;
  erpCustomerId?: string | null;
  erpCustomerNumber?: string | null;
  origin: string;
  isDeleted: boolean;
  createdAt: string;
  updatedAt?: string | null;
}

export interface CreateCrmAccountRequest {
  legalName: string;
  taxId?: string | null;
  countryCode: string;
}

export interface CrmContact {
  id: string;
  accountId?: string | null;
  firstName: string;
  lastName: string;
  email?: string | null;
  phone?: string | null;
  origin: string;
  isDeleted: boolean;
  createdAt: string;
  updatedAt?: string | null;
}

export interface CreateCrmContactRequest {
  firstName: string;
  lastName: string;
  email?: string | null;
  phone?: string | null;
  accountId?: string | null;
}

export class CrmApiClient {
  private constructor(private readonly api: APIRequestContext) {}

  static async create(): Promise<CrmApiClient> {
    const api = await request.newContext({
      baseURL: ServiceUrls.crmApi,
      ignoreHTTPSErrors: true,
    });
    return new CrmApiClient(api);
  }

  async dispose(): Promise<void> {
    await this.api.dispose();
  }

  async createAccount(req: CreateCrmAccountRequest): Promise<CrmAccount> {
    const res = await this.api.post("/api/accounts", { data: req });
    if (!res.ok()) throw new Error(`CRM POST /api/accounts → ${res.status()} ${await res.text()}`);
    return (await res.json()) as CrmAccount;
  }

  /** Create a CRM contact. Publishes CrmContactCreated (SessionKey=ContactId). */
  async createContact(req: CreateCrmContactRequest): Promise<CrmContact> {
    const res = await this.api.post("/api/contacts", { data: req });
    if (!res.ok()) throw new Error(`CRM POST /api/contacts → ${res.status()} ${await res.text()}`);
    return (await res.json()) as CrmContact;
  }

  async getAccount(id: string): Promise<CrmAccount | null> {
    const res = await this.api.get(`/api/accounts/${id}`);
    if (res.status() === 404) return null;
    if (!res.ok()) throw new Error(`CRM GET /api/accounts/${id} → ${res.status()}`);
    return (await res.json()) as CrmAccount;
  }

  async listAccounts(): Promise<CrmAccount[]> {
    const res = await this.api.get("/api/accounts");
    if (!res.ok()) throw new Error(`CRM GET /api/accounts → ${res.status()}`);
    return (await res.json()) as CrmAccount[];
  }

  async updateAccount(id: string, req: CreateCrmAccountRequest): Promise<CrmAccount> {
    const res = await this.api.put(`/api/accounts/${id}`, { data: req });
    if (!res.ok()) throw new Error(`CRM PUT /api/accounts/${id} → ${res.status()}`);
    return (await res.json()) as CrmAccount;
  }

  async deleteAccount(id: string): Promise<void> {
    const res = await this.api.delete(`/api/accounts/${id}`);
    if (!res.ok() && res.status() !== 404) {
      throw new Error(`CRM DELETE /api/accounts/${id} → ${res.status()}`);
    }
  }

  /**
   * Request/reply showcase: synchronous ERP credit check. Returns the typed
   * result on 200; returns null on 504 (ERP did not reply within the timeout)
   * so timeout specs can assert on it without try/catch.
   */
  async creditCheck(accountId: string): Promise<CreditCheckResult | null> {
    const res = await this.api.post(`/api/accounts/${accountId}/credit-check`, {
      // The server-side request/reply timeout is 10s; leave headroom.
      timeout: 30_000,
    });
    if (res.status() === 504) return null;
    if (!res.ok()) throw new Error(`CRM POST credit-check → ${res.status()} ${await res.text()}`);
    return (await res.json()) as CreditCheckResult;
  }

  /** Command showcase: fire-and-forget PlaceCustomerOnCreditHold. */
  async placeCreditHold(accountId: string, reason?: string): Promise<void> {
    const res = await this.api.post(`/api/accounts/${accountId}/credit-hold`, {
      data: { reason: reason ?? null },
    });
    if (!res.ok()) throw new Error(`CRM POST credit-hold → ${res.status()} ${await res.text()}`);
  }

  /** Audit rows for one entity — used to prove inbox-skipped duplicates ran no handler. */
  async getAuditLog(entityType: "Account" | "Contact", entityId: string): Promise<unknown[]> {
    const res = await this.api.get(`/api/audit/${entityType}/${entityId}`);
    if (!res.ok()) throw new Error(`CRM GET audit → ${res.status()}`);
    return (await res.json()) as unknown[];
  }
}

export interface CreditCheckResult {
  accountId: string;
  approved: boolean;
  status: "Active" | "OnHold" | "NotFound" | "Deleted" | string;
  customerNumber?: string | null;
  checkedAt: string;
}
