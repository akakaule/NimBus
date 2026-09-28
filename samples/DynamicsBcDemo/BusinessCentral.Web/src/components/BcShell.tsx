import { Avatar, makeStyles, mergeClasses, tokens } from '@fluentui/react-components';
import { GridDots20Regular } from '@fluentui/react-icons';
import { NavLink, Outlet } from 'react-router-dom';
import { api } from '../api';
import { usePolling } from '../hooks/usePolling';
import { chrome } from '../theme';
import { EnvironmentBanner } from './EnvironmentBanner';

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    minHeight: '100vh',
    backgroundColor: chrome.pageBackground,
  },
  header: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalM,
    height: '48px',
    padding: `0 ${tokens.spacingHorizontalL}`,
    backgroundColor: chrome.headerBackground,
    color: chrome.headerForeground,
    flexShrink: 0,
  },
  waffle: {
    color: chrome.headerMuted,
    flexShrink: 0,
  },
  product: {
    fontSize: tokens.fontSizeBase400,
    fontWeight: tokens.fontWeightSemibold,
    whiteSpace: 'nowrap',
  },
  simulated: {
    marginLeft: `calc(${tokens.spacingHorizontalXS} * -1)`,
    fontSize: tokens.fontSizeBase200,
    color: chrome.headerMuted,
    whiteSpace: 'nowrap',
  },
  headerRight: {
    marginLeft: 'auto',
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalL,
    minWidth: 0,
  },
  environment: {
    fontSize: tokens.fontSizeBase200,
    color: chrome.headerForeground,
    backgroundColor: 'rgba(255, 255, 255, 0.12)',
    borderRadius: tokens.borderRadiusMedium,
    padding: `2px ${tokens.spacingHorizontalS}`,
    whiteSpace: 'nowrap',
  },
  company: {
    fontSize: tokens.fontSizeBase300,
    color: chrome.headerMuted,
    whiteSpace: 'nowrap',
  },
  nav: {
    display: 'flex',
    alignItems: 'stretch',
    gap: tokens.spacingHorizontalXS,
    height: '44px',
    padding: `0 ${tokens.spacingHorizontalXXL}`,
    backgroundColor: tokens.colorNeutralBackground1,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
    overflowX: 'auto',
    flexShrink: 0,
  },
  navCompany: {
    display: 'flex',
    alignItems: 'center',
    marginRight: tokens.spacingHorizontalL,
    fontSize: tokens.fontSizeBase400,
    fontWeight: tokens.fontWeightBold,
    color: tokens.colorNeutralForeground1,
    whiteSpace: 'nowrap',
  },
  navLink: {
    display: 'flex',
    alignItems: 'center',
    padding: `0 ${tokens.spacingHorizontalM}`,
    color: tokens.colorNeutralForeground2,
    fontSize: tokens.fontSizeBase300,
    textDecorationLine: 'none',
    whiteSpace: 'nowrap',
    borderBottom: '2px solid transparent',
    ':hover': {
      color: tokens.colorBrandForeground1,
    },
    ':focus-visible': {
      outline: `2px solid ${tokens.colorStrokeFocus2}`,
      outlineOffset: '-2px',
    },
  },
  navActive: {
    color: tokens.colorBrandForeground1,
    fontWeight: tokens.fontWeightSemibold,
    borderBottom: `2px solid ${tokens.colorBrandStroke1}`,
  },
  main: {
    flexGrow: 1,
    display: 'flex',
    flexDirection: 'column',
    minWidth: 0,
  },
});

const navigation = [
  { to: '/', label: 'Home', end: true },
  { to: '/customers', label: 'Customers', end: false },
  { to: '/contacts', label: 'Contacts', end: false },
  { to: '/crm-opportunities', label: 'CRM Opportunities', end: false },
  { to: '/quotes', label: 'Sales Quotes', end: false },
  { to: '/orders', label: 'Sales Orders', end: false },
  { to: '/salespeople', label: 'Salespeople', end: false },
];

/** The Business Central web client chrome: app bar, role-centre navigation and notices. */
export function BcShell() {
  const styles = useStyles();
  const { data: info } = usePolling((signal) => api.info(signal), null);
  const companyName = info?.companyName ?? 'Contoso Subsea';

  return (
    <div className={styles.root}>
      <header className={styles.header}>
        <GridDots20Regular className={styles.waffle} aria-hidden />
        <span className={styles.product}>Business Central</span>
        <span className={styles.simulated}>(simulated)</span>
        <div className={styles.headerRight}>
          {info && (
            <span className={styles.environment} title="Environment" data-testid="environment">
              {info.environment}
            </span>
          )}
          <span className={styles.company}>{companyName}</span>
          <Avatar size={28} color="neutral" aria-label="Signed-in user (Sales Order Processor)" />
        </div>
      </header>

      <nav className={styles.nav} aria-label="Role Center navigation">
        <span className={styles.navCompany}>{companyName}</span>
        {navigation.map((item) => (
          <NavLink
            key={item.to}
            to={item.to}
            end={item.end}
            className={({ isActive }) => mergeClasses(styles.navLink, isActive && styles.navActive)}
          >
            {item.label}
          </NavLink>
        ))}
      </nav>

      <EnvironmentBanner />

      <main className={styles.main}>
        <Outlet />
      </main>
    </div>
  );
}
