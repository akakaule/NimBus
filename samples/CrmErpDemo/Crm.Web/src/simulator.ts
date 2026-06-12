// Browser-side "Simulate mode" engine. When running, it mimics real users
// working in the CRM by performing randomized create/update/delete activity on
// accounts and contacts through the same /api calls the UI uses — which publish
// CrmAccount*/CrmContact* events to the Service Bus. Up to MAX_WORKERS actions
// run in parallel, with a random 500ms–2s gap between each worker's actions.

import { useSyncExternalStore } from 'react';
import { api } from './api';
import { randomCompany, randomPerson, randomPick } from './fakeData';

const MIN_DELAY = 500;
const MAX_DELAY = 2000;
const MAX_WORKERS = 2;

export interface SimStats {
  running: boolean;
  actions: number;
  lastAction: string | null;
}

type ActionKind =
  | 'account.create'
  | 'account.update'
  | 'account.delete'
  | 'contact.create'
  | 'contact.update'
  | 'contact.delete';

// Biased toward creates so the dataset stays populated; deletes stay occasional.
const WEIGHTS: ReadonlyArray<readonly [ActionKind, number]> = [
  ['account.create', 25],
  ['account.update', 20],
  ['account.delete', 10],
  ['contact.create', 25],
  ['contact.update', 15],
  ['contact.delete', 5],
];

/** Pure weighted pick — exported so the distribution is unit-testable. */
export function pickWeighted(
  weights: ReadonlyArray<readonly [ActionKind, number]>,
  rnd: number = Math.random(),
): ActionKind {
  const total = weights.reduce((sum, [, w]) => sum + w, 0);
  let r = rnd * total;
  for (const [kind, w] of weights) {
    if (r < w) return kind;
    r -= w;
  }
  return weights[weights.length - 1][0];
}

function sleep(ms: number): Promise<void> {
  return new Promise(resolve => setTimeout(resolve, ms));
}

class Simulator {
  private running = false;
  private actions = 0;
  private lastAction: string | null = null;
  // Bumped on every start() so workers from a previous run exit instead of
  // doubling up if the toggle is flipped off then on again quickly.
  private generation = 0;
  private listeners = new Set<() => void>();
  private snapshot: SimStats = { running: false, actions: 0, lastAction: null };

  // Stable identities required by useSyncExternalStore.
  subscribe = (cb: () => void): (() => void) => {
    this.listeners.add(cb);
    return () => {
      this.listeners.delete(cb);
    };
  };

  getSnapshot = (): SimStats => this.snapshot;

  toggle(): void {
    if (this.running) this.stop();
    else this.start();
  }

  start(): void {
    if (this.running) return;
    this.running = true;
    this.generation += 1;
    const gen = this.generation;
    this.emit();
    for (let i = 0; i < MAX_WORKERS; i++) void this.worker(gen);
  }

  stop(): void {
    if (!this.running) return;
    this.running = false;
    this.emit();
  }

  private emit(): void {
    this.snapshot = { running: this.running, actions: this.actions, lastAction: this.lastAction };
    this.listeners.forEach(l => l());
  }

  private async worker(gen: number): Promise<void> {
    while (this.running && this.generation === gen) {
      try {
        this.lastAction = await this.act();
        this.actions += 1;
        this.emit();
      } catch {
        // Swallow transient failures (e.g. two workers racing to delete the
        // same row → 404) so the loop keeps simulating.
      }
      await sleep(MIN_DELAY + Math.random() * (MAX_DELAY - MIN_DELAY));
    }
  }

  private act(): Promise<string> {
    switch (pickWeighted(WEIGHTS)) {
      case 'account.create': return this.createAccount();
      case 'account.update': return this.updateAccount();
      case 'account.delete': return this.deleteAccount();
      case 'contact.create': return this.createContact();
      case 'contact.update': return this.updateContact();
      case 'contact.delete': return this.deleteContact();
    }
  }

  private async createAccount(): Promise<string> {
    const a = await api.createAccount(randomCompany());
    return `Created account ${a.legalName}`;
  }

  private async updateAccount(): Promise<string> {
    const target = randomPick((await api.listAccounts()).filter(a => !a.isDeleted));
    if (!target) return this.createAccount();
    const a = await api.updateAccount(target.id, randomCompany());
    return `Updated account ${a.legalName}`;
  }

  private async deleteAccount(): Promise<string> {
    const target = randomPick((await api.listAccounts()).filter(a => !a.isDeleted));
    if (!target) return this.createAccount();
    await api.deleteAccount(target.id);
    return `Deleted account ${target.legalName}`;
  }

  private async createContact(): Promise<string> {
    const accounts = (await api.listAccounts()).filter(a => !a.isDeleted);
    // ~70% of contacts belong to an existing account; the rest are unlinked.
    const account = Math.random() < 0.7 ? randomPick(accounts) : undefined;
    const c = await api.createContact({ ...randomPerson(), accountId: account?.id ?? null });
    return `Created contact ${c.firstName} ${c.lastName}`;
  }

  private async updateContact(): Promise<string> {
    const target = randomPick((await api.listContacts()).filter(c => !c.isDeleted));
    if (!target) return this.createContact();
    const c = await api.updateContact(target.id, { ...randomPerson(), accountId: target.accountId ?? null });
    return `Updated contact ${c.firstName} ${c.lastName}`;
  }

  private async deleteContact(): Promise<string> {
    const target = randomPick((await api.listContacts()).filter(c => !c.isDeleted));
    if (!target) return this.createContact();
    await api.deleteContact(target.id);
    return `Deleted contact ${target.firstName} ${target.lastName}`;
  }
}

export const simulator = new Simulator();

/** React binding so the header button reflects sim state across route changes. */
export function useSimulator(): SimStats {
  return useSyncExternalStore(simulator.subscribe, simulator.getSnapshot);
}
