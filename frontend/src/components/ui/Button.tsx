"use client";

/**
 * Button — shared component for the design system.
 * Variants: primary (brand), secondary (outline), danger, ghost
 * Sizes: sm, md, lg
 *
 * Sprint 37 — wraps <button> with a thin class merge utility.
 * Replaces the legacy `.btn` / `.btn-primary` CSS classes for
 * new code. Old code still works thanks to the CSS shim in
 * globals.css.
 */
import { ButtonHTMLAttributes, forwardRef } from "react";
import { cn } from "@/lib/utils";

type Variant = "primary" | "secondary" | "danger" | "ghost";
type Size = "sm" | "md" | "lg";

interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: Variant;
  size?: Size;
}

const variantClass: Record<Variant, string> = {
  primary:
    "bg-primary-700 text-white hover:bg-primary-800 active:bg-primary-900 " +
    "focus-visible:ring-primary-500 dark:bg-primary-600 dark:hover:bg-primary-500",
  secondary:
    "bg-raised text-ink-strong border border-edge hover:bg-canvas " +
    "focus-visible:ring-primary-500",
  danger:
    "bg-red-600 text-white hover:bg-red-700 active:bg-red-800 " +
    "focus-visible:ring-red-500",
  ghost:
    "bg-transparent text-ink-muted hover:bg-raised hover:text-ink-strong " +
    "focus-visible:ring-primary-500"
};

const sizeClass: Record<Size, string> = {
  sm: "h-8 px-3 text-xs gap-1.5 rounded-md",
  md: "h-10 px-4 text-sm gap-2 rounded-md",
  lg: "h-12 px-5 text-canvas gap-2 rounded-md"
};

const Button = forwardRef<HTMLButtonElement, ButtonProps>(function Button(
  { className, variant = "primary", size = "md", type = "button", ...rest },
  ref
) {
  return (
    <button
      ref={ref}
      type={type}
      className={cn(
        "inline-flex items-center justify-center font-medium transition-colors",
        "focus:outline-none focus-visible:ring-2 focus-visible:ring-offset-2 focus-visible:ring-offset-bg-primary",
        "disabled:opacity-50 disabled:cursor-not-allowed",
        variantClass[variant],
        sizeClass[size],
        className
      )}
      {...rest}
    />
  );
});

export default Button;
