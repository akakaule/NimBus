import type { ReactNode } from 'react';
import {
  makeStyles,
  mergeClasses,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Spinner,
  tokens,
} from '@fluentui/react-components';
import { DocumentText20Regular, Warning16Filled } from '@fluentui/react-icons';
import type { Opportunity } from '../../api';
import { QuoteStatusBadge, quoteStatusColor } from '../../components/StatusBadges';
import { ExternalLink } from '../../components/ui';
import { bcQuoteUrl } from '../../config';
import { formatDateTime, formatMoney } from '../../format';
import { useChangeHighlight } from '../../hooks/useHighlight';
import { isOpen, linesChangedSinceRequest, StateCode } from '../../model';

const ACCENTS: Record<string, string> = {
  warning: tokens.colorPaletteMarigoldBorderActive,
  brand: tokens.colorBrandStroke1,
  success: tokens.colorPaletteGreenBorderActive,
  danger: tokens.colorPaletteRedBorderActive,
  informative: tokens.colorNeutralStroke1,
};

const useStyles = makeStyles({
  won: {
    marginBottom: '8px',
  },
  strip: {
    display: 'flex',
    alignItems: 'center',
    flexWrap: 'wrap',
    columnGap: '12px',
    rowGap: '6px',
    minHeight: '44px',
    padding: '6px 16px',
    marginBottom: '12px',
    backgroundColor: tokens.colorNeutralBackground1,
    borderRadius: tokens.borderRadiusXLarge,
    boxShadow: tokens.shadow2,
    borderLeftWidth: '4px',
    borderLeftStyle: 'solid',
  },
  highlight: {
    animationName: {
      from: { backgroundColor: tokens.colorBrandBackground2 },
      to: { backgroundColor: tokens.colorNeutralBackground1 },
    },
    animationDuration: '2.6s',
    animationTimingFunction: 'ease-out',
    '@media (prefers-reduced-motion: reduce)': {
      animationName: 'none',
    },
  },
  icon: {
    display: 'flex',
    color: tokens.colorNeutralForeground3,
  },
  text: {
    display: 'inline-flex',
    alignItems: 'center',
    flexWrap: 'wrap',
    gap: '8px',
    fontSize: tokens.fontSizeBase300,
    color: tokens.colorNeutralForeground1,
  },
  number: {
    fontWeight: tokens.fontWeightSemibold,
  },
  meta: {
    marginLeft: 'auto',
    display: 'inline-flex',
    alignItems: 'center',
    flexWrap: 'wrap',
    gap: '16px',
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
  },
  changed: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '4px',
    color: tokens.colorPaletteMarigoldForeground1,
    fontWeight: tokens.fontWeightSemibold,
  },
});

interface QuoteStatusStripProps {
  opportunity: Opportunity;
}

/** Where the Business Central quote stands, updated live as the integration reports back. */
export function QuoteStatusStrip({ opportunity: o }: QuoteStatusStripProps) {
  const styles = useStyles();
  const status = o.csBcQuoteStatus;
  const won = o.stateCode === StateCode.WonOrQualified;
  const lost = o.stateCode === StateCode.LostOrDisqualified;
  const highlight = useChangeHighlight(`${status}|${o.csBcQuoteNumber}|${o.csBcOrderNumber}|${o.stateCode}`);
  const changedLines = isOpen(o) && linesChangedSinceRequest(o);

  let icon: ReactNode = <DocumentText20Regular />;
  let text: ReactNode;
  if (status === 'Requested' && !o.csBcQuoteNumber) {
    icon = <Spinner size="extra-tiny" aria-label="Waiting for Business Central" />;
    text = (
      <>
        Quote requested… Business Central is creating it.
        <QuoteStatusBadge status="Requested" />
      </>
    );
  } else if (o.csBcQuoteNumber) {
    text = (
      <>
        <span>
          Business Central quote <span className={styles.number}>{o.csBcQuoteNumber}</span> ·
        </span>
        {status && <QuoteStatusBadge status={status} />}
        {status === 'Accepted' && o.csBcOrderNumber && (
          <span>
            — order <span className={styles.number}>{o.csBcOrderNumber}</span>
          </span>
        )}
      </>
    );
  } else if (!isOpen(o)) {
    text = 'No Business Central quote.';
  } else if (o.lines.length === 0) {
    text = 'No quote yet. Add product lines, then request a quote — quotes are made in Business Central.';
  } else {
    text = 'No quote yet. Request one — Business Central makes the quote and reports back here.';
  }

  const accent = ACCENTS[o.csBcQuoteNumber || status ? quoteStatusColor(status) : 'informative'];

  return (
    <>
      {won && (
        <MessageBar intent="success" className={styles.won} data-testid="won-banner">
          <MessageBarBody>
            {o.csBcOrderNumber ? (
              <>
                <MessageBarTitle>Won from Business Central order {o.csBcOrderNumber}</MessageBarTitle>· actual revenue{' '}
                {formatMoney(o.actualValue)}
              </>
            ) : (
              <>
                <MessageBarTitle>Won</MessageBarTitle>· actual revenue {formatMoney(o.actualValue)}
              </>
            )}
          </MessageBarBody>
        </MessageBar>
      )}
      {lost && (
        <MessageBar intent="warning" className={styles.won}>
          <MessageBarBody>This opportunity was closed as lost.</MessageBarBody>
        </MessageBar>
      )}
      <div
        className={mergeClasses(styles.strip, highlight && styles.highlight)}
        style={{ borderLeftColor: accent }}
        data-testid="bc-quote-status"
        data-status={status ?? 'None'}
        role="status"
      >
        <span className={styles.icon}>{icon}</span>
        <span className={styles.text}>{text}</span>
        <span className={styles.meta}>
          {changedLines && (
            <span className={styles.changed} data-testid="lines-changed">
              <Warning16Filled />
              Lines changed after the last request — request the quote again (revision {o.csLinesRevision})
            </span>
          )}
          {o.csQuoteRequestedOn && (
            <span>
              Requested {formatDateTime(o.csQuoteRequestedOn)} · revision {o.csQuoteRequestRevision}
            </span>
          )}
          {o.csBcQuoteId && <ExternalLink href={bcQuoteUrl(o.csBcQuoteId)}>Open in Business Central</ExternalLink>}
        </span>
      </div>
    </>
  );
}
