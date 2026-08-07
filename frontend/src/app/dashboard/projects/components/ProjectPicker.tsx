"use client";

/**
 * Sprint 35 — ProjectPicker.
 *
 * Reusable dropdown for invoice / JE / payment / receipt forms.
 *
 * Why a combobox and not a plain <select>?
 *   A company with 50+ projects can't be scrolled through on a
 *   phone. We render a search input + filtered list of project
 *   rows (code + name). When the user types, the list shrinks.
 *   A "clear" button on the right of the input sets the value
 *   back to null (= "no project").
 *
 * Why a controlled component?
 *   Each form already keeps `projectId` in its own `form` state.
 *   This component is a presentation/controlled-input hybrid:
 *   it owns the *internal* UI (search text, open/closed) but
 *   the parent owns the *value*. When the parent re-renders
 *   with a new `value` (e.g. after reset), the picker should
 *   reflect that. We sync the displayed label via useEffect.
 *
 * Usage:
 *   <ProjectPicker
 *     companyId={activeCompany?.id}
 *     value={form.projectId}
 *     onChange={(id) => setForm({ ...form, projectId: id })}
 *   />
 */
import { useEffect, useRef, useState } from "react";
import { Search, X, FolderKanban, ChevronDown, Loader2 } from "lucide-react";
import { api, getErrorMessage } from "@/lib/api";
import { cn } from "@/lib/utils";

interface Project {
  id: string;
  code: string;
  name: string;
  nameAr?: string;
  status: string;
}

interface Props {
  companyId?: string;
  value: string | null | undefined;
  onChange: (id: string | null) => void;
  /** Show "All projects" pseudo-option at top of list. */
  includeAllOption?: boolean;
  /** Disable the input (e.g. while parent form is submitting). */
  disabled?: boolean;
  /** Render an empty top row (used in filter dropdowns, not in forms). */
  placeholder?: string;
  className?: string;
}

export default function ProjectPicker({
  companyId,
  value,
  onChange,
  includeAllOption = false,
  disabled = false,
  placeholder = "ابحث عن مشروع...",
  className
}: Props) {
  const [open, setOpen] = useState(false);
  const [projects, setProjects] = useState<Project[]>([]);
  const [loading, setLoading] = useState(false);
  const [search, setSearch] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [highlight, setHighlight] = useState(0);
  const wrapRef = useRef<HTMLDivElement | null>(null);
  const inputRef = useRef<HTMLInputElement | null>(null);

  // Load the project list when the picker is first opened.
  // We deliberately don't pre-load on mount — invoices/JE forms
  // can mount the picker without ever opening it (e.g. the user
  // hits Cancel and never touches the project field). Saves a
  // roundtrip on every form render.
  useEffect(() => {
    if (!open || !companyId) return;
    if (projects.length > 0) return; // already loaded
    setLoading(true);
    setError(null);
    api
      .get(`/projects?companyId=${companyId}&limit=200`)
      .then((res) => setProjects(res.data || []))
      .catch((err) => setError(getErrorMessage(err)))
      .finally(() => setLoading(false));
  }, [open, companyId, projects.length]);

  // Click-outside-to-close
  useEffect(() => {
    if (!open) return;
    const onDoc = (e: MouseEvent) => {
      if (!wrapRef.current) return;
      if (!wrapRef.current.contains(e.target as Node)) setOpen(false);
    };
    document.addEventListener("mousedown", onDoc);
    return () => document.removeEventListener("mousedown", onDoc);
  }, [open]);

  // Selected project (if any) — kept in sync when the parent
  // changes the value externally.
  const selected = projects.find((p) => p.id === value);

  // Filter by search text (code, name, nameAr)
  const filtered = projects.filter((p) => {
    if (!search) return true;
    const q = search.toLowerCase();
    return (
      p.code.toLowerCase().includes(q) ||
      p.name.toLowerCase().includes(q) ||
      (p.nameAr || "").toLowerCase().includes(q)
    );
  });

  // Keyboard nav: ↑ ↓ Enter Esc
  const onKey = (e: React.KeyboardEvent) => {
    if (e.key === "ArrowDown") {
      e.preventDefault();
      setHighlight((h) => Math.min(h + 1, filtered.length - (includeAllOption ? 0 : 1)));
    } else if (e.key === "ArrowUp") {
      e.preventDefault();
      setHighlight((h) => Math.max(h - 1, 0));
    } else if (e.key === "Enter") {
      e.preventDefault();
      const idx = highlight;
      if (includeAllOption && idx === 0) {
        onChange(null);
        setOpen(false);
        setSearch("");
      } else {
        const realIdx = includeAllOption ? idx - 1 : idx;
        const p = filtered[realIdx];
        if (p) {
          onChange(p.id);
          setOpen(false);
          setSearch("");
        }
      }
    } else if (e.key === "Escape") {
      setOpen(false);
    }
  };

  return (
    <div ref={wrapRef} className={cn("relative", className)}>
      {/* Display row: looks like a select but is a button + clear */}
      <div
        className={cn(
          "flex items-center gap-1 w-full px-3 py-2 border border-edge rounded-md text-sm bg-canvas dark:bg-neutral-900",
          disabled && "opacity-50 cursor-not-allowed",
          !disabled && "cursor-pointer hover:border-edge"
        )}
        onClick={() => {
          if (disabled) return;
          setOpen((o) => !o);
          setTimeout(() => inputRef.current?.focus(), 0);
        }}
      >
        <FolderKanban size={14} className="text-ink-muted shrink-0" />
        {selected ? (
          <div className="flex-1 truncate text-right">
            <span className="font-mono text-xs text-ink-muted ml-1">{selected.code}</span>
            <span className="text-sm">{selected.nameAr || selected.name}</span>
          </div>
        ) : (
          <span className="flex-1 text-ink-muted text-right">— بدون مشروع —</span>
        )}
        {value && !disabled && (
          <button
            type="button"
            onClick={(e) => {
              e.stopPropagation();
              onChange(null);
              setSearch("");
            }}
            className="text-ink-subtle hover:text-red-600 shrink-0"
            aria-label="مسح"
            title="مسح الاختيار"
          >
            <X size={14} />
          </button>
        )}
        <ChevronDown size={14} className={cn("text-ink-subtle shrink-0 transition-transform", open && "rotate-180")} />
      </div>

      {open && (
        <div className="absolute z-30 right-0 left-0 mt-1 bg-canvas dark:bg-neutral-900 border border-edge rounded-md shadow-lg max-h-72 overflow-hidden flex flex-col">
          {/* Search box */}
          <div className="p-2 border-b border-edge">
            <div className="flex items-center gap-1 px-2 py-1 border border-edge rounded">
              <Search size={14} className="text-ink-subtle" />
              <input
                ref={inputRef}
                value={search}
                onChange={(e) => {
                  setSearch(e.target.value);
                  setHighlight(includeAllOption ? 0 : 0);
                }}
                onKeyDown={onKey}
                placeholder={placeholder}
                className="flex-1 text-sm outline-none bg-transparent"
              />
            </div>
          </div>

          {/* List */}
          <div className="flex-1 overflow-y-auto">
            {loading ? (
              <div className="flex items-center justify-center py-4 text-ink-muted text-sm gap-2">
                <Loader2 className="animate-spin" size={16} />
                جاري التحميل...
              </div>
            ) : error ? (
              <div className="p-3 text-sm text-red-700">{error}</div>
            ) : filtered.length === 0 ? (
              <div className="p-3 text-sm text-ink-muted text-center">
                {projects.length === 0 ? "لا توجد مشاريع" : "لا نتائج"}
              </div>
            ) : (
              <ul role="listbox">
                {includeAllOption && (
                  <li
                    role="option"
                    onClick={() => {
                      onChange(null);
                      setOpen(false);
                      setSearch("");
                    }}
                    className={cn(
                      "px-3 py-2 cursor-pointer text-sm hover:bg-raised",
                      !value && "bg-primary-50"
                    )}
                  >
                    <span className="text-ink-muted">— جميع المشاريع —</span>
                  </li>
                )}
                {filtered.map((p, i) => {
                  const active = p.id === value;
                  return (
                    <li
                      key={p.id}
                      role="option"
                      onClick={() => {
                        onChange(p.id);
                        setOpen(false);
                        setSearch("");
                      }}
                      className={cn(
                        "px-3 py-2 cursor-pointer text-sm hover:bg-raised flex items-center gap-2",
                        active && "bg-primary-50"
                      )}
                    >
                      <FolderKanban size={12} className="text-ink-subtle shrink-0" />
                      <span className="font-mono text-xs text-ink-muted shrink-0">{p.code}</span>
                      <span className="truncate flex-1 text-right">{p.nameAr || p.name}</span>
                    </li>
                  );
                })}
              </ul>
            )}
          </div>
        </div>
      )}

      {/* Hidden input so the value is in the form payload even though
          we render a click-to-open dropdown. Actually, the parent owns
          `value` state and passes it in the api.post() body, so we
          don't need a hidden input here. */}
    </div>
  );
}
