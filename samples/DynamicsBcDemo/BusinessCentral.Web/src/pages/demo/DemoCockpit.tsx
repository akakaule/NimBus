import { Button, Link, makeStyles, mergeClasses, tokens } from '@fluentui/react-components';
import {
  Alert20Regular,
  ArrowRepeatAll20Regular,
  Broom20Regular,
  Flag20Regular,
  Open16Regular,
  PeopleTeam20Regular,
  PlugDisconnected20Regular,
  Server20Regular,
  WindowNew20Regular,
} from '@fluentui/react-icons';
import { useState, type ReactNode } from 'react';
import { demoApi, errorMessage } from '../../api';
import { LoadError } from '../../components/States';
import { D365_WEB_URL, NIMBUS_OPS_URL } from '../../config';
import { useDocumentTitle } from '../../hooks/useDocumentTitle';
import { usePolling } from '../../hooks/usePolling';
import { chrome } from '../../theme';
import { BurstPanel } from './BurstPanel';
import { CircuitPanel } from './CircuitPanel';
import { FaultControl } from './FaultControl';
import { InitialSyncPanel } from './InitialSyncPanel';
import { RedeliverPanel } from './RedeliverPanel';

const useStyles = makeStyles({
  page: {
    minHeight: '100vh',
    backgroundColor: '#EEF1F2',
    padding: `${tokens.spacingVerticalXL} ${tokens.spacingHorizontalXXL} ${tokens.spacingVerticalXXXL}`,
  },
  header: {
    display: 'flex',
    alignItems: 'flex-end',
    gap: tokens.spacingHorizontalL,
    marginBottom: tokens.spacingVerticalL,
    maxWidth: '1480px',
  },
  eyebrow: {
    color: chrome.brandText,
    fontSize: tokens.fontSizeBase200,
    fontWeight: tokens.fontWeightSemibold,
    textTransform: 'uppercase',
    letterSpacing: '0.06em',
  },
  title: {
    margin: 0,
    fontSize: tokens.fontSizeHero700,
    lineHeight: tokens.lineHeightHero700,
    fontWeight: tokens.fontWeightSemibold,
  },
  subtitle: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase300,
  },
  live: {
    marginLeft: 'auto',
    display: 'inline-flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
    whiteSpace: 'nowrap',
  },
  dot: {
    width: '10px',
    height: '10px',
    borderRadius: '50%',
  },
  dotOk: {
    backgroundColor: tokens.colorStatusSuccessBackground3,
  },
  dotDown: {
    backgroundColor: tokens.colorStatusDangerBackground3,
  },
  columns: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fit, minmax(460px, 1fr))',
    gap: tokens.spacingHorizontalL,
    alignItems: 'start',
    maxWidth: '1480px',
  },
  column: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalL,
    minWidth: 0,
  },
  section: {
    backgroundColor: tokens.colorNeutralBackground1,
    borderRadius: tokens.borderRadiusXLarge,
    boxShadow: tokens.shadow4,
    padding: `${tokens.spacingVerticalL} ${tokens.spacingHorizontalXL}`,
  },
  sectionTitle: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    margin: `0 0 ${tokens.spacingVerticalS}`,
    fontSize: tokens.fontSizeBase400,
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground1,
    '& svg': {
      color: tokens.colorBrandForeground1,
    },
  },
  alertsRow: {
    display: 'flex',
    alignItems: 'center',
    flexWrap: 'wrap',
    gap: tokens.spacingHorizontalL,
  },
  alertCount: {
    fontSize: '32px',
    lineHeight: '36px',
    fontWeight: tokens.fontWeightSemibold,
    fontVariantNumeric: 'tabular-nums',
  },
  alertLabel: {
    color: tokens.colorNeutralForeground2,
  },
  links: {
    margin: 0,
    padding: 0,
    listStyleType: 'none',
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fill, minmax(220px, 1fr))',
    gap: tokens.spacingVerticalS,
  },
  linkItem: {
    display: 'flex',
    flexDirection: 'column',
  },
  linkLabel: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
    fontWeight: tokens.fontWeightSemibold,
  },
  linkHint: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
  },
  error: {
    color: tokens.colorPaletteRedForeground1,
    fontSize: tokens.fontSizeBase200,
  },
});

function Section({ title, icon, children, testId }: { title: string; icon: ReactNode; children: ReactNode; testId: string }) {
  const styles = useStyles();
  return (
    <section className={styles.section} data-testid={testId}>
      <h2 className={styles.sectionTitle}>
        {icon}
        {title}
      </h2>
      {children}
    </section>
  );
}

const openLinks = [
  { label: 'nimbus-ops · Flow', hint: 'Live message flow', href: `${NIMBUS_OPS_URL}/Flow` },
  { label: 'nimbus-ops · Failed', hint: 'Failed messages, resubmit', href: `${NIMBUS_OPS_URL}/Failed` },
  { label: 'nimbus-ops · Endpoints', hint: 'Sessions per endpoint', href: `${NIMBUS_OPS_URL}/Endpoints` },
  { label: 'nimbus-ops · Event types', hint: 'The message catalog', href: `${NIMBUS_OPS_URL}/EventTypes` },
  { label: 'Sales Hub', hint: 'Dynamics 365 Sales (simulated)', href: D365_WEB_URL },
  { label: 'Business Central', hint: 'This client (simulated)', href: '/' },
  { label: '#integration-alerts', hint: 'Teams channel (simulated)', href: '/demo/alerts' },
];

/** Hidden presenter page (/demo): go-live sync, failure modes, burst, redelivery, circuit state, alerts and links. */
export default function DemoCockpit() {
  const styles = useStyles();
  useDocumentTitle('Demo cockpit · Contoso Subsea');
  const state = usePolling((signal) => demoApi.state(signal), 1000);
  const [clearing, setClearing] = useState(false);
  const [clearError, setClearError] = useState<string | null>(null);

  const faults = state.data?.faults;
  const connected = Boolean(state.data) && !state.error;

  const clearAlerts = async () => {
    setClearing(true);
    setClearError(null);
    try {
      await demoApi.clearAlerts();
      await state.refresh();
    } catch (e) {
      setClearError(errorMessage(e));
    } finally {
      setClearing(false);
    }
  };

  return (
    <div className={styles.page} data-testid="demo-cockpit">
      <header className={styles.header}>
        <div>
          <div className={styles.eyebrow}>NimBus demo · presenter only</div>
          <h1 className={styles.title}>Demo cockpit</h1>
          <div className={styles.subtitle}>
            Contoso Subsea — Dynamics 365 Sales (simulated) ⇄ NimBus ⇄ Business Central (simulated)
          </div>
        </div>
        <span className={styles.live} title={state.error?.message}>
          <span className={mergeClasses(styles.dot, connected ? styles.dotOk : styles.dotDown)} />
          {connected ? 'Live · updates every second' : 'Business Central API not reachable'}
        </span>
      </header>

      {state.error && !state.data && <LoadError error={state.error} onRetry={state.refresh} />}

      <div className={styles.columns}>
        <div className={styles.column}>
          <Section title="Go-live" icon={<Flag20Regular />} testId="section-golive">
            <InitialSyncPanel />
          </Section>

          <Section title="Business Central environment" icon={<Server20Regular />} testId="section-environment">
            <FaultControl
              kind="maintenance"
              title="Update window (503)"
              description="Business Central's APIs answer 503 Service Unavailable, like during a scheduled update. The BC screens keep working."
              window={faults?.maintenance}
              startLabel={(seconds) => (seconds ? `Start ${seconds} s update window` : 'Start update window')}
              onSet={demoApi.setMaintenance}
              onChanged={() => void state.refresh()}
              data-testid="start-maintenance"
            />
            <FaultControl
              kind="throttling"
              title="API throttling (429)"
              description="Business Central's APIs answer 429 Too Many Requests with Retry-After, like when the rate limit is exceeded."
              window={faults?.throttling}
              startLabel={(seconds) => (seconds ? `Start ${seconds} s throttling` : 'Start throttling')}
              onSet={demoApi.setThrottling}
              onChanged={() => void state.refresh()}
              data-testid="start-throttling"
            />
          </Section>

          <Section title="Pilot sales office" icon={<PeopleTeam20Regular />} testId="section-burst">
            <BurstPanel />
          </Section>

          <Section title="Alerts" icon={<Alert20Regular />} testId="section-alerts">
            <div className={styles.alertsRow}>
              <span className={styles.alertCount} data-testid="alert-count">
                {state.data?.alertCount ?? '–'}
              </span>
              <span className={styles.alertLabel}>
                {state.data?.alertCount === 1 ? 'alert' : 'alerts'} in #integration-alerts
              </span>
              <Button
                icon={<Broom20Regular />}
                onClick={clearAlerts}
                disabled={clearing || !state.data?.alertCount}
                data-testid="clear-alerts"
              >
                Clear alerts
              </Button>
              <Link href="/demo/alerts" target="_blank" rel="noopener noreferrer">
                Open #integration-alerts <Open16Regular aria-hidden />
              </Link>
            </div>
            {clearError && <div className={styles.error}>{clearError}</div>}
          </Section>
        </div>

        <div className={styles.column}>
          <Section title="NimBus · Business Central adapter circuit" icon={<PlugDisconnected20Regular />} testId="section-circuit">
            <CircuitPanel circuit={state.data?.circuit} />
          </Section>

          <Section title="Delivery" icon={<ArrowRepeatAll20Regular />} testId="section-delivery">
            <RedeliverPanel />
          </Section>

          <Section title="Open" icon={<WindowNew20Regular />} testId="section-links">
            <ul className={styles.links}>
              {openLinks.map((link) => (
                <li key={link.label} className={styles.linkItem}>
                  <Link href={link.href} target="_blank" rel="noopener noreferrer" className={styles.linkLabel}>
                    {link.label} <Open16Regular aria-hidden />
                  </Link>
                  <span className={styles.linkHint}>{link.hint}</span>
                </li>
              ))}
            </ul>
          </Section>
        </div>
      </div>
    </div>
  );
}
