import { Dropdown, Input, makeStyles, Option, type InputProps } from '@fluentui/react-components';
import { useId, type ReactNode } from 'react';
import { FieldRow, Value } from './Fields';

const useStyles = makeStyles({
  control: {
    width: '100%',
    minWidth: 0,
    maxWidth: '360px',
  },
});

interface TextRowProps {
  label: string;
  value: string;
  editing: boolean;
  onChange: (value: string) => void;
  type?: InputProps['type'];
  required?: boolean;
  /** What read mode shows, when it differs from the raw value (e.g. a formatted amount). */
  display?: ReactNode;
  hint?: ReactNode;
  'data-testid'?: string;
}

/** A card field that turns into a text box in edit mode. */
export function TextRow({ label, value, editing, onChange, type = 'text', required, display, hint, 'data-testid': testId }: TextRowProps) {
  const styles = useStyles();
  const id = useId();
  if (!editing) {
    return (
      <FieldRow label={label} hint={hint} data-testid={testId}>
        <Value>{display ?? value}</Value>
      </FieldRow>
    );
  }
  return (
    <FieldRow label={required ? `${label} *` : label} htmlFor={id} hint={hint} data-testid={testId}>
      <Input
        id={id}
        size="small"
        type={type}
        className={styles.control}
        value={value}
        required={required}
        onChange={(_, data) => onChange(data.value)}
      />
    </FieldRow>
  );
}

export interface Choice {
  value: string;
  text: string;
}

interface ChoiceRowProps {
  label: string;
  value: string;
  choices: Choice[];
  editing: boolean;
  onChange: (value: string) => void;
  display?: ReactNode;
  hint?: ReactNode;
  placeholder?: string;
  'data-testid'?: string;
}

/** A card field that turns into a dropdown in edit mode. Choice values must be non-empty. */
export function ChoiceRow({ label, value, choices, editing, onChange, display, hint, placeholder, 'data-testid': testId }: ChoiceRowProps) {
  const styles = useStyles();
  const id = useId();
  const selected = choices.find((c) => c.value === value);
  if (!editing) {
    return (
      <FieldRow label={label} hint={hint} data-testid={testId}>
        <Value>{display ?? selected?.text ?? value}</Value>
      </FieldRow>
    );
  }
  return (
    <FieldRow label={label} htmlFor={id} hint={hint} data-testid={testId}>
      <Dropdown
        id={id}
        size="small"
        className={styles.control}
        value={selected?.text ?? ''}
        selectedOptions={selected ? [selected.value] : []}
        placeholder={placeholder}
        onOptionSelect={(_, data) => {
          if (data.optionValue !== undefined) onChange(data.optionValue);
        }}
      >
        {choices.map((choice) => (
          <Option key={choice.value} value={choice.value}>
            {choice.text}
          </Option>
        ))}
      </Dropdown>
    </FieldRow>
  );
}
