import { type LabelHTMLAttributes } from "react";
import { cn } from "lib/utils";

export interface LabelProps extends LabelHTMLAttributes<HTMLLabelElement> {
  required?: boolean;
}

const Label = ({ className, required, children, ...props }: LabelProps) => (
  <label
    className={cn(
      "text-sm font-medium text-foreground leading-none",
      "peer-disabled:cursor-not-allowed peer-disabled:opacity-70",
      className,
    )}
    {...props}
  >
    {children}
    {required && <span className="text-red-500 ml-1">*</span>}
  </label>
);

export { Label };
