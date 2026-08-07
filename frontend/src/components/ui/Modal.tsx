"use client";

/**
 * Modal — shared component.
 * Sprint 37 — refreshes the existing modal surface (if any) to
 * use the new design tokens. The previous version was an inline
 * className soup; this one is a small wrapper around <dialog>
 * for the open/close lifecycle and a backdrop click trap.
 */
import { ReactNode, useEffect } from "react";
import { X } from "lucide-react";
import { cn } from "@/lib/utils";

interface ModalProps {
  open: boolean;
  onClose: () => void;
  title?: string;
  children: ReactNode;
  size?: "sm" | "md" | "lg" | "xl";
  footer?: ReactNode;
}

const sizeClass = {
  sm: "max-w-sm",
  md: "max-w-md",
  lg: "max-w-2xl",
  xl: "max-w-4xl"
};

export default function Modal({
  open,
  onClose,
  title,
  children,
  size = "md",
  footer
}: ModalProps) {
  // Lock body scroll while open
  useEffect(() => {
    if (!open) return;
    const prev = document.body.style.overflow;
    document.body.style.overflow = "hidden";
    return () => {
      document.body.style.overflow = prev;
    };
  }, [open]);

  if (!open) return null;

  return (
    <div
      className="fixed inset-0 z-50 flex items-center justify-center p-4 bg-black/50"
      onClick={onClose}
      role="dialog"
      aria-modal="true"
    >
      <div
        className={cn(
          "w-full bg-raised dark:bg-neutral-900 rounded-card border border-edge shadow-lg",
          "max-h-[90vh] overflow-hidden flex flex-col",
          sizeClass[size]
        )}
        onClick={(e) => e.stopPropagation()}
      >
        {title && (
          <div className="flex items-center justify-between px-5 py-3 border-b border-edge">
            <h3 className="text-canvas font-semibold text-ink-strong">{title}</h3>
            <button
              type="button"
              onClick={onClose}
              className="p-1 rounded-md text-ink-subtle hover:bg-canvas hover:text-ink-strong"
              aria-label="إغلاق"
            >
              <X size={18} />
            </button>
          </div>
        )}
        <div className="p-5 overflow-y-auto flex-1">{children}</div>
        {footer && (
          <div className="px-5 py-3 border-t border-edge flex justify-end gap-2">
            {footer}
          </div>
        )}
      </div>
    </div>
  );
}
