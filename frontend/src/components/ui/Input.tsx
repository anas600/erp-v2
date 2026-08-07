"use client";

/**
 * Input — shared component.
 * Consistent border + brand focus ring. Auto-adapts to dark mode
 * via the semantic tokens.
 */
import { InputHTMLAttributes, forwardRef } from "react";
import { cn } from "@/lib/utils";

interface InputProps extends InputHTMLAttributes<HTMLInputElement> {
  invalid?: boolean;
}

const Input = forwardRef<HTMLInputElement, InputProps>(function Input(
  { className, invalid = false, ...rest },
  ref
) {
  return (
    <input
      ref={ref}
      className={cn(
        "w-full px-3 h-10 rounded-md text-sm",
        "bg-canvas text-ink-strong placeholder:text-ink-subtle",
        "border focus:outline-none focus:ring-2 focus:ring-offset-0",
        invalid
          ? "border-red-500 focus:ring-red-500 focus:border-red-500"
          : "border-edge focus:ring-brand-500 focus:border-brand-500",
        "disabled:opacity-50 disabled:cursor-not-allowed",
        className
      )}
      {...rest}
    />
  );
});

export default Input;
