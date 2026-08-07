"use client";

import { useState, useMemo, useCallback, useEffect } from "react";
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
  Equity: "bg-primary-100 text-primary-700 border-primary-200",
  Revenue: "bg-purple-100 text-purple-700 border-purple-200",
  Expense: "bg-amber-100 text-amber-700 border-amber-200"
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
  // Defensive: some backend versions don't include parentId in the response.
  // In that case, derive it from the account code:
  //   L1: 1 digit                (e.g. "1")
  //   L2: 2 digits → first 1     (e.g. "11" → "1")
  //   L3: 4 digits → first 2     (e.g. "1101" → "11")
  //   L4: {parentCode}-{suffix}  (e.g. "1103-CUST-001" → "1103")
  const deriveParent = (a: Account): string | null => {
    if (a.parentId) return a.parentId;
    const code = a.code;
    if (a.level === 1 || code.length === 1) return null;
    if (a.level === 4 || code.includes("-")) {
      return code.split("-")[0];
    }
    if (a.level === 2) return code.substring(0, 1);
    if (a.level === 3) return code.substring(0, 2);
    return null;
  };

  // Decorate each account with a derived parentCode
  const decorated = flat.map((a) => ({
    ...a,
    _derivedParentId: deriveParent(a)
  }));

  const byId = new Map<string, TreeNode>();
  decorated.forEach((a) => byId.set(a.id, { account: a, children: [] }));

  // Build parentId → accountId lookup using code (for the fallback case)
  const byCode = new Map<string, string>();
  decorated.forEach((a) => byCode.set(a.code, a.id));

  const roots: TreeNode[] = [];
  decorated.forEach((a) => {
    const node = byId.get(a.id)!;
    let parentId: string | null | undefined = a.parentId;
    if (!parentId && a._derivedParentId) {
      // Derive from code
      parentId = byCode.get(a._derivedParentId) ?? null;
    }
    if (parentId && byId.has(parentId)) {
      byId.get(parentId)!.children.push(node);
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

  // Debug: log tree state whenever it changes (helps diagnose "buttons don't work" issues)
  useEffect(() => {
    const countParents = (n: TreeNode): number =>
      n.children.length + n.children.reduce((s, c) => s + countParents(c), 0);
    const totalNodes = (n: TreeNode): number =>
      1 + n.children.reduce((s, c) => s + totalNodes(c), 0);
    const flat = tree.flatMap((n) => [n, ...collectDescendantIds(n).map((id) => ({ account: { id } as any, children: [] }))]);
    // eslint-disable-next-line no-console
    console.log(
      `[AccountTree] tree built: ${tree.length} L1 roots, ${totalNodes(tree[0] || { account: {} as any, children: [] })} total nodes, expanded=${Object.keys(expanded).length} of ${accounts.length}`
    );
  }, [tree, expanded, accounts.length]);

  // Sync the count of parents with children
  const parentCount = useMemo(() => {
    let n = 0;
    const walk = (nodes: TreeNode[]) => {
      nodes.forEach((x) => {
        if (x.children.length > 0) n += 1;
        walk(x.children);
      });
    };
    walk(tree);
    return n;
  }, [tree]);

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
      <div className="text-center py-12 text-ink-muted">
        <Wallet size={48} className="mx-auto mb-3 text-ink-subtle" />
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
          className="px-3 py-1.5 text-xs bg-raised hover:bg-raised text-ink-muted rounded border border-edge cursor-pointer font-medium flex items-center gap-1 transition-colors"
          title="طي جميع الحسابات وإبقاء L1 فقط"
        >
          <Folder size={14} />
          طي الكل
        </button>
      </div>

      {/* The tree itself */}
      <div className="border border-edge rounded-md overflow-hidden bg-canvas dark:bg-neutral-900">
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
          "group flex items-center gap-2 py-1.5 px-3 border-b border-edge hover:bg-raised transition-colors",
          !account.isActive && "opacity-50"
        )}
        style={{ paddingInlineStart: `${0.75 + indentPx / 16}rem` }}
      >
        {/* Chevron — single-level toggle */}
        {hasChildren ? (
          <button
            type="button"
            onClick={handleChevronClick}
            className="w-5 h-5 flex items-center justify-center text-ink-muted hover:text-ink-strong hover:bg-raised rounded transition-colors cursor-pointer"
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
          <span className="w-6 h-6 inline-flex items-center justify-center text-ink-subtle">
            <FileText size={14} />
          </span>
        )}

        {/* Code */}
        <span className="font-mono text-sm font-semibold text-ink-muted min-w-[60px]">
          {account.code}
        </span>

        {/* Type badge */}
        <span className={cn(
          "text-[10px] px-1.5 py-0.5 rounded border",
          TYPE_BADGE[account.accountType] || "bg-raised text-ink-muted"
        )}>
          {TYPE_LABELS[account.accountType] || account.accountType}
        </span>

        {/* Name (Arabic preferred) */}
        <span className="text-sm text-ink-strong flex-1 truncate" dir="rtl">
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
              : "bg-raised text-ink-muted"
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
            "text-ink-subtle"
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
