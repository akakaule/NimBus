export interface Customer {
  id: string;
  customerNumber: string;
  legalName: string;
  taxId?: string | null;
  countryCode: string;
  crmAccountId?: string | null;
  createdAt: string;
  updatedAt?: string | null;
  origin?: string;
  isDeleted?: boolean;
}

export interface Contact {
  id: string;
  customerId?: string | null;
  firstName: string;
  lastName: string;
  email?: string | null;
  phone?: string | null;
  createdAt: string;
  updatedAt?: string | null;
  origin?: string;
  isDeleted?: boolean;
}

async function json<T>(res: Response): Promise<T> {
  if (!res.ok) {
    const body = await res.text().catch(() => '');
    throw new Error(`${res.status} ${res.statusText}${body ? ` — ${body.slice(0, 500)}` : ''}`);
  }
  return res.json() as Promise<T>;
}

export const api = {
  listCustomers: () => fetch('/api/customers').then(json<Customer[]>),
  getCustomer: (id: string) => fetch(`/api/customers/${id}`).then(json<Customer>),
  createCustomer: (c: Partial<Customer>) =>
    fetch('/api/customers', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(c),
    }).then(json<Customer>),
  updateCustomer: (id: string, c: Partial<Customer>) =>
    fetch(`/api/customers/${id}`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(c),
    }).then(json<Customer>),
  deleteCustomer: (id: string) =>
    fetch(`/api/customers/${id}`, { method: 'DELETE' }).then(json<Customer>),

  listContacts: () => fetch('/api/contacts').then(json<Contact[]>),
  getContact: (id: string) => fetch(`/api/contacts/${id}`).then(json<Contact>),
  createContact: (c: Partial<Contact>) =>
    fetch('/api/contacts', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(c),
    }).then(json<Contact>),
  updateContact: (id: string, c: Partial<Contact>) =>
    fetch(`/api/contacts/${id}`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(c),
    }).then(json<Contact>),
  deleteContact: (id: string) =>
    fetch(`/api/contacts/${id}`, { method: 'DELETE' }).then(json<Contact>),
};
