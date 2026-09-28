import type { ReactNode } from 'react';
import {
  makeStyles,
  mergeClasses,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  tokens,
} from '@fluentui/react-components';
import { DocumentText20Regular } from '@fluentui/react-icons';
import type { BcQuote, Opportunity } from '../../api';
import { QuoteStatusBadge, quoteStatusColor } from '../../components/StatusBadges';
import { ExternalLink } from '../../components/ui';
import { BC_CRM_OPPORTUNITIES_URL, bcQuoteUrl } from '../../config';
import { formatMoney } from '../../format';
import { useChangeHighlight } from '../../hooks/useHighlight';
import { isOpen, StateCode } from '../../model';

const ACCENTS: Record<string, string> = {
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
});

interface QuoteStatusStripProps {
  opportunity: Opportunity;
  /** The Business Central quotes linked to the opportunity (for the linked quote's total). */
  quotes: BcQuote[];
}

/**
 * Where the Business Central quote stands, updated live as the integration reports back. A
 * Business Central user makes the quote from the opportunity and links it; accepting it wins the deal.
 */
export function QuoteStatusStrip({ opportunity: o, quotes }: QuoteStatusStripProps) {
  const styles = useStyles();
  const status = o.csBcQuoteStatus;
  const won = o.stateCode === StateCode.WonOrQualified;
  const lost = o.stateCode === StateCode.LostOrDisqualified;
  const quote = quotes.find((q) => q.bcQuoteId === o.csBcQuoteId);
  const total = quote ? formatMoney(quote.totalAmount, quote.currencyCode) : undefined;
  const highlight = useChangeHighlight(`${status}|${o.csBcQuoteNumber}|${total}|${o.stateCode}`);

  let text: ReactNode;
  let link: ReactNode = null;
  if (o.csBcQuoteNumber) {
    text = (
      <>
        <span>
          Business Central quote <span className={styles.number}>{o.csBcQuoteNumber}</span> ·
        </span>
        {status && <QuoteStatusBadge status={status} />}
        {total && (
          <span data-testid="bc-quote-total">
            · total <span className={styles.number}>{total}</span>
          </span>
        )}
      </>
    );
    if (o.csBcQuoteId) link = <ExternalLink href={bcQuoteUrl(o.csBcQuoteId)}>Open in Business Central</ExternalLink>;
  } else if (isOpen(o)) {
    text = 'No quote yet. A Business Central user makes the quote from this opportunity and links it here.';
    link = (
      <ExternalLink href={BC_CRM_OPPORTUNITIES_URL} testId="open-bc-opportunities">
        Open CRM opportunities in Business Central
      </ExternalLink>
    );
  } else {
    text = 'No Business Central quote.';
  }

  const accent = ACCENTS[o.csBcQuoteNumber || status ? quoteStatusColor(status) : 'informative'];

  return (
    <>
      {won && (
        <MessageBar intent="success" className={styles.won} data-testid="won-banner">
          <MessageBarBody>
            <MessageBarTitle>
              {o.csBcQuoteNumber ? `Won — Business Central quote ${o.csBcQuoteNumber} accepted` : 'Won'}
            </MessageBarTitle>
            · actual revenue {formatMoney(o.actualValue)}
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
        <span className={styles.icon}>
          <DocumentText20Regular />
        </span>
        <span className={styles.text}>{text}</span>
        {link && <span className={styles.meta}>{link}</span>}
      </div>
    </>
  );
}
