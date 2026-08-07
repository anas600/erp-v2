"use client";

/**
 * Badge — shared component.
 * Variants: brand, success, warning, danger, neutral
 *
 * Backed by the semantic CSS variables so it auto-adapts to
 * dark mode without explicit dark: classes per variant.
 */
import { HTMLAttributes } from "react";
import { cn } from "@/lib/utils";

type Variant = "brand" | "success" | "warning" | "danger" | "neutral";

interface BadgeProps extends HTMLAttributes<HTMLSpanElement> {
  variant?: Variant;
}

const variantClass: Record<Variant, string> = {
  brand: "bg-brand-light text-brand-700 dark:bg-brand-900/40 dark:text-brand-300",
  success: "bg-green-100 text-green-800 dark:bg-green-900/40 dark:text-green-300",
  warning: "bg-amber-100 text-amber-800 dark:bg-amber-900/40 dark:text-amber-300",
  danger: "bg-red-100 text-red-800 dark:bg-red-900/40 dark:text-red-300",
  neutral: "bg-neutral-100 text-neutral-700 dark:bg-neutral-800 dark:text-neutral-200"
};

export default function Badge({
  variant = "brand",
  className,
  children,
  ...rest
}: BadgeProps) {
  return (
    <span
      className={cn(
        "inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-xs font-medium",
        variantClass[variant],
        className
      )}
      {...rest}
    >
      {children}
    </span>
  );
}
