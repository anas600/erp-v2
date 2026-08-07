"use client";

import { useEffect, useMemo, useState } from "react";
import { X, Loader2, AlertCircle, CheckCircle2, Info } from "lucide-react";
import { api, getErrorMessage } from "@/lib/api";
import type { Account, CreateAccountRequest } from "@/lib/types";

/**
 * Create-account modal (Sprint 26).
 *
 * The modal knows the 4-level COA rules and auto-derives:
 *   - level      = parent.level + 1 (or 1 if no parent)
 *   - code       = parent.code + "-" + subCode input
 *   - isPostable = per-level rule (L1/L2/L3 forced false, L4
 *                  forced true). The checkbox is disabled and
 *                  labelled on every level — the user sees the
 *                  rule but cannot violate it.
 *
 * The "parent account" dropdown is filtered to "valid parents":
 *   - Level 1: no parent (it's the logical type — actually, the
 *     first real account row is L2 with no parent).
 *   - Level 2: no parent.
 *   - Level 3: parent = L2.
 *   - Level 4: parent = L3.
 *
 * If the caller passed a `parent` (via the `+` button on a tree
 * row), we pre-select it and lock the dropdown — the user
 * intended a specific child.
 */

interface CreateAccountModalProps {
  open: boolean;
  onClose: () => void;
  onCreated: (a: Account) => void;
  /** Optional pre-selected parent (from the tree's `+` button). */
  parent: Account | null;
  /** All existing accounts (used to populate the parent dropdown
      when the user opens the modal without a pre-selected parent). */
  accounts: Account[];
  companyId: string;
}

const TYPE_OPTIONS = [
  { v: "Asset",     l: "أصول" },
  { v: "Liability", l: "خصوم" },
  { v: "Equity",    l: "حقوق ملكية" },
  { v: "Revenue",   l: "إيرادات" },
  { v: "Expense",   l: "مصروفات" }
];

/**
 * Derive the *forced* isPostable for a level.
 *
 * Sprint 31 — L3 is now also forced to non-postable. Only L4 can
 * receive direct journal entries. L1/L2/L3 are aggregators only
 * (the rollup is computed from L4 movements).
 */
function forcedIsPostable(level: number): boolean | null {
  if (level === 1 || level === 2 || level === 3) return false; // forced false (header only)
  if (level === 4) return true;                                 // forced true (postable)
  return null;
}

/** Compute the level of a new account given a parent. */
function computeLevel(parent: Account | null): number {
  if (!parent) return 2; // L2 is the first real account row
  return Math.min(parent.level + 1, 4);
}

export default function CreateAccountModal({
  open, onClose, onCreated, parent, accounts, companyId
}: CreateAccountModalProps) {
  // ─── Form state ──────────────────────────────────────────────────────
  const [parentId, setParentId] = useState<string>(parent?.id || "");
  const [subCode, setSubCode] = useState<string>("");
  const [name, setName] = useState("");
  const [nameAr, setNameAr] = useState("");
  const [accountType, setAccountType] = useState<string>("Asset");
  const [nature, setNature] = useState<string>("Debit");
  const [isPostable, setIsPostable] = useState<boolean>(true);

  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [success, setSuccess] = useState<string | null>(null);

  // ─── Derived values ──────────────────────────────────────────────────
  // When a parent is pre-selected, lock the dropdown. Otherwise the
  // user can pick any valid parent (L1 not allowed, L4 not allowed).
  const parentLocked = !!parent;

  const selectedParent = useMemo<Account | null>(() => {
    if (!parentId) return null;
    return accounts.find((a) => a.id === parentId) || null;
  }, [parentId, accounts]);

  const level = computeLevel(selectedParent);

  // The forced isPostable for this level. If null, the user picks.
  const forcedPostable = forcedIsPostable(level);

  // Auto-suggested code: parent.code + "-" + subCode (or just subCode
  // for L2 with no parent).
  const fullCode = selectedParent
    ? `${selectedParent.code}-${subCode}`
    : subCode;

  // Auto-suggest nature from the parent's type (override at L2).
  // Asset/Expense → Debit, Liability/Equity/Revenue → Credit.
  useEffect(() => {
    if (selectedParent) {
      setAccountType(selectedParent.accountType);
      setNature(selectedParent.nature);
    }
  }, [selectedParent?.id]); // eslint-disable-line react-hooks/exhaustive-deps

  // Reset on open.
  useEffect(() => {
    if (open) {
      setParentId(parent?.id || "");
      setSubCode("");
      setName("");
      setNameAr("");
      setAccountType(parent?.accountType || "Asset");
      setNature(parent?.nature || "Debit");
      const fp = forcedIsPostable(computeLevel(parent));
      setIsPostable(fp === null ? true : fp);
      setError(null);
      setSuccess(null);
    }
  }, [open, parent]); // eslint-disable-line react-hooks/exhaustive-deps

  if (!open) return null;

  // ─── Valid parent accounts for the dropdown ──────────────────────────
  // A new account is at level = parent.level + 1. So a valid parent
  // is any account whose level is `newLevel - 1`. The L2 option (no
  // parent) is always available.
  const newLevel = level;
  const validParents = accounts
    .filter((a) => a.level === newLevel - 1)
    .sort((x, y) => x.code.localeCompare(y.code, undefined, { numeric: true }));

  // ─── Submit ──────────────────────────────────────────────────────────
  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!fullCode) {
      setError("أدخل الكود الفرعي أولاً");
      return;
    }
    if (!name) {
      setError("الاسم بالإنجليزية مطلوب");
      return;
    }
    setSubmitting(true);
    setError(null);
    setSuccess(null);
    try {
      const finalIsPostable = forcedPostable === null ? isPostable : forcedPostable;
      const accountClass = level === 2 ? "header" : "detail";
      const req: CreateAccountRequest = {
        companyId,
        code: fullCode,
        name,
        nameAr: nameAr || undefined,
        parentId: selectedParent?.id || null,
        accountType,
        nature,
        level: newLevel,
        isPostable: finalIsPostable,
        accountClass
      };
      const res = await api.post("/accounts", req);
      setSuccess(`تم إنشاء الحساب ${fullCode}`);
      // Pass the new account up; the parent refreshes its list.
      onCreated(res.data);
      // Close after a short delay so the user sees the success.
      setTimeout(() => onClose(), 700);
    } catch (err) {
      setError(getErrorMessage(err));
    } finally {
      setSubmitting(false);
    }
  };

  // ─── Render ──────────────────────────────────────────────────────────
  const levelLabel: Record<number, string> = {
    1: "L1 (نوع)",
    2: "L2 (فئة)",
    3: "L3 (حساب تشغيلي)",
    4: "L4 (حساب تفصيلي)"
  };

  return (
    <div className="fixed inset-0 bg-black/40 flex items-center justify-center z-50 p-4">
      <div className="bg-canvas dark:bg-neutral-900 rounded-card shadow-xl w-full max-w-lg p-6 max-h-[90vh] overflow-y-auto">
        <div className="flex items-center justify-between mb-4">
          <h2 className="text-lg font-semibold">حساب جديد</h2>
          <button
            onClick={onClose}
            className="text-ink-subtle hover:text-ink-muted"
            type="button"
          >
            <X size={20} />
          </button>
        </div>

        <form onSubmit={submit} className="space-y-3">
          {/* Parent dropdown — disabled when pre-selected */}
          <div>
            <label className="block text-sm font-medium mb-1">
              الحساب الأب
              {parentLocked && (
                <span className="text-xs text-ink-muted mr-2">
                  (محجوب — تم اختياره من الشجرة)
                </span>
              )}
            </label>
            <select
              className="input"
              value={parentId}
              onChange={(e) => setParentId(e.target.value)}
              disabled={parentLocked}
            >
              <option value="">- حساب رئيسي (L2) -</option>
              {validParents.map((a) => (
                <option key={a.id} value={a.id}>
                  {a.code} — {a.nameAr || a.name} (L{a.level})
                </option>
              ))}
            </select>
          </div>

          {/* Level info */}
          <div className="flex items-center gap-2 text-xs bg-primary-50 text-primary-800 p-2 rounded">
            <Info size={14} />
            <span>
              المستوى المحسوب تلقائياً:{" "}
              <strong>{levelLabel[level] || `L${level}`}</strong>
              {selectedParent && (
                <> (أب: <span className="font-mono" dir="ltr">{selectedParent.code}</span>)</>
              )}
            </span>
          </div>

          {/* Code: parent.code + subCode (auto-suggested) */}
          <div>
            <label className="block text-sm font-medium mb-1">الكود *</label>
            <div className="flex items-stretch gap-0" dir="ltr">
              {selectedParent && (
                <span className="inline-flex items-center px-3 rounded-r-md border border-l-0 border-edge bg-raised text-ink-muted font-mono text-sm">
                  {selectedParent.code}-
                </span>
              )}
              <input
                className={cn(
                  "input font-mono",
                  selectedParent ? "rounded-l-md" : ""
                )}
                value={subCode}
                onChange={(e) => setSubCode(e.target.value)}
                required
                placeholder={selectedParent ? "01" : "1000"}
              />
            </div>
            {fullCode && (
              <p className="text-xs text-ink-muted mt-1" dir="ltr">
                الكود الكامل: <span className="font-mono font-semibold">{fullCode}</span>
              </p>
            )}
          </div>

          {/* Names */}
          <div>
            <label className="block text-sm font-medium mb-1">الاسم بالإنجليزية *</label>
            <input
              className="input"
              value={name}
              onChange={(e) => setName(e.target.value)}
              required
              dir="ltr"
              placeholder="e.g., Cash on Hand"
            />
          </div>
          <div>
            <label className="block text-sm font-medium mb-1">الاسم بالعربية</label>
            <input
              className="input"
              value={nameAr}
              onChange={(e) => setNameAr(e.target.value)}
              placeholder="النقدية في الصندوق"
            />
          </div>

          {/* Type + Nature (auto-filled from parent) */}
          <div className="grid grid-cols-2 gap-3">
            <div>
              <label className="block text-sm font-medium mb-1">النوع *</label>
              <select
                className="input"
                value={accountType}
                onChange={(e) => setAccountType(e.target.value)}
              >
                {TYPE_OPTIONS.map((o) => (
                  <option key={o.v} value={o.v}>{o.l}</option>
                ))}
              </select>
            </div>
            <div>
              <label className="block text-sm font-medium mb-1">الطبيعة *</label>
              <select
                className="input"
                value={nature}
                onChange={(e) => setNature(e.target.value)}
              >
                <option value="Debit">مدين</option>
                <option value="Credit">دائن</option>
              </select>
            </div>
          </div>

          {/* IsPostable toggle — level-aware */}
          <div className="border border-edge rounded-md p-3 bg-raised">
            <label className="flex items-start gap-2 cursor-pointer">
              <input
                type="checkbox"
                checked={isPostable}
                onChange={(e) => setIsPostable(e.target.checked)}
                disabled={forcedPostable !== null}
                className="mt-1"
              />
              <div className="flex-1">
                <div className="text-sm font-medium">
                  قابل للترحيل المباشر
                  {forcedPostable !== null && (
                    <span className="text-xs text-ink-muted mr-2">
                      ({forcedPostable
                        ? "إجباري — قابل للترحيل"
                        : "إجباري — غير قابل للترحيل"})
                    </span>
                  )}
                </div>
                <p className="text-xs text-ink-muted mt-1">
                  {level === 1 || level === 2
                    ? "الحسابات من المستوى 1 و 2 هي حسابات تجميعية فقط ولا تقبل القيود المباشرة."
                    : level === 3
                    ? "المستوى 3: الحسابات التشغيلية. فعّل هذا الخيار إذا كان الحساب يستقبل قيوداً مباشرة (مثل حساب النقدية)، أو ألغِه إذا كان مجرد تجميع فرعي لحسابات المستوى 4."
                    : "المستوى 4: الحسابات التفصيلية (الفرعية) ترتبط بجهات الاتصال وتقبل الترحيل دائماً."}
                </p>
              </div>
            </label>
          </div>

          {error && (
            <div className="p-3 bg-red-50 text-red-700 rounded-md text-sm flex items-start gap-2">
              <AlertCircle size={16} className="mt-0.5 flex-shrink-0" />
              <span>{error}</span>
            </div>
          )}
          {success && (
            <div className="p-3 bg-green-50 text-green-700 rounded-md text-sm flex items-center gap-2">
              <CheckCircle2 size={16} />
              <span>{success}</span>
            </div>
          )}

          <div className="flex gap-2 pt-2">
            <button
              type="submit"
              disabled={submitting}
              className="btn-primary flex-1"
            >
              {submitting ? (
                <><Loader2 className="animate-spin" size={16} /> جاري الحفظ...</>
              ) : (
                "حفظ"
              )}
            </button>
            <button
              type="button"
              onClick={onClose}
              className="btn-secondary"
            >
              إلغاء
            </button>
          </div>
        </form>
      </div>
    </div>
  );
}

// Inline cn helper to avoid an extra import (Tailwind merge is fine
// but we only need a couple of class joins here).
function cn(...args: (string | false | null | undefined)[]): string {
  return args.filter(Boolean).join(" ");
}
