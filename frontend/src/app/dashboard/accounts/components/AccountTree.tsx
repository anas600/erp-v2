"use client";

import { useState, useMemo, useCallback } from "react";
import {
  ChevronDown, ChevronLeft, ChevronRight, Folder, FolderOpen,
  Plus, Check, X, Wallet, FileText
} from "lucide-react";
import { cn, formatNumber } from "@/lib/utils";
import type { Account } from "@/lib/types";

/**
 * AccountTree — Sprint 33 complete rewrite.
 *
 * The previous version had bugs:
 *   1. expandAll/collapseAll buttons were not reliably toggling state
 *   2. No per-node folder icon for expand/collapse of subtrees
 *   3. State management was fragile (Set mutated, rebuild on every render)
 *
 * This rewrite:
 *   1. Uses useState with a clear shape: `Record<string, boolean>` for the
 *      expand map. Keys are account IDs, values are booleans.
 *   2. Memoizes the tree via useMemo so it's stable across renders.
 *   3. Adds a dedicated **folder icon** on every parent node. Clicking
 *      the folder toggles the expand state of THAT subtree only
 *      (L1 → expands to show L2, clicking again collapses to L1 only).
 *   4. Keeps the "Expand All" / "Collapse All" buttons as a
 *      global convenience, with explicit console logging so we
 *      can verify the handler fires.
 *   5. Recursive node component is self-contained and uses
 *      useCallback to avoid unnecessary re-renders.
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

// Returns all descendant account IDs (recursive) of the given root ID.
function collectDescendantIds(node: TreeNode, acc: string[] = []): string[] {
  node.children.forEach((c) => {
    acc.push(c.account.id);
    collectDescendantIds(c, acc);
  });
  return acc;
}

interface AccountTreeProps {
  accounts: Account[];
  onAddChild: (parent: Account) => void;
}

export default function AccountTree({ accounts, onAddChild }: AccountTreeProps) {
  // Memoize the tree so it's stable across renders.
  const tree = useMemo(() => buildTree(accounts), [accounts]);

  // Expand state: { [accountId]: true/false }
  // Default: nothing expanded. User clicks the folder (or chevron) to drill down.
  const [expanded, setExpanded] = useState<Record<string, boolean>>({});

  // Toggle a single node. Pure local toggle.
  const toggle = useCallback((id: string) => {
    setExpanded((prev) => ({ ...prev, [id]: !prev[id] }));
  }, []);

  // Expand a single subtree (the node + all its descendants).
  // Used by the folder-icon "expand" action.
  const expandSubtree = useCallback((rootNode: TreeNode) => {
    const ids = [rootNode.account.id, ...collectDescendantIds(rootNode)];
    setExpanded((prev) => {
      const next = { ...prev };
      ids.forEach((id) => { next[id] = true; });
      return next;
    });
  }, []);

  // Collapse a single subtree.
  const collapseSubtree = useCallback((rootNode: TreeNode) => {
    const ids = [rootNode.account.id, ...collectDescendantIds(rootNode)];
    setExpanded((prev) => {
      const next = { ...prev };
      ids.forEach((id) => { next[id] = false; });
      return next;
    });
  }, []);

  // Expand all (every account in the dataset).
  const expandAll = useCallback(() => {
    const all: Record<string, boolean> = {};
    accounts.forEach((a) => { all[a.id] = true; });
    setExpanded(all);
    // eslint-disable-next-line no-console
    console.log("[AccountTree] expandAll: expanded", accounts.length, "accounts");
  }, [accounts]);

  // Collapse all.
  const collapseAll = useCallback(() => {
    setExpanded({});
    // eslint-disable-next-line no-console
    console.log("[AccountTree] collapseAll: collapsed to L1 only");
  }, []);

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
      {/* Global expand/collapse controls */}
      <div className="flex items-center justify-end gap-2 mb-3 no-print">
        <button
          type="button"
          onClick={expandAll}
          data-testid="btn-expand-all"
          className="px-3 py-1.5 text-xs bg-primary-50 hover:bg-primary-100 text-primary-700 rounded border border-primary-200 cursor-pointer font-medium flex items-center gap-1 transition-colors"
          title="عرض جميع الحسابات في كل المستويات"
        >
          <FolderOpen size={14} />
          فتح الكل ({accounts.length})
        </button>
        <button
          type="button"
          onClick={collapseAll}
          data-testid="btn-collapse-all"
          className="px-3 py-1.5 text-xs bg-gray-50 hover:bg-gray-100 text-gray-700 rounded border border-gray-200 cursor-pointer font-medium flex items-center gap-1 transition-colors"
          title="طي جميع الحسابات وإبقاء L1 فقط"
        >
          <Folder size={14} />
          طي الكل
        </button>
      </div>

      {/* The tree itself */}
      <div className="border border-gray-200 rounded-md overflow-hidden bg-white">
        {tree.map((node) => (
          <TreeNodeView
            key={node.account.id}
            node={node}
            level={1}
            expanded={expanded}
            onToggle={toggle}
            onExpandSubtree={expandSubtree}
            onCollapseSubtree={collapseSubtree}
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
  expanded: Record<string, boolean>;
  onToggle: (id: string) => void;
  onExpandSubtree: (root: TreeNode) => void;
  onCollapseSubtree: (root: TreeNode) => void;
  onAddChild: (parent: Account) => void;
}

function TreeNodeView({
  node, level, expanded,
  onToggle, onExpandSubtree, onCollapseSubtree, onAddChild
}: TreeNodeViewProps) {
  const { account, children } = node;
  const isExpanded = !!expanded[account.id];
  const hasChildren = children.length > 0;
  // Indent: in RTL, more indent pushes content to the LEFT side.
  // We use padding-inline-start so the indent flips correctly.
  const indentPx = (level - 1) * 28;

  // Folder icon click: if expanded → collapse subtree, else → expand subtree
  const handleFolderClick = (e: React.MouseEvent) => {
    e.preventDefault();
    e.stopPropagation();
    if (isExpanded) {
      onCollapseSubtree(node);
    } else {
      onExpandSubtree(node);
    }
  };

  // Chevron click: simple single-level toggle
  const handleChevronClick = (e: React.MouseEvent) => {
    e.preventDefault();
    e.stopPropagation();
    onToggle(account.id);
  };

  return (
    <div>
      {/* The row */}
      <div
        className={cn(
          "group flex items-center gap-2 py-1.5 px-3 border-b border-gray-100 hover:bg-gray-50 transition-colors",
          !account.isActive && "opacity-50"
        )}
        style={{ paddingInlineStart: `${0.75 + indentPx / 16}rem` }}
      >
        {/* Chevron — single-level toggle */}
        {hasChildren ? (
          <button
            type="button"
            onClick={handleChevronClick}
            className="w-5 h-5 flex items-center justify-center text-gray-500 hover:text-gray-800 hover:bg-gray-200 rounded transition-colors cursor-pointer"
            aria-label={isExpanded ? "طي هذا الحساب" : "فتح هذا الحساب"}
            title={isExpanded ? "طي هذا الحساب" : "فتح هذا الحساب"}
          >
            {isExpanded ? <ChevronDown size={14} /> : <ChevronLeft size={14} />}
          </button>
        ) : (
          <span className="w-5 h-5 inline-block" />
        )}

        {/* Folder icon — expand/collapse the entire subtree */}
        {hasChildren ? (
          <button
            type="button"
            onClick={handleFolderClick}
            className={cn(
              "w-6 h-6 flex items-center justify-center rounded cursor-pointer transition-colors",
              isExpanded
                ? "text-amber-600 hover:bg-amber-100"
                : "text-amber-500 hover:bg-amber-50"
            )}
            aria-label={isExpanded ? "طي كل المجموعات الفرعية" : "فتح كل المجموعات الفرعية"}
            title={isExpanded ? "طي كل المجموعات الفرعية لهذا الحساب" : `فتح كل المجموعات الفرعية لـ ${account.nameAr || account.name}`}
            data-testid={`folder-${account.code}`}
          >
            {isExpanded ? <FolderOpen size={16} /> : <Folder size={16} />}
          </button>
        ) : (
          <span className="w-6 h-6 inline-flex items-center justify-center text-gray-300">
            <FileText size={14} />
          </span>
        )}

        {/* Code */}
        <span className="font-mono text-sm font-semibold text-gray-700 min-w-[60px]">
          {account.code}
        </span>

        {/* Type badge */}
        <span className={cn(
          "text-[10px] px-1.5 py-0.5 rounded border",
          TYPE_BADGE[account.accountType] || "bg-gray-100 text-gray-700"
        )}>
          {TYPE_LABELS[account.accountType] || account.accountType}
        </span>

        {/* Name (Arabic preferred) */}
        <span className="text-sm text-gray-900 flex-1 truncate" dir="rtl">
          {account.nameAr || account.name}
        </span>

        {/* Nature badge */}
        {account.nature && (
          <span className={cn(
            "text-[10px] px-1.5 py-0.5 rounded",
            NATURE_BADGE[account.nature]
          )}>
            {account.nature === "Debit" ? "مدين" : "دائن"}
          </span>
        )}

        {/* IsPostable indicator */}
        <span
          className={cn(
            "text-[10px] px-1.5 py-0.5 rounded flex items-center gap-1",
            account.isPostable
              ? "bg-emerald-100 text-emerald-700"
              : "bg-gray-100 text-gray-500"
          )}
          title={account.isPostable ? "قابل للترحيل (يمكن إدراج قيود عليه)" : "تجميعي (لا يقبل قيود مباشرة)"}
        >
          {account.isPostable ? <Check size={10} /> : <X size={10} />}
          {account.isPostable ? "يُرحَّل" : "تجميعي"}
        </span>

        {/* Balance */}
        <span
          className={cn(
            "font-mono text-sm min-w-[100px] text-left",
            account.balance > 0 ? "text-emerald-700 font-semibold" :
            account.balance < 0 ? "text-red-700 font-semibold" :
            "text-gray-400"
          )}
          dir="ltr"
        >
          {formatNumber(account.balance)}
        </span>

        {/* + button to add child account */}
        {hasChildren || true ? (
          <button
            type="button"
            onClick={(e) => { e.preventDefault(); e.stopPropagation(); onAddChild(account); }}
            className="opacity-0 group-hover:opacity-100 w-6 h-6 flex items-center justify-center text-primary-600 hover:bg-primary-100 rounded transition-all cursor-pointer"
            title="إضافة حساب فرعي"
            aria-label="إضافة حساب فرعي"
          >
            <Plus size={14} />
          </button>
        ) : null}
      </div>

      {/* Children — render only if expanded */}
      {hasChildren && isExpanded && (
        <div>
          {children.map((child) => (
            <TreeNodeView
              key={child.account.id}
              node={child}
              level={level + 1}
              expanded={expanded}
              onToggle={onToggle}
              onExpandSubtree={onExpandSubtree}
              onCollapseSubtree={onCollapseSubtree}
              onAddChild={onAddChild}
            />
          ))}
        </div>
      )}
    </div>
  );
}
