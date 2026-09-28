import { Badge, type BadgeProps } from '@fluentui/react-components';
import type { BcQuoteStatus } from '../api';
import { leadStatusLabel, opportunityStatusLabel, relationshipLabel, RelationshipType, StateCode } from '../model';

type BadgeColor = NonNullable<BadgeProps['color']>;

const stateColor = (stateCode: number): BadgeColor =>
  stateCode === StateCode.WonOrQualified ? 'success' : stateCode === StateCode.LostOrDisqualified ? 'danger' : 'informative';

export function OpportunityStatusBadge({ stateCode }: { stateCode: number }) {
  return (
    <Badge appearance="tint" color={stateColor(stateCode)} shape="rounded">
      {opportunityStatusLabel(stateCode)}
    </Badge>
  );
}

export function LeadStatusBadge({ stateCode }: { stateCode: number }) {
  return (
    <Badge
      appearance="tint"
      color={stateCode === StateCode.LostOrDisqualified ? 'subtle' : stateColor(stateCode)}
      shape="rounded"
    >
      {leadStatusLabel(stateCode)}
    </Badge>
  );
}

export function RelationshipBadge({ code, testId }: { code: number; testId?: string }) {
  return (
    <Badge
      appearance="tint"
      color={code === RelationshipType.Customer ? 'success' : 'brand'}
      shape="rounded"
      data-testid={testId}
    >
      {relationshipLabel(code)}
    </Badge>
  );
}

export const quoteStatusColor = (status: string | null | undefined): BadgeColor => {
  switch (status as BcQuoteStatus | null | undefined) {
    case 'Requested':
      return 'warning';
    case 'Sent':
      return 'brand';
    case 'Accepted':
      return 'success';
    case 'Expired':
      return 'danger';
    default:
      return 'informative';
  }
};

export function QuoteStatusBadge({ status }: { status: string }) {
  return (
    <Badge appearance="tint" color={quoteStatusColor(status)} shape="rounded">
      {status}
    </Badge>
  );
}

/** Business Central's customer.blocked: blank means not blocked. */
export function BlockedBadge({ blocked }: { blocked: string | null | undefined }) {
  if (!blocked || !blocked.trim()) return <>Not blocked</>;
  return (
    <Badge appearance="tint" color="warning" shape="rounded">
      Blocked: {blocked}
    </Badge>
  );
}
