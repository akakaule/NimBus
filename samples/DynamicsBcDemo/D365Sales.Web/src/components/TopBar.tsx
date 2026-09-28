import {
  Avatar,
  Input,
  makeStyles,
  Menu,
  MenuGroup,
  MenuGroupHeader,
  MenuItemLink,
  MenuItemRadio,
  MenuList,
  MenuPopover,
  MenuTrigger,
  tokens,
} from '@fluentui/react-components';
import { GridDots20Regular, Open16Regular, Search20Regular } from '@fluentui/react-icons';
import { BC_WEB_URL, NIMBUS_OPS_URL } from '../config';
import { useCurrentUser } from '../user';

/** The dark app header of a model-driven app. */
export const TOP_BAR_HEIGHT = 48;
const BACKGROUND = '#0f1c33';
const HOVER = 'rgba(255, 255, 255, 0.10)';
const PRESSED = 'rgba(255, 255, 255, 0.16)';

const headerButton = {
  display: 'flex',
  alignItems: 'center',
  border: 'none',
  backgroundColor: 'transparent',
  color: 'inherit',
  cursor: 'pointer',
  fontFamily: 'inherit',
  ':hover': { backgroundColor: HOVER },
  ':active': { backgroundColor: PRESSED },
  ':focus-visible': {
    outlineStyle: 'solid',
    outlineWidth: '2px',
    outlineOffset: '-2px',
    outlineColor: '#ffffff',
  },
} as const;

const useStyles = makeStyles({
  bar: {
    display: 'flex',
    alignItems: 'center',
    gap: '12px',
    height: `${TOP_BAR_HEIGHT}px`,
    flexShrink: 0,
    paddingRight: '8px',
    backgroundColor: BACKGROUND,
    color: '#ffffff',
  },
  waffle: {
    ...headerButton,
    justifyContent: 'center',
    width: `${TOP_BAR_HEIGHT}px`,
    height: `${TOP_BAR_HEIGHT}px`,
    flexShrink: 0,
  },
  brand: {
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
    minWidth: 0,
    whiteSpace: 'nowrap',
  },
  app: {
    fontSize: tokens.fontSizeBase400,
    fontWeight: tokens.fontWeightSemibold,
  },
  simulated: {
    padding: '0 6px',
    borderRadius: tokens.borderRadiusMedium,
    border: '1px solid rgba(255, 255, 255, 0.35)',
    fontSize: tokens.fontSizeBase100,
    lineHeight: '16px',
    color: 'rgba(255, 255, 255, 0.85)',
  },
  divider: {
    width: '1px',
    height: '20px',
    margin: '0 4px',
    backgroundColor: 'rgba(255, 255, 255, 0.3)',
  },
  company: {
    fontSize: tokens.fontSizeBase300,
    color: 'rgba(255, 255, 255, 0.8)',
    overflow: 'hidden',
    textOverflow: 'ellipsis',
  },
  searchArea: {
    flex: 1,
    display: 'flex',
    justifyContent: 'center',
    minWidth: '120px',
    padding: '0 16px',
  },
  search: {
    width: '100%',
    maxWidth: '440px',
  },
  persona: {
    ...headerButton,
    gap: '10px',
    height: '40px',
    padding: '0 8px 0 12px',
    borderRadius: tokens.borderRadiusMedium,
    flexShrink: 0,
  },
  personaText: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'flex-end',
    lineHeight: '15px',
  },
  personaLabel: {
    fontSize: tokens.fontSizeBase100,
    color: 'rgba(255, 255, 255, 0.72)',
  },
  personaName: {
    fontSize: tokens.fontSizeBase300,
    fontWeight: tokens.fontWeightSemibold,
  },
});

export function TopBar() {
  const styles = useStyles();
  const { users, user, userId, setUserId } = useCurrentUser();

  return (
    <header className={styles.bar}>
      <Menu>
        <MenuTrigger disableButtonEnhancement>
          <button type="button" className={styles.waffle} aria-label="App launcher">
            <GridDots20Regular />
          </button>
        </MenuTrigger>
        <MenuPopover>
          <MenuList>
            <MenuGroup>
              <MenuGroupHeader>Contoso Subsea apps (simulated)</MenuGroupHeader>
              <MenuItemLink href={BC_WEB_URL} target="_blank" rel="noopener noreferrer" secondaryContent={<Open16Regular />}>
                Business Central
              </MenuItemLink>
              <MenuItemLink href={NIMBUS_OPS_URL} target="_blank" rel="noopener noreferrer" secondaryContent={<Open16Regular />}>
                NimBus operations
              </MenuItemLink>
            </MenuGroup>
          </MenuList>
        </MenuPopover>
      </Menu>

      <div className={styles.brand}>
        <span className={styles.app}>Sales Hub</span>
        <span className={styles.simulated}>(simulated)</span>
        <span className={styles.divider} aria-hidden />
        <span className={styles.company}>Contoso Subsea</span>
      </div>

      <div className={styles.searchArea}>
        <Input
          className={styles.search}
          contentBefore={<Search20Regular />}
          placeholder="Search"
          aria-label="Search"
        />
      </div>

      <Menu
        checkedValues={{ user: [userId] }}
        onCheckedValueChange={(_, data) => {
          const selected = data.checkedItems[0];
          if (selected) setUserId(selected);
        }}
      >
        <MenuTrigger disableButtonEnhancement>
          <button type="button" className={styles.persona} data-testid="signed-in-as">
            <span className={styles.personaText}>
              <span className={styles.personaLabel}>Signed in as</span>
              <span className={styles.personaName}>{user?.fullName ?? '…'}</span>
            </span>
            <Avatar name={user?.fullName} size={28} color="colorful" aria-hidden />
          </button>
        </MenuTrigger>
        <MenuPopover>
          <MenuList>
            <MenuGroup>
              <MenuGroupHeader>Signed in as</MenuGroupHeader>
              {users.map((u) => (
                <MenuItemRadio key={u.systemUserId} name="user" value={u.systemUserId}>
                  {u.fullName}
                </MenuItemRadio>
              ))}
            </MenuGroup>
          </MenuList>
        </MenuPopover>
      </Menu>
    </header>
  );
}
