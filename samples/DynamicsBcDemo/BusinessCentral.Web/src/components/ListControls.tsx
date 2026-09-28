import { Button, Input, makeStyles, tokens } from '@fluentui/react-components';
import { Dismiss16Regular, Filter20Regular, Search20Regular } from '@fluentui/react-icons';

const useStyles = makeStyles({
  search: {
    width: '280px',
    maxWidth: '100%',
  },
  count: {
    marginLeft: 'auto',
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
    whiteSpace: 'nowrap',
  },
  filterLabel: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
  },
});

/** The list page's search box (filters the rows already loaded). */
export function SearchInput({ value, onChange, placeholder = 'Search' }: { value: string; onChange: (value: string) => void; placeholder?: string }) {
  const styles = useStyles();
  return (
    <Input
      className={styles.search}
      size="small"
      value={value}
      placeholder={placeholder}
      aria-label={placeholder}
      contentBefore={<Search20Regular aria-hidden />}
      onChange={(_, data) => onChange(data.value)}
    />
  );
}

/** An active filter set from a Role Center cue; clicking it clears the filter. */
export function FilterChip({ label, onClear }: { label: string; onClear: () => void }) {
  const styles = useStyles();
  return (
    <span className={styles.filterLabel}>
      <Filter20Regular aria-hidden />
      <Button
        size="small"
        appearance="outline"
        shape="circular"
        icon={<Dismiss16Regular />}
        iconPosition="after"
        onClick={onClear}
        aria-label={`Remove filter ${label}`}
      >
        {label}
      </Button>
    </span>
  );
}

/** "12 records" at the right of the list toolbar. */
export function RecordCount({ shown, total }: { shown: number; total: number }) {
  const styles = useStyles();
  return (
    <span className={styles.count}>
      {shown === total ? `${total} ${total === 1 ? 'record' : 'records'}` : `${shown} of ${total} records`}
    </span>
  );
}
