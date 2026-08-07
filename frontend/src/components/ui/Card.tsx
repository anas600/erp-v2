"use client";

/**
 * Card — shared component.
 * Subtle border, optional top gradient highlight strip.
 *
 * The highlight is a 1px gradient on the top edge only (per the
 * design system spec) — implemented as a child element because
 * CSS doesn't allow background-clip on individual borders.
 */
import { HTMLAttributes, forwardRef } from "react";
import { cn } from "@/lib/utils";

interface CardProps extends HTMLAttributes<HTMLDivElement> {
  highlight?: boolean;
}

const Card = forwardRef<HTMLDivElement, CardProps>(function Card(
  { className, highlight = false, children, ...rest },
  ref
) {
  return (
    <div
      ref={ref}
      className={cn(
        "relative bg-raised dark:bg-neutral-900 rounded-card border border-edge",
        className
      )}
      {...rest}
    >
      {highlight && (
        <div
          aria-hidden
          className="absolute inset-x-0 top-0 h-px bg-brand-gradient rounded-t-card"
        />
      )}
      <div className="p-4">{children}</div>
    </div>
  );
});

export default Card;
