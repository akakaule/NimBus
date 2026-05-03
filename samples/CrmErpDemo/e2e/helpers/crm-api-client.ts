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
}
