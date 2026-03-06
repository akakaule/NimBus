import {
  createContext,
  useContext,
  useState,
  type ReactNode,
  type HTMLAttributes,
} from "react";
import { cn } from "lib/utils";

// Context for accordion state
interface AccordionContextValue {
  expandedItems: Set<string>;
  toggleItem: (id: string) => void;
  allowMultiple: boolean;
}

const AccordionContext = createContext<AccordionContextValue | null>(null);

function useAccordionContext() {
  const context = useContext(AccordionContext);
  if (!context) {
    throw new Error("Accordion components must be used within an Accordion");
  }
  return context;
}

// Accordion container
export interface AccordionProps extends HTMLAttributes<HTMLDivElement> {
  allowMultiple?: boolean;
  defaultExpandedItems?: string[];
  children: ReactNode;
}

const Accordion = ({
  allowMultiple = false,
  defaultExpandedItems = [],
  className,
  children,
  ...props
}: AccordionProps) => {
  const [expandedItems, setExpandedItems] = useState<Set<string>>(
    new Set(defaultExpandedItems),
  );

  const toggleItem = (id: string) => {
    setExpandedItems((prev) => {
      const next = new Set(prev);
      if (next.has(id)) {
        next.delete(id);
      } else {
        if (!allowMultiple) {
          next.clear();
        }
        next.add(id);
      }
      return next;
    });
  };

  return (
    <AccordionContext.Provider
      value={{ expandedItems, toggleItem, allowMultiple }}
    >
      <div className={cn("divide-y divide-border", className)} {...props}>
        {children}
      </div>
    </AccordionContext.Provider>
  );
};

// Accordion item
export interface AccordionItemProps extends HTMLAttributes<HTMLDivElement> {
  id: string;
  children: ReactNode;
}

const AccordionItem = ({
  id,
  className,
  children,
  ...props
}: AccordionItemProps) => {
  return (
    <div className={cn("", className)} data-accordion-id={id} {...props}>
      {children}
    </div>
  );
};

// Accordion trigger/button
export interface AccordionTriggerProps extends HTMLAttributes<HTMLButtonElement> {
  itemId: string;
  children: ReactNode;
}

const AccordionTrigger = ({
  itemId,
  className,
  children,
  ...props
}: AccordionTriggerProps) => {
  const { expandedItems, toggleItem } = useAccordionContext();
  const isExpanded = expandedItems.has(itemId);

  return (
    <button
      type="button"
      className={cn(
        "flex w-full items-center justify-between py-4 px-4 text-left font-medium transition-colors",
        "hover:bg-accent focus:outline-none focus:ring-2 focus:ring-primary focus:ring-inset",
        isExpanded && "bg-accent",
        className,
      )}
      onClick={() => toggleItem(itemId)}
      aria-expanded={isExpanded}
      {...props}
    >
      {children}
      <svg
        className={cn(
          "h-5 w-5 shrink-0 transition-transform duration-200",
          isExpanded && "rotate-180",
        )}
        fill="none"
        viewBox="0 0 24 24"
        stroke="currentColor"
      >
        <path
          strokeLinecap="round"
          strokeLinejoin="round"
          strokeWidth={2}
          d="M19 9l-7 7-7-7"
        />
      </svg>
    </button>
  );
};

// Accordion content/panel
export interface AccordionContentProps extends HTMLAttributes<HTMLDivElement> {
  itemId: string;
  children: ReactNode;
}

const AccordionContent = ({
  itemId,
  className,
  children,
  ...props
}: AccordionContentProps) => {
  const { expandedItems } = useAccordionContext();
  const isExpanded = expandedItems.has(itemId);

  if (!isExpanded) {
    return null;
  }

  return (
    <div className={cn("pb-4 px-4", className)} role="region" {...props}>
      {children}
    </div>
  );
};

export { Accordion, AccordionItem, AccordionTrigger, AccordionContent };
