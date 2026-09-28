import type { Account } from './api';

// Dataverse option-set values the simulator uses (D365Sales.Api Data/Entities.cs, OptionSets).

export const RelationshipType = { Customer: 3, Prospect: 8 } as const;
export const MasterDataOwner = { Dynamics365: 1, BusinessCentral: 2 } as const;
export const StateCode = { Open: 0, WonOrQualified: 1, LostOrDisqualified: 2 } as const;

/** The opportunity business process flow. */
export const STAGES = ['1-Qualify', '2-Develop', '3-Propose', '4-Close'] as const;
export type StageName = (typeof STAGES)[number];

export const isStage = (value: string): value is StageName => (STAGES as readonly string[]).includes(value);

/** "2-Develop" → "Develop" */
export const stageLabel = (stepName: string) => stepName.replace(/^\d+-/, '');

export const relationshipLabel = (code: number) =>
  code === RelationshipType.Customer ? 'Customer' : code === RelationshipType.Prospect ? 'Prospect' : 'Other';

export const isBcOwned = (account: Pick<Account, 'csMasterDataOwner'>) =>
  account.csMasterDataOwner === MasterDataOwner.BusinessCentral;

/** A prospect with a Business Central quote: Business Central manages its master data from the first quote on. */
export const isBcManagedProspect = (account: Pick<Account, 'csMasterDataOwner' | 'customerTypeCode'>) =>
  isBcOwned(account) && account.customerTypeCode === RelationshipType.Prospect;

export const isOpen = (record: { stateCode: number }) => record.stateCode === StateCode.Open;

export const opportunityStatusLabel = (stateCode: number) =>
  stateCode === StateCode.WonOrQualified ? 'Won' : stateCode === StateCode.LostOrDisqualified ? 'Lost' : 'Open';

export const leadStatusLabel = (stateCode: number) =>
  stateCode === StateCode.WonOrQualified ? 'Qualified' : stateCode === StateCode.LostOrDisqualified ? 'Disqualified' : 'Open';
