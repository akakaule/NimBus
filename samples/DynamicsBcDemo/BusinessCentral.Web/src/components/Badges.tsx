import { Badge, type BadgeProps } from '@fluentui/react-components';

const quoteStatusColors: Record<string, BadgeProps['color']> = {
  Draft: 'informative',
  Sent: 'brand',
  Accepted: 'success',
  Expired: 'warning',
};

interface QuoteStatusBadgeProps {
  status: string;
  size?: BadgeProps['size'];
  'data-testid'?: string;
}

/** Draft neutral, Sent teal, Accepted green, Expired amber. */
export function QuoteStatusBadge({ status, size = 'medium', 'data-testid': testId }: QuoteStatusBadgeProps) {
  return (
    <Badge appearance="tint" shape="rounded" size={size} color={quoteStatusColors[status] ?? 'informative'} data-testid={testId}>
      {status}
    </Badge>
  );
}

const blockedDescriptions: Record<string, string> = {
  Ship: 'Blocked for shipping',
  Invoice: 'Blocked for invoicing and shipping',
  All: 'Blocked for all transactions',
};

const blockedColors: Record<string, BadgeProps['color']> = {
  Ship: 'warning',
  Invoice: 'severe',
  All: 'danger',
};

/** The customer's Blocked value; renders nothing when the customer isn't blocked. */
export function BlockedBadge({ blocked }: { blocked: string }) {
  if (!blocked) return null;
  return (
    <Badge
      appearance="tint"
      shape="rounded"
      color={blockedColors[blocked] ?? 'danger'}
      title={blockedDescriptions[blocked] ?? 'Blocked'}
    >
      Blocked: {blocked}
    </Badge>
  );
}

/** A contact BC quotes but that hasn't bought yet. */
export function ProspectTag() {
  return (
    <Badge appearance="outline" shape="rounded" size="small" color="brand">
      Prospect
    </Badge>
  );
}

const opportunityStatusColors: Record<string, BadgeProps['color']> = {
  Open: 'brand',
  Won: 'success',
  Lost: 'informative',
};

/** CRM opportunity status: Open teal, Won green, Lost neutral. */
export function OpportunityStatusBadge({ status }: { status: string }) {
  return (
    <Badge appearance="tint" shape="rounded" color={opportunityStatusColors[status] ?? 'informative'}>
      {status}
    </Badge>
  );
}

/** Sales order status (the simulator creates orders as Open). */
export function OrderStatusBadge({ status }: { status: string }) {
  return (
    <Badge appearance="tint" shape="rounded" color={status === 'Open' ? 'brand' : 'informative'}>
      {status}
    </Badge>
  );
}
