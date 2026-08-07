"use client";

import { useState } from "react";
import {
  ChevronDown, ChevronLeft, Plus, Check, X, Wallet
} from "lucide-react";
import { cn, formatNumber } from "@/lib/utils";
import type { Account } from "@/lib/types";

/**
 * Recursive tree view for the chart of accounts.
 *
 * Sprint 26 rewrite of the previous flat-table view. The user
 * asked for a 4-level nested tree that mirrors the `parentId`
 * hierarchy. Each node shows:
 *   - expand/collapse chevron (only if has children)
 *   - account code (monospace, bold)
 *   - name (Arabic preferred, English fallback)
 *   - type badge (Asset/Liability/Equity/Revenue/Expense)
 *   - nature badge (Debit/Credit)
 *   - balance (right-aligned, monospace)
 *   - isPostable badge (green ✓ or grey ✗)
 *   - `[+]` button to create a child account
 *
 * Indentation = 24px × level. Connecting lines are drawn with
 * a left border on the children wrapper — pure CSS, no SVG.
 *
 * The component is purely presentational: it receives the flat
 * account list and the `onAddChild` callback, and it builds the
 * tree internally via `buildTree()`.
 */

const TYPE_LABELS: Record<string, string> = {
  Asset: "أصول",
  Liability: "خصوم",
  Equity: "حقوق ملكية",
  Revenue: "إيرادات",
  Expense: "مصروفات"
};

const TYPE_BADGE: Record<string, string> = {
  Asset: "bg-green-100 text-green-700 border-green-200",
  Liability: "bg-red-100 text-red-700 border-red-200",
  Equity: "bg-blue-100 text-blue-700 border-blue-200",
  Revenue: "bg-purple-100 text-purple-700 border-purple-200",
  Expense: "bg-orange-100 text-orange-700 border-orange-200"
};

const NATURE_BADGE: Record<string, string> = {
  Debit: "bg-sky-100 text-sky-700",
  Credit: "bg-amber-100 text-amber-700"
};

interface TreeNode {
  account: Account;
  children: TreeNode[];
}

function buildTree(flat: Account[]): TreeNode[] {
  // Group by parentId. Accounts whose parent is missing
  // (orphan) are treated as roots so they still surface in the UI.
  const byId = new Map<string, TreeNode>();
  flat.forEach((a) => byId.set(a.id, { account: a, children: [] }));

  const roots: TreeNode[] = [];
  flat.forEach((a) => {
    const node = byId.get(a.id)!;
    if (a.parentId && byId.has(a.parentId)) {
      byId.get(a.parentId)!.children.push(node);
    } else {
      roots.push(node);
    }
  });

  // Sort siblings by code (lexical) so the tree is deterministic.
  const sortRecursive = (n: TreeNode) => {
    n.children.sort((x, y) =>
      x.account.code.localeCompare(y.account.code, undefined, { numeric: true })
    );
    n.children.forEach(sortRecursive);
  };
  roots.sort((x, y) =>
    x.account.code.localeCompare(y.account.code, undefined, { numeric: true })
  );
  roots.forEach(sortRecursive);
  return roots;
}

interface AccountTreeProps {
  accounts: Account[];
  /** Called when the user clicks the `+` button on a row. */
  onAddChild: (parent: Account) => void;
  /**
   * Optional initial-expanded state. By default NOTHING is expanded
   * (the user sees only the L1 root nodes — the 6 account classes
   * like 1-أصول, 2-التزامات, etc.). They click to drill down.
   *
   * The "Expand All" button at the top reveals the full tree.
   */
  initialExpanded?: (a: Account) => boolean;
}

export default function AccountTree({
  accounts,
  onAddChild,
  initialExpanded
}: AccountTreeProps) {
  const tree = buildTree(accounts);
  // Sprint 33 — Default state: NOTHING is expanded. The user sees
  // only the 6 L1 root nodes (الأصول، الالتزامات، حقوق الملكية،
  // الإيرادات، المصروفات، حسابات المراجعة). They click the chevron
  // to drill down: L1 → L2 → L3 → L4.
  //
  // The previous behavior of auto-expanding L1/L2/L3 was overwhelming
  // for a clean COA (78 accounts visible at once). The new default
  // makes the tree feel like the reference system — collapsed by
  // default, expanded on demand.
  const [expanded, setExpanded] = useState<Set<string>>(() => {
    const initial = new Set<string>();
    accounts.forEach((a) => {
      if (initialExpanded) {
        if (initialExpanded(a)) initial.add(a.id);
      }
      // No auto-expand for any level. User clicks to drill down.
    });
    return initial;
  });

  const toggle = (id: string) => {
    setExpanded((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  };

  const expandAll = () => setExpanded(new Set(accounts.map((a) => a.id)));
  const collapseAll = () => setExpanded(new Set());

  if (tree.length === 0) {
    return (
      <div className="text-center py-12 text-gray-500">
        <Wallet size={48} className="mx-auto mb-3 text-gray-300" />
        <p>لا توجد حسابات</p>
      </div>
    );
  }

  return (
    <div>
      <div className="flex items-center justify-end gap-2 mb-2 text-xs">
        <button
          onClick={expandAll}
          className="text-primary-600 hover:text-primary-800 hover:underline"
        >
          فتح الكل
        </button>
        <span className="text-gray-300">|</span>
        <button
          onClick={collapseAll}
          className="text-primary-600 hover:text-primary-800 hover:underline"
        >
          طي الكل
        </button>
      </div>

      <div className="border border-gray-200 rounded-md overflow-hidden">
        {tree.map((node) => (
          <TreeNodeView
            key={node.account.id}
            node={node}
            level={1}
            expanded={expanded}
            onToggle={toggle}
            onAddChild={onAddChild}
          />
        ))}
      </div>
    </div>
  );
}

interface TreeNodeViewProps {
  node: TreeNode;
  level: number;
  expanded: Set<string>;
  onToggle: (id: string) => void;
  onAddChild: (parent: Account) => void;
}

function TreeNodeView({
  node, level, expanded, onToggle, onAddChild
}: TreeNodeViewProps) {
  const { account, children } = node;
  const isExpanded = expanded.has(account.id);
  const hasChildren = children.length > 0;
  // In RTL, "more indent" pushes the content toward the LEFT
  // side of the page (because the right edge is the start of
  // the row). We use padding-inline-start so the indent flips
  // correctly when the direction changes.
  const indentPx = (level - 1) * 24;

  return (
    <div>
      {/* The row itself */}
      <div
        className={cn(
          "group flex items-center gap-2 py-1.5 px-3 border-b border-gray-100 hover:bg-gray-50 transition-colors",
          !account.isActive && "opacity-50"
        )}
        style={{ paddingInlineStart: `${0.75 + indentPx / 16}rem` }}
      >
        {/* Expand/collapse chevron */}
        {hasChildren ? (
          <button
            onClick={() => onToggle(account.id)}
            className="w-5 h-5 flex items-center justify-center text-gray-500 hover:text-gray-800"
            aria-label={isExpanded ? "طي" : "فتح"}
          >
            {isExpanded ? (
              <ChevronDown size={14} />
            ) : (
              // RTL: arrow points left when collapsed (toward the
              // children which are indented to the left in RTL).
              <ChevronLeft size={14} />
            )}
          </button>
        ) : (
          <span className="w-5 h-5" />
        )}

        {/* Code */}
        <span
          className="font-mono font-semibold text-sm text-gray-900 min-w-[60px]"
          dir="ltr"
        >
          {account.code}
        </span>

        {/* Name (Arabic preferred) */}
        <span className="text-sm text-gray-800 flex-1 truncate">
          {account.nameAr || account.name}
          {account.nameAr && (
            <span className="text-xs text-gray-400 mr-2" dir="ltr">
              ({account.name})
            </span>
          )}
        </span>

        {/* Type badge */}
        <span
          className={cn(
            "badge text-xs border",
            TYPE_BADGE[account.accountType] || "bg-gray-100 text-gray-700 border-gray-200"
          )}
        >
          {TYPE_LABELS[account.accountType] || account.accountType}
        </span>

        {/* Nature badge */}
        <span
          className={cn(
            "badge text-xs",
            NATURE_BADGE[account.nature] || "bg-gray-100 text-gray-700"
          )}
        >
          {account.nature === "Debit" ? "مدين" : "دائن"}
        </span>

        {/* IsPostable badge */}
        <span
          className={cn(
            "flex items-center gap-1 text-xs px-2 py-0.5 rounded-full border",
            account.isPostable
              ? "bg-green-50 text-green-700 border-green-200"
              : "bg-gray-50 text-gray-500 border-gray-200"
          )}
          title={
            account.isPostable
              ? "يقبل الترحيل المباشر"
              : "حساب تجميعي — لا يقبل الترحيل"
          }
        >
          {account.isPostable ? (
            <Check size={10} />
          ) : (
            <X size={10} />
          )}
          {account.isPostable ? "قابل للترحيل" : "تجميعي"}
        </span>

        {/* Balance */}
        <span
          className={cn(
            "font-mono text-sm font-semibold min-w-[100px] text-left",
            account.balance > 0.01
              ? "text-gray-900"
              : account.balance < -0.01
              ? "text-red-600"
              : "text-gray-400"
          )}
          dir="ltr"
        >
          {formatNumber(account.balance)}
        </span>

        {/* Add child button — only enable for accounts that can
            have a child (i.e. the new account is at most L3, since
            L4 is the deepest). The backend will also enforce this. */}
        <button
          onClick={() => onAddChild(account)}
          disabled={account.level >= 4}
          className={cn(
            "w-7 h-7 flex items-center justify-center rounded-md transition-colors",
            account.level >= 4
              ? "text-gray-300 cursor-not-allowed"
              : "text-primary-600 hover:bg-primary-50 opacity-0 group-hover:opacity-100"
          )}
          title={
            account.level >= 4
              ? "لا يمكن إضافة حساب فرعي لحساب تفصيلي (L4)"
              : "إضافة حساب فرعي"
          }
        >
          <Plus size={14} />
        </button>
      </div>

      {/* Children */}
      {isExpanded && hasChildren && (
        <div className="relative">
          {/* The vertical connector line. We position it at the
              indent boundary so each level's children share a
              visual "spine". */}
          <div
            className="absolute top-0 bottom-0 w-px bg-gray-200"
            style={{ insetInlineStart: `${0.75 + (indentPx + 12) / 16}rem` }}
            aria-hidden
          />
          {children.map((child) => (
            <TreeNodeView
              key={child.account.id}
              node={child}
              level={level + 1}
              expanded={expanded}
              onToggle={onToggle}
              onAddChild={onAddChild}
            />
          ))}
        </div>
      )}
    </div>
  );
}
