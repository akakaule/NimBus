import type { ReactElement } from 'react';
import { makeStyles, mergeClasses, tokens, Tooltip } from '@fluentui/react-components';
import { Building20Regular, Gauge20Regular, Money20Regular, Navigation20Regular, PersonCall20Regular } from '@fluentui/react-icons';
import { NavLink } from 'react-router-dom';

interface SiteMapItem {
  to: string;
  label: string;
  icon: ReactElement;
  end?: boolean;
}

const GROUPS: { title: string; items: SiteMapItem[] }[] = [
  { title: 'My Work', items: [{ to: '/', label: 'Dashboard', icon: <Gauge20Regular />, end: true }] },
  {
    title: 'Sales',
    items: [
      { to: '/leads', label: 'Leads', icon: <PersonCall20Regular /> },
      { to: '/opportunities', label: 'Opportunities', icon: <Money20Regular /> },
    ],
  },
  { title: 'Customers', items: [{ to: '/accounts', label: 'Accounts', icon: <Building20Regular /> }] },
];

const useStyles = makeStyles({
  nav: {
    width: '216px',
    flexShrink: 0,
    display: 'flex',
    flexDirection: 'column',
    padding: '4px 8px 12px',
    overflowY: 'auto',
    boxSizing: 'border-box',
    transitionProperty: 'width',
    transitionDuration: tokens.durationNormal,
  },
  collapsed: {
    width: '56px',
  },
  hamburger: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    width: '40px',
    height: '40px',
    marginBottom: '4px',
    border: 'none',
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: 'transparent',
    color: tokens.colorNeutralForeground2,
    cursor: 'pointer',
    ':hover': { backgroundColor: tokens.colorSubtleBackgroundHover },
    ':focus-visible': {
      outlineStyle: 'solid',
      outlineWidth: '2px',
      outlineColor: tokens.colorStrokeFocus2,
    },
  },
  group: {
    marginBottom: '10px',
  },
  groupTitle: {
    padding: '10px 10px 4px',
    fontSize: tokens.fontSizeBase200,
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground3,
    whiteSpace: 'nowrap',
  },
  groupRule: {
    height: '1px',
    margin: '8px 6px',
    backgroundColor: tokens.colorNeutralStroke2,
  },
  list: {
    listStyleType: 'none',
    margin: 0,
    padding: 0,
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
  },
  item: {
    position: 'relative',
    display: 'flex',
    alignItems: 'center',
    gap: '10px',
    height: '36px',
    padding: '0 10px',
    borderRadius: tokens.borderRadiusMedium,
    color: tokens.colorNeutralForeground2,
    fontSize: tokens.fontSizeBase300,
    textDecorationLine: 'none',
    whiteSpace: 'nowrap',
    overflow: 'hidden',
    ':hover': {
      backgroundColor: tokens.colorSubtleBackgroundHover,
      color: tokens.colorNeutralForeground1,
    },
    ':focus-visible': {
      outlineStyle: 'solid',
      outlineWidth: '2px',
      outlineColor: tokens.colorStrokeFocus2,
    },
  },
  active: {
    backgroundColor: tokens.colorNeutralBackground1,
    color: tokens.colorNeutralForeground1,
    fontWeight: tokens.fontWeightSemibold,
    boxShadow: tokens.shadow2,
    ':hover': { backgroundColor: tokens.colorNeutralBackground1 },
    '::before': {
      content: '""',
      position: 'absolute',
      left: 0,
      top: '8px',
      bottom: '8px',
      width: '3px',
      borderRadius: tokens.borderRadiusCircular,
      backgroundColor: tokens.colorBrandBackground,
    },
  },
  icon: {
    display: 'flex',
    flexShrink: 0,
  },
});

interface SiteMapProps {
  collapsed: boolean;
  onToggle: () => void;
}

/** The left site map of the Sales Hub app. */
export function SiteMap({ collapsed, onToggle }: SiteMapProps) {
  const styles = useStyles();

  return (
    <nav className={mergeClasses(styles.nav, collapsed && styles.collapsed)} aria-label="Site map">
      <button
        type="button"
        className={styles.hamburger}
        onClick={onToggle}
        aria-label={collapsed ? 'Expand site map' : 'Collapse site map'}
        aria-expanded={!collapsed}
      >
        <Navigation20Regular />
      </button>
      {GROUPS.map((group) => (
        <div key={group.title} className={styles.group}>
          {collapsed ? <div className={styles.groupRule} /> : <div className={styles.groupTitle}>{group.title}</div>}
          <ul className={styles.list}>
            {group.items.map((item) => {
              const link = (
                <NavLink
                  to={item.to}
                  end={item.end}
                  className={({ isActive }) => mergeClasses(styles.item, isActive && styles.active)}
                >
                  <span className={styles.icon}>{item.icon}</span>
                  {!collapsed && item.label}
                </NavLink>
              );
              return (
                <li key={item.to}>
                  {collapsed ? (
                    <Tooltip content={item.label} relationship="label" positioning="after">
                      {link}
                    </Tooltip>
                  ) : (
                    link
                  )}
                </li>
              );
            })}
          </ul>
        </div>
      ))}
    </nav>
  );
}
