import { useState, useRef, useEffect, type ReactNode } from "react";
import { cn } from "lib/utils";
import { Checkbox } from "./checkbox";

export interface ComboboxOption {
  value: string;
  label: string;
}

export interface ComboboxProps {
  options: ComboboxOption[];
  value?: string[];
  onChange?: (value: string[]) => void;
  placeholder?: string;
  label?: string;
  multiple?: boolean;
  disabled?: boolean;
  className?: string;
}

const Combobox = ({
  options,
  value = [],
  onChange,
  placeholder = "Select...",
  label,
  multiple = true,
  disabled = false,
  className,
}: ComboboxProps) => {
  const [isOpen, setIsOpen] = useState(false);
  const [search, setSearch] = useState("");
  const containerRef = useRef<HTMLDivElement>(null);
  const inputRef = useRef<HTMLInputElement>(null);

  // Close on outside click
  useEffect(() => {
    const handleClickOutside = (event: MouseEvent) => {
      if (
        containerRef.current &&
        !containerRef.current.contains(event.target as Node)
      ) {
        setIsOpen(false);
      }
    };
    document.addEventListener("mousedown", handleClickOutside);
    return () => document.removeEventListener("mousedown", handleClickOutside);
  }, []);

  const filteredOptions = options.filter((option) =>
    option.label.toLowerCase().includes(search.toLowerCase()),
  );

  const handleSelect = (optionValue: string) => {
    if (!onChange) return;

    if (multiple) {
      const newValue = value.includes(optionValue)
        ? value.filter((v) => v !== optionValue)
        : [...value, optionValue];
      onChange(newValue);
    } else {
      onChange([optionValue]);
      setIsOpen(false);
      setSearch("");
    }
  };

  const handleInputChange = (e: React.ChangeEvent<HTMLInputElement>) => {
    setSearch(e.target.value);
    if (!isOpen) setIsOpen(true);
  };

  const handleInputClick = () => {
    if (!multiple && value.length > 0 && !isOpen) {
      setSearch("");
    }
    setIsOpen(true);
  };

  const selectedLabels = value
    .map((v) => options.find((o) => o.value === v)?.label)
    .filter(Boolean);

  return (
    <div ref={containerRef} className={cn("relative w-full", className)}>
      {label && (
        <label className="block text-sm font-medium text-foreground mb-1">
          {label}
        </label>
      )}
      <div
        className={cn(
          "flex items-center border border-input rounded-md bg-background",
          "focus-within:ring-2 focus-within:ring-primary focus-within:border-primary",
          disabled && "bg-muted cursor-not-allowed",
        )}
      >
        <div className="flex flex-wrap gap-1 p-1.5 flex-1 min-w-0">
          {multiple && value.length > 0 && (
            <div className="flex flex-wrap gap-1">
              {selectedLabels.map((label, idx) => (
                <span
                  key={idx}
                  className="inline-flex items-center gap-1 px-2 py-0.5 bg-primary-100 text-primary-800 text-xs rounded"
                >
                  {label}
                  <button
                    type="button"
                    onClick={(e) => {
                      e.stopPropagation();
                      handleSelect(value[idx]);
                    }}
                    className="hover:text-primary-600"
                  >
                    <svg
                      className="w-3 h-3"
                      fill="currentColor"
                      viewBox="0 0 20 20"
                    >
                      <path
                        fillRule="evenodd"
                        d="M4.293 4.293a1 1 0 011.414 0L10 8.586l4.293-4.293a1 1 0 111.414 1.414L11.414 10l4.293 4.293a1 1 0 01-1.414 1.414L10 11.414l-4.293 4.293a1 1 0 01-1.414-1.414L8.586 10 4.293 5.707a1 1 0 010-1.414z"
                        clipRule="evenodd"
                      />
                    </svg>
                  </button>
                </span>
              ))}
            </div>
          )}
          <input
            ref={inputRef}
            type="text"
            className="flex-1 min-w-[100px] outline-none text-sm py-1 px-1 bg-transparent"
            placeholder={
              value.length === 0
                ? placeholder
                : !multiple
                  ? (selectedLabels[0] ?? "")
                  : ""
            }
            value={search}
            onChange={handleInputChange}
            onClick={handleInputClick}
            disabled={disabled}
          />
        </div>
        <button
          type="button"
          className="px-2 py-2 text-muted-foreground hover:text-foreground"
          onClick={() => !disabled && setIsOpen(!isOpen)}
          disabled={disabled}
        >
          <svg
            className={cn(
              "w-4 h-4 transition-transform",
              isOpen && "rotate-180",
            )}
            fill="none"
            stroke="currentColor"
            viewBox="0 0 24 24"
          >
            <path
              strokeLinecap="round"
              strokeLinejoin="round"
              strokeWidth={2}
              d="M19 9l-7 7-7-7"
            />
          </svg>
        </button>
      </div>

      {isOpen && !disabled && (
        <div className="absolute z-50 w-full mt-1 bg-popover text-popover-foreground border border-border rounded-md shadow-lg max-h-60 overflow-auto">
          {filteredOptions.length === 0 ? (
            <div className="px-3 py-2 text-sm text-muted-foreground">
              No options found
            </div>
          ) : (
            filteredOptions.map((option) => {
              const isSelected = value.includes(option.value);
              return (
                <div
                  key={option.value}
                  className={cn(
                    "flex items-center gap-2 px-3 py-2 cursor-pointer hover:bg-accent",
                    isSelected && "bg-primary-50",
                  )}
                  onClick={() => handleSelect(option.value)}
                >
                  {multiple && (
                    <Checkbox
                      checked={isSelected}
                      onChange={() => {}}
                      aria-label={option.label}
                    />
                  )}
                  <span className="text-sm break-all">{option.label}</span>
                </div>
              );
            })
          )}
        </div>
      )}
    </div>
  );
};

export { Combobox };
