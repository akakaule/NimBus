import { useEffect, useState } from 'react';
import { Button, Input, makeStyles, tokens, useId } from '@fluentui/react-components';
import { Save20Regular } from '@fluentui/react-icons';
import { api, errorMessage, type Account } from '../../api';
import { FieldRow, Section } from '../../components/ui';
import { orDash } from '../../format';
import { useNotify } from '../../hooks/useNotify';
import { isBcOwned } from '../../model';

const OWNED_BY_BC = 'Owned by Business Central — change it there.';

type MasterDataKey =
  | 'name'
  | 'address1Line1'
  | 'address1City'
  | 'address1PostalCode'
  | 'address1Country'
  | 'telephone1'
  | 'websiteUrl'
  | 'csVatNumber';

type MasterData = Record<MasterDataKey, string>;

const FIELDS: { key: MasterDataKey; label: string }[] = [
  { key: 'name', label: 'Account name' },
  { key: 'address1Line1', label: 'Address' },
  { key: 'address1City', label: 'City' },
  { key: 'address1PostalCode', label: 'Postal code' },
  { key: 'address1Country', label: 'Country/region' },
  { key: 'telephone1', label: 'Phone' },
  { key: 'websiteUrl', label: 'Website' },
  { key: 'csVatNumber', label: 'VAT number' },
];

const toMasterData = (account: Account): MasterData => ({
  name: account.name,
  address1Line1: account.address1Line1 ?? '',
  address1City: account.address1City ?? '',
  address1PostalCode: account.address1PostalCode ?? '',
  address1Country: account.address1Country ?? '',
  telephone1: account.telephone1 ?? '',
  websiteUrl: account.websiteUrl ?? '',
  csVatNumber: account.csVatNumber ?? '',
});

const sameMasterData = (a: MasterData, b: MasterData) => FIELDS.every(({ key }) => a[key] === b[key]);
const blankToNull = (value: string) => (value.trim() ? value.trim() : null);

const useStyles = makeStyles({
  input: {
    width: '100%',
    maxWidth: '360px',
  },
  actions: {
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
    marginTop: '12px',
    paddingTop: '12px',
    borderTop: `1px solid ${tokens.colorNeutralStroke3}`,
  },
  unsaved: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
  },
});

interface AccountInfoSectionProps {
  account: Account;
  refresh: () => Promise<void>;
}

/** The account's master data: editable while Dynamics 365 owns it, locked once Business Central does. */
export function AccountInfoSection({ account, refresh }: AccountInfoSectionProps) {
  const styles = useStyles();
  const notify = useNotify();
  const idPrefix = useId('account');
  const locked = isBcOwned(account);
  const [draft, setDraft] = useState<MasterData | null>(null);
  const [saving, setSaving] = useState(false);

  // Business Central can take the account over (first quote) while the seller is editing.
  useEffect(() => {
    if (locked) setDraft(null);
  }, [locked]);

  const server = toMasterData(account);
  const values = draft ?? server;
  const dirty = draft !== null && !sameMasterData(draft, server);
  const valid = values.name.trim().length > 0;

  const save = async () => {
    setSaving(true);
    try {
      await api.updateAccount(account.accountId, {
        name: values.name.trim(),
        address1Line1: blankToNull(values.address1Line1),
        address1City: blankToNull(values.address1City),
        address1PostalCode: blankToNull(values.address1PostalCode),
        address1Country: blankToNull(values.address1Country),
        telephone1: blankToNull(values.telephone1),
        websiteUrl: blankToNull(values.websiteUrl),
        csVatNumber: blankToNull(values.csVatNumber),
        ownerId: account.ownerId,
        description: account.description,
      });
      await refresh();
      setDraft(null);
      // Only a Dynamics 365-owned prospect can be saved here, and the API sends each save to Business Central.
      notify.success('Account saved', 'Saved and sent to Business Central, which keeps this prospect as a contact.');
    } catch (error) {
      notify.error('The account was not saved', errorMessage(error));
    } finally {
      setSaving(false);
    }
  };

  return (
    <Section
      title="Account information"
      caption={locked ? 'Master data owned by Business Central — read-only' : 'Master data owned by Dynamics 365'}
      testId="account-information"
    >
      {FIELDS.map(({ key, label }) => {
        const inputId = `${idPrefix}-${key}`;
        return (
          <FieldRow key={key} label={label} htmlFor={locked ? undefined : inputId} lockedReason={locked ? OWNED_BY_BC : undefined}>
            {locked ? (
              orDash(account[key])
            ) : (
              <Input
                id={inputId}
                className={styles.input}
                appearance="filled-darker"
                value={values[key]}
                onChange={(_, data) => setDraft({ ...values, [key]: data.value })}
                data-testid={`account-${key}`}
              />
            )}
          </FieldRow>
        );
      })}
      {account.description && <FieldRow label="Description">{account.description}</FieldRow>}
      {!locked && (
        <div className={styles.actions}>
          <Button
            appearance="primary"
            icon={<Save20Regular />}
            disabled={!dirty || !valid || saving}
            onClick={() => void save()}
            data-testid="save-account"
          >
            {saving ? 'Saving…' : 'Save'}
          </Button>
          <Button disabled={!dirty || saving} onClick={() => setDraft(null)}>
            Discard changes
          </Button>
          {dirty && <span className={styles.unsaved}>{valid ? 'Unsaved changes' : 'The account name is required.'}</span>}
        </div>
      )}
    </Section>
  );
}
