import {
  createContext,
  useContext,
  type InputHTMLAttributes,
  type ReactNode,
} from "react";
import { cn } from "lib/utils";

interface RadioGroupContextValue {
  name: string;
  value: string | undefined;
  onChange: (value: string) => void;
  disabled?: boolean;
}

const RadioGroupContext = createContext<RadioGroupContextValue | null>(null);

export interface RadioGroupProps {
  name: string;
  value?: string;
  defaultValue?: string;
  onChange?: (value: string) => void;
  disabled?: boolean;
  className?: string;
  children: ReactNode;
}

const RadioGroup = ({
  name,
  value,
  onChange,
  disabled,
  className,
  children,
}: RadioGroupProps) => {
  return (
    <RadioGroupContext.Provider
      value={{
        name,
        value,
        onChange: onChange ?? (() => {}),
        disabled,
      }}
    >
      <div role="radiogroup" className={cn("inline-flex gap-3", className)}>
        {children}
      </div>
    </RadioGroupContext.Provider>
  );
};

export interface RadioProps
  extends Omit<InputHTMLAttributes<HTMLInputElement>, "value" | "onChange" | "type" | "name"> {
  value: string;
  children?: ReactNode;
}

const Radio = ({ value, children, className, disabled, ...rest }: RadioProps) => {
  const ctx = useContext(RadioGroupContext);
  if (!ctx) {
    throw new Error("Radio must be used inside a RadioGroup");
  }

  const isDisabled = disabled || ctx.disabled;

  return (
    <label
      className={cn(
        "inline-flex items-center gap-2 cursor-pointer",
        isDisabled && "cursor-not-allowed opacity-60",
        className,
      )}
    >
      <input
        type="radio"
        name={ctx.name}
        value={value}
        checked={ctx.value === value}
        disabled={isDisabled}
        onChange={() => ctx.onChange(value)}
        className="h-4 w-4 cursor-pointer accent-primary disabled:cursor-not-allowed"
        {...rest}
      />
      {children && <span className="text-sm text-foreground">{children}</span>}
    </label>
  );
};

export { RadioGroup, Radio };
