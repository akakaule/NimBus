import type { ReactNode } from 'react';
import { Avatar, makeStyles, tokens } from '@fluentui/react-components';

const useStyles = makeStyles({
  header: {
    backgroundColor: tokens.colorNeutralBackground1,
    borderRadius: tokens.borderRadiusXLarge,
    boxShadow: tokens.shadow2,
    padding: '14px 20px 0',
    marginBottom: '12px',
  },
  top: {
    display: 'flex',
    alignItems: 'flex-start',
    justifyContent: 'space-between',
    flexWrap: 'wrap',
    columnGap: '24px',
    rowGap: '10px',
    paddingBottom: '12px',
  },
  identity: {
    display: 'flex',
    alignItems: 'center',
    gap: '12px',
    minWidth: 0,
    flex: '1 1 320px',
  },
  titles: {
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
    minWidth: 0,
  },
  titleRow: {
    display: 'flex',
    alignItems: 'center',
    flexWrap: 'wrap',
    gap: '10px',
  },
  title: {
    margin: 0,
    fontSize: tokens.fontSizeBase500,
    lineHeight: tokens.lineHeightBase500,
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground1,
  },
  subtitle: {
    fontSize: tokens.fontSizeBase200,
    lineHeight: tokens.lineHeightBase200,
    color: tokens.colorNeutralForeground3,
  },
  fields: {
    display: 'flex',
    alignItems: 'stretch',
    flexWrap: 'wrap',
    rowGap: '8px',
  },
  field: {
    display: 'flex',
    flexDirection: 'column',
    justifyContent: 'center',
    gap: '2px',
    minWidth: '96px',
    maxWidth: '240px',
    padding: '0 16px',
    borderLeft: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  fieldValue: {
    fontSize: tokens.fontSizeBase300,
    lineHeight: tokens.lineHeightBase300,
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground1,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  fieldLabel: {
    fontSize: tokens.fontSizeBase200,
    lineHeight: tokens.lineHeightBase200,
    color: tokens.colorNeutralForeground3,
    whiteSpace: 'nowrap',
  },
});

export interface HeaderField {
  label: string;
  value: ReactNode;
  testId?: string;
}

interface FormHeaderProps {
  title: string;
  /** Entity name and key, e.g. "Opportunity · OPP-10025". */
  subtitle: ReactNode;
  /** Badges next to the title. */
  badges?: ReactNode;
  fields: HeaderField[];
  /** Business process flow, tab list. */
  children?: ReactNode;
}

/** The record header of a model-driven form: name, entity, header fields, then the tabs. */
export function FormHeader({ title, subtitle, badges, fields, children }: FormHeaderProps) {
  const styles = useStyles();
  return (
    <header className={styles.header}>
      <div className={styles.top}>
        <div className={styles.identity}>
          <Avatar name={title} shape="square" size={48} color="colorful" aria-hidden />
          <div className={styles.titles}>
            <div className={styles.titleRow}>
              <h1 className={styles.title}>{title}</h1>
              {badges}
            </div>
            <span className={styles.subtitle}>{subtitle}</span>
          </div>
        </div>
        <div className={styles.fields}>
          {fields.map((field) => (
            <div key={field.label} className={styles.field} data-testid={field.testId}>
              <span className={styles.fieldValue}>{field.value}</span>
              <span className={styles.fieldLabel}>{field.label}</span>
            </div>
          ))}
        </div>
      </div>
      {children}
    </header>
  );
}
