"use client";

/**
 * Sprint 50 — Rules Engine Editor.
 *
 * Visual editor for the 6 default posting rules. The user can:
 *   - See all action lines (debit/credit + account + amount formula)
 *   - Pick the account for each line from a dropdown of L3 + L4 accounts
 *     (the dropdown pre-selects the account the rule currently uses)
 *   - Toggle nature (debit/credit)
 *   - Edit amount formula (with the standard tokens: invoice.total,
 *     invoice.tax, contact.subLedger, etc.)
 *   - Edit description
 *   - Add or remove lines
 *
 * The original Sprint 34 version was a raw JSON textarea. That worked
 * for power users but blocked everyone else. This UI surfaces the
 * same data through structured controls + a JSON preview tab.
 *
 * The editor writes back to the backend via PUT /api/rules/{id} with
 * the full rule JSON. Server-side validation enforces the schema
 * (we don't add new fields from the UI yet).
 */
import { useEffect, useState, useMemo } from "react";
import { api, getErrorMessage } from "@/lib/api";
import { useAuth } from "@/lib/auth-context";
import {
  Zap, Plus, Loader2, X, Play, Edit2, Trash2, Save,
  ChevronDown, ChevronUp, FileCode, AlertCircle, Check
} from "lucide-react";

// ---------- Types (mirror of backend) ----------

interface RuleLine {
  nature: "debit" | "credit";
  accountCode?: string;       // static code like "5301" or "5401-PRJ-005"
  accountFrom?: string;       // dynamic directive like "contact.subLedger"
  amountFormula: string;      // e.g. "invoice.total - invoice.tax" or "line.amount"
  description?: string;
}

interface RuleAction {
  type: "PostJournalEntry";
  projectFrom?: string;        // Sprint 50 — optional
  narration?: string;
  lines: RuleLine[];
}

interface RuleDef {
  conditions?: { all: Array<{ field: string; op: string; value: any }> };
  actions: RuleAction[];
}

interface Rule {
  id: string;
  name: string;
  description?: string;
  eventName: string;
  enabled: boolean;
  priority: number;
  ruleJson: string;
  isTemplate: boolean;
  createdAt: string;
  updatedAt: string;
}

interface AccountNode {
  id: string;
  code: string;
  name: string;
  nameAr?: string;
  parentId?: string;
  level: number;
  isPostable: boolean;
  isControlAccount: boolean;
  nature?: string;
  accountType?: string;
  children?: AccountNode[];
}

// ---------- Helpers ----------

/**
 * Flattens the account tree to a list of (label, id) pairs suitable for a
 * dropdown. We include L3 control accounts (e.g. "1103 — Accounts
 * Receivable") AND L4 postable sub-ledgers (e.g. "1103-CUST-001 —
 * CUST-001"). L2/L1 are excluded — they're rollups, not postable.
 */
function flattenAccounts(accounts: AccountNode[]): { id: string; code: string; label: string }[] {
  const out: { id: string; code: string; label: string }[] = [];
  const walk = (nodes: AccountNode[], depth: number) => {
    for (const a of nodes || []) {
      if (a.level === 3 || a.level === 4) {
        const indent = "—".repeat(Math.max(0, depth - 1));
        out.push({
          id: a.id,
          code: a.code,
          label: `${indent} ${a.code} — ${a.nameAr || a.name}`,
        });
      }
      if (a.children) walk(a.children, depth + 1);
    }
  };
  walk(accounts, 0);
  return out;
}

// Standard accountFrom directives the user can pick from a dropdown.
const ACCOUNT_FROM_DIRECTIVES = [
  { value: "",                              label: "(لا شيء — استخدم accountCode)" },
  { value: "contact.subLedger",             label: "contact.subLedger — حساب العميل/المورّد التفصيلي" },
  { value: "voucher.bankAccount",           label: "voucher.bankAccount — حساب البنك/الصندوق من السند" },
  { value: "line.accountCode",              label: "line.accountCode — حساب من بند الفاتورة (للمشاريع)" },
  { value: "control.ar",                    label: "control.ar — المدينون الرئيسي 1103" },
  { value: "control.ap",                    label: "control.ap — الدائنون الرئيسي 2101" },
  { value: "control.cash",                  label: "control.cash — الصندوق الافتراضي 1101-CASH-001" },
];

// Standard projectFrom directives
const PROJECT_FROM_DIRECTIVES = [
  { value: "",                              label: "(لا تنسخ — القيد غير مخصص لمشروع)" },
  { value: "invoice.projectId",             label: "invoice.projectId — مشروع الفاتورة" },
  { value: "project.id",                    label: "project.id — المشروع (alias)" },
];

// Standard event names. Add new ones as the system grows.
const EVENT_NAMES = [
  "PurchaseInvoiceApproved",
  "PurchaseInvoiceApprovedForProject",
  "SalesInvoiceApproved",
  "SupplierPaymentMade",
  "CustomerReceiptReceived",
  "ProjectBillingIssued",
  "PeriodClose",
  "ProjectMilestoneCompleted",
];

// ---------- Component ----------

export default function RulesPage() {
  const { activeCompany, user } = useAuth();
  const [rules, setRules] = useState<Rule[]>([]);
  const [loading, setLoading] = useState(true);
  const [editing, setEditing] = useState<Rule | null>(null);
  const [showForm, setShowForm] = useState(false);
  const [accounts, setAccounts] = useState<AccountNode[]>([]);
  const [saving, setSaving] = useState(false);
  const [savedOk, setSavedOk] = useState(false);
  const [testModal, setTestModal] = useState<Rule | null>(null);
  const [testPayload, setTestPayload] = useState("{\n  \"invoice\": { \"number\": \"INV-001\", \"total\": 1000, \"tax\": 0 },\n  \"supplier\": { \"name\": \"Test\" }\n}");
  const [testResult, setTestResult] = useState<string | null>(null);
  const [testing, setTesting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [showJson, setShowJson] = useState(false);

  const load = async () => {
    try {
      setLoading(true);
      const [rulesRes, acctsRes] = await Promise.all([
        api.get("/rules"),
        api.get(`/accounts?companyId=${activeCompany?.id || ""}`),
      ]);
      setRules(rulesRes.data);
      setAccounts(acctsRes.data || []);
    } catch (err) {
      setError(getErrorMessage(err));
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => { if (activeCompany) load(); }, [activeCompany?.id]);

  const flatAccounts = useMemo(() => flattenAccounts(accounts), [accounts]);
  const accountByCode = useMemo(() => {
    const m = new Map<string, AccountNode>();
    const walk = (nodes: AccountNode[]) => {
      for (const a of nodes || []) {
        m.set(a.code, a);
        if (a.children) walk(a.children);
      }
    };
    walk(accounts);
    return m;
  }, [accounts]);
  const accountById = useMemo(() => {
    const m = new Map<string, AccountNode>();
    const walk = (nodes: AccountNode[]) => {
      for (const a of nodes || []) {
        m.set(a.id, a);
        if (a.children) walk(a.children);
      }
    };
    walk(accounts);
    return m;
  }, [accounts]);

  // When the user opens the edit modal, parse ruleJson into the structured fields.
  const openEdit = (rule: Rule) => {
    try {
      const parsed: RuleDef = JSON.parse(rule.ruleJson);
      setEditing({
        ...rule,
        ruleJson: JSON.stringify(parsed, null, 2),  // re-format
      });
    } catch {
      setEditing({ ...rule });
    }
    setShowForm(true);
    setShowJson(false);
    setSavedOk(false);
  };

  const save = async () => {
    if (!editing) return;
    setSaving(true);
    setError(null);
    setSavedOk(false);
    try {
      // Re-parse the structured fields back to JSON to ensure consistency
      const parsed: RuleDef = JSON.parse(editing.ruleJson);
      const body = {
        name: editing.name,
        description: editing.description,
        eventName: editing.eventName,
        enabled: editing.enabled,
        priority: editing.priority,
        ruleJson: JSON.stringify(parsed),
      };
      if (editing.id) {
        await api.put(`/rules/${editing.id}`, body);
      } else {
        await api.post("/rules", body);
      }
      setSavedOk(true);
      setTimeout(() => { setShowForm(false); setEditing(null); setSavedOk(false); }, 800);
      await load();
    } catch (err) {
      setError(getErrorMessage(err));
    } finally {
      setSaving(false);
    }
  };

  // ------- Structured editor helpers -------

  const getDef = (): RuleDef => {
    if (!editing) return { conditions: { all: [] }, actions: [] };
    try {
      return JSON.parse(editing.ruleJson);
    } catch {
      return { conditions: { all: [] }, actions: [] };
    }
  };

  const setDef = (def: RuleDef) => {
    if (!editing) return;
    setEditing({ ...editing, ruleJson: JSON.stringify(def, null, 2) });
  };

  const updateAction = (actionIdx: number, updater: (a: RuleAction) => RuleAction) => {
    const def = getDef();
    if (!def.actions[actionIdx]) return;
    def.actions[actionIdx] = updater({ ...def.actions[actionIdx] });
    setDef(def);
  };

  const addLine = (actionIdx: number) => {
    updateAction(actionIdx, (a) => ({
      ...a,
      lines: [...a.lines, { nature: "debit", accountCode: "1101-CASH-001", amountFormula: "0", description: "" }],
    }));
  };

  const removeLine = (actionIdx: number, lineIdx: number) => {
    updateAction(actionIdx, (a) => ({
      ...a,
      lines: a.lines.filter((_, i) => i !== lineIdx),
    }));
  };

  const updateLine = (actionIdx: number, lineIdx: number, updater: (l: RuleLine) => RuleLine) => {
    updateAction(actionIdx, (a) => ({
      ...a,
      lines: a.lines.map((l, i) => (i === lineIdx ? updater({ ...l }) : l)),
    }));
  };

  // Resolve the "current account id" for a line, given the structured fields.
  // Returns the account's UUID (for the dropdown) so we can highlight the
  // current selection even after the user switches between accountFrom and
  // accountCode.
  const resolveAccountId = (line: RuleLine): string => {
    if (line.accountFrom) {
      // accountFrom directives don't have a single account — they look up
      // dynamically. The dropdown stays empty (the user reads the helper
      // text below to understand which directive they're using).
      return "";
    }
    if (line.accountCode) {
      const node = accountByCode.get(line.accountCode);
      return node?.id || "";
    }
    return "";
  };

  // Apply the user's dropdown choice to the line. We try to set BOTH
  // accountCode (the static) and accountFrom (the directive) appropriately.
  const applyAccountChoice = (actionIdx: number, lineIdx: number, accountId: string, directive: string) => {
    updateLine(actionIdx, lineIdx, (l) => {
      const next = { ...l };
      if (directive) {
        next.accountFrom = directive;
        next.accountCode = "";  // mutually exclusive
      } else if (accountId) {
        const node = accountById.get(accountId);
        if (node) {
          next.accountCode = node.code;
          next.accountFrom = "";
        }
      } else {
        next.accountCode = "";
        next.accountFrom = "";
      }
      return next;
    });
  };

  const remove = async (id: string) => {
    if (!confirm("هل أنت متأكد من حذف هذه القاعدة؟")) return;
    try {
      await api.delete(`/rules/${id}`);
      await load();
    } catch (err) {
      alert(getErrorMessage(err));
    }
  };

  const toggleEnabled = async (rule: Rule) => {
    try {
      await api.put(`/rules/${rule.id}`, {
        name: rule.name,
        description: rule.description,
        eventName: rule.eventName,
        enabled: !rule.enabled,
        priority: rule.priority,
        ruleJson: rule.ruleJson
      });
      await load();
    } catch (err) {
      alert(getErrorMessage(err));
    }
  };

  const runTest = async () => {
    if (!testModal || !activeCompany) return;
    setTesting(true);
    setTestResult(null);
    try {
      const payload = JSON.parse(testPayload);
      const res = await api.post("/rules/trigger", {
        eventName: testModal.eventName,
        payload
      });
      setTestResult(JSON.stringify(res.data, null, 2));
    } catch (err) {
      setTestResult(`ERROR: ${getErrorMessage(err)}`);
    } finally {
      setTesting(false);
    }
  };

  return (
    <div>
      <div className="flex items-center justify-between mb-6">
        <div>
          <h1 className="text-2xl font-bold text-ink-strong flex items-center gap-2">
            <Zap size={24} className="text-amber-500" />
            محرك قواعد العمل
          </h1>
          <p className="text-sm text-ink-muted mt-1">
            قوالب جاهزة للترحيل التلقائي — يمكن تعديلها أو إضافة قواعد جديدة
          </p>
        </div>
      </div>

      {error && <div className="mb-4 p-3 bg-red-50 text-red-700 rounded-md text-sm flex items-start gap-2"><AlertCircle size={16} className="mt-0.5 shrink-0" /><span>{error}</span></div>}

      <div className="card">
        {loading ? (
          <div className="flex justify-center py-8">
            <Loader2 className="animate-spin text-primary-500" size={32} />
          </div>
        ) : (
          <table className="table">
            <thead>
              <tr>
                <th>الاسم</th>
                <th>الحدث</th>
                <th>الأولوية</th>
                <th>الحالة</th>
                <th>قالب</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              {rules.map((r) => (
                <tr key={r.id}>
                  <td>
                    <div className="font-semibold">{r.name}</div>
                    <div className="text-xs text-ink-muted">{r.description}</div>
                  </td>
                  <td><code className="text-xs bg-raised px-2 py-1 rounded">{r.eventName}</code></td>
                  <td>{r.priority}</td>
                  <td>
                    {r.enabled ? (
                      <span className="badge badge-success">مفعّلة</span>
                    ) : (
                      <span className="badge badge-warning">معطلة</span>
                    )}
                  </td>
                  <td>
                    {r.isTemplate && <span className="badge badge-info">قالب</span>}
                  </td>
                  <td>
                    <div className="flex items-center gap-1">
                      <button onClick={() => { setTestModal(r); setTestResult(null); }} className="text-green-600 hover:bg-green-50 p-1 rounded" title="اختبر">
                        <Play size={14} />
                      </button>
                      <button onClick={() => openEdit(r)} className="text-primary-700 hover:bg-primary-50 p-1 rounded" title="تعديل">
                        <Edit2 size={14} />
                      </button>
                      <button onClick={() => toggleEnabled(r)} className="text-amber-600 hover:bg-amber-50 p-1 rounded text-xs">
                        {r.enabled ? "إيقاف" : "تفعيل"}
                      </button>
                      {!r.isTemplate && (
                        <button onClick={() => remove(r.id)} className="text-red-600 hover:bg-red-50 p-1 rounded" title="حذف">
                          <Trash2 size={14} />
                        </button>
                      )}
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>

      {/* Structured editor modal */}
      {showForm && editing && (
        <div className="fixed inset-0 bg-black/40 flex items-center justify-center z-50 p-4 overflow-y-auto">
          <div className="bg-canvas dark:bg-neutral-900 rounded-card shadow-xl w-full max-w-4xl p-6 my-8">
            <div className="flex items-center justify-between mb-4">
              <h2 className="text-lg font-semibold flex items-center gap-2">
                <Zap size={18} className="text-amber-500" />
                {editing.id ? `تعديل: ${editing.name}` : "قاعدة جديدة"}
              </h2>
              <button onClick={() => { setShowForm(false); setEditing(null); setError(null); }} className="text-ink-subtle hover:text-ink-muted">
                <X size={20} />
              </button>
            </div>

            {/* Header fields */}
            <div className="space-y-3 mb-4">
              <div>
                <label className="block text-sm font-medium mb-1">اسم القاعدة *</label>
                <input className="input" value={editing.name} onChange={(e) => setEditing({ ...editing, name: e.target.value })} />
              </div>
              <div>
                <label className="block text-sm font-medium mb-1">الوصف</label>
                <input className="input" value={editing.description || ""} onChange={(e) => setEditing({ ...editing, description: e.target.value })} />
              </div>
              <div className="grid grid-cols-3 gap-3">
                <div>
                  <label className="block text-sm font-medium mb-1">نوع الحدث *</label>
                  <select className="input" value={editing.eventName} onChange={(e) => setEditing({ ...editing, eventName: e.target.value })}>
                    {EVENT_NAMES.map(n => <option key={n} value={n}>{n}</option>)}
                  </select>
                </div>
                <div>
                  <label className="block text-sm font-medium mb-1">الأولوية</label>
                  <input type="number" className="input" value={editing.priority} onChange={(e) => setEditing({ ...editing, priority: Number(e.target.value) })} />
                </div>
                <div>
                  <label className="block text-sm font-medium mb-1">الحالة</label>
                  <select className="input" value={editing.enabled ? "1" : "0"} onChange={(e) => setEditing({ ...editing, enabled: e.target.value === "1" })}>
                    <option value="1">مفعّلة</option>
                    <option value="0">معطلة</option>
                  </select>
                </div>
              </div>
            </div>

            {/* Actions editor */}
            <div className="border-t border-edge pt-4">
              <div className="flex items-center justify-between mb-2">
                <h3 className="font-semibold">الإجراءات (Actions)</h3>
                <button
                  type="button"
                  onClick={() => {
                    const def = getDef();
                    setDef({
                      ...def,
                      actions: [...def.actions, { type: "PostJournalEntry", narration: "", lines: [] }],
                    });
                  }}
                  className="text-sm text-primary-600 hover:underline flex items-center gap-1"
                >
                  <Plus size={14} /> إضافة إجراء
                </button>
              </div>

              {getDef().actions.map((action, actionIdx) => (
                <div key={actionIdx} className="border border-edge rounded-md p-3 mb-3 bg-raised">
                  <div className="flex items-center justify-between mb-2">
                    <div className="text-sm font-semibold">
                      إجراء #{actionIdx + 1}: <code className="bg-canvas dark:bg-neutral-900 px-1 rounded text-xs">{action.type}</code>
                    </div>
                    <button onClick={() => {
                      const def = getDef();
                      setDef({ ...def, actions: def.actions.filter((_, i) => i !== actionIdx) });
                    }} className="text-red-600 hover:bg-red-50 p-1 rounded" title="حذف الإجراء">
                      <Trash2 size={14} />
                    </button>
                  </div>

                  {/* Narration + projectFrom */}
                  <div className="grid grid-cols-1 md:grid-cols-2 gap-3 mb-3">
                    <div>
                      <label className="block text-xs font-medium mb-1">الوصف (Narration)</label>
                      <input
                        className="input text-sm"
                        value={action.narration || ""}
                        onChange={(e) => updateAction(actionIdx, (a) => ({ ...a, narration: e.target.value }))}
                        dir="rtl"
                      />
                    </div>
                    <div>
                      <label className="block text-xs font-medium mb-1">projectFrom (تحديد المشروع)</label>
                      <select
                        className="input text-sm"
                        value={action.projectFrom || ""}
                        onChange={(e) => updateAction(actionIdx, (a) => ({ ...a, projectFrom: e.target.value || undefined }))}
                      >
                        {PROJECT_FROM_DIRECTIVES.map(d => <option key={d.value} value={d.value}>{d.label}</option>)}
                      </select>
                    </div>
                  </div>

                  {/* Lines */}
                  <div className="flex items-center justify-between mb-2">
                    <h4 className="text-sm font-semibold">بنود القيد ({action.lines.length})</h4>
                    <button onClick={() => addLine(actionIdx)} className="text-xs text-primary-600 hover:underline flex items-center gap-1">
                      <Plus size={12} /> إضافة بند
                    </button>
                  </div>

                  {action.lines.length === 0 ? (
                    <p className="text-xs text-ink-muted py-2 text-center">لا توجد بنود. اضغط "إضافة بند" للبدء.</p>
                  ) : (
                    <div className="space-y-2">
                      {action.lines.map((line, lineIdx) => {
                        const selectedId = resolveAccountId(line);
                        const selectedDirective = line.accountFrom || "";
                        return (
                          <div key={lineIdx} className="grid grid-cols-12 gap-2 items-center bg-canvas dark:bg-neutral-900 p-2 rounded">
                            {/* Nature */}
                            <div className="col-span-1">
                              <select
                                className="input text-xs py-1"
                                value={line.nature}
                                onChange={(e) => updateLine(actionIdx, lineIdx, (l) => ({ ...l, nature: e.target.value as "debit" | "credit" }))}
                                title="مدين أو دائن"
                              >
                                <option value="debit">مدين</option>
                                <option value="credit">دائن</option>
                              </select>
                            </div>

                            {/* accountFrom directive */}
                            <div className="col-span-3">
                              <select
                                className="input text-xs py-1"
                                value={selectedDirective}
                                onChange={(e) => applyAccountChoice(actionIdx, lineIdx, selectedId, e.target.value)}
                                title="توجيه accountFrom الديناميكي"
                              >
                                {ACCOUNT_FROM_DIRECTIVES.map(d => <option key={d.value} value={d.value}>{d.label}</option>)}
                              </select>
                            </div>

                            {/* accountCode (static) */}
                            <div className="col-span-3">
                              <select
                                className="input text-xs py-1"
                                value={selectedId}
                                onChange={(e) => applyAccountChoice(actionIdx, lineIdx, e.target.value, "")}
                                disabled={!!selectedDirective}
                                title={selectedDirective ? "معطّل لأن accountFrom محدد" : "حساب ثابت من شجرة الحسابات"}
                              >
                                <option value="">— اختر حساب ثابت —</option>
                                {flatAccounts.map(a => (
                                  <option key={a.id} value={a.id}>{a.label}</option>
                                ))}
                              </select>
                            </div>

                            {/* Amount formula */}
                            <div className="col-span-2">
                              <input
                                className="input text-xs py-1 font-mono"
                                value={line.amountFormula}
                                onChange={(e) => updateLine(actionIdx, lineIdx, (l) => ({ ...l, amountFormula: e.target.value }))}
                                placeholder="invoice.total"
                                dir="ltr"
                                title="صيغة المبلغ (مثال: invoice.total - invoice.tax)"
                              />
                            </div>

                            {/* Description */}
                            <div className="col-span-2">
                              <input
                                className="input text-xs py-1"
                                value={line.description || ""}
                                onChange={(e) => updateLine(actionIdx, lineIdx, (l) => ({ ...l, description: e.target.value }))}
                                placeholder="الوصف"
                                dir="rtl"
                              />
                            </div>

                            {/* Remove */}
                            <div className="col-span-1 text-center">
                              <button onClick={() => removeLine(actionIdx, lineIdx)} className="text-red-600 hover:bg-red-50 p-1 rounded" title="حذف البند">
                                <Trash2 size={12} />
                              </button>
                            </div>
                          </div>
                        );
                      })}
                    </div>
                  )}
                </div>
              ))}
            </div>

            {/* JSON preview toggle */}
            <div className="mt-3">
              <button
                type="button"
                onClick={() => setShowJson(!showJson)}
                className="text-xs text-ink-muted hover:text-ink-strong flex items-center gap-1"
              >
                {showJson ? <ChevronUp size={12} /> : <ChevronDown size={12} />}
                {showJson ? "إخفاء JSON" : "عرض JSON"}
              </button>
              {showJson && (
                <textarea
                  className="input font-mono text-xs mt-2"
                  rows={12}
                  value={editing.ruleJson}
                  onChange={(e) => setEditing({ ...editing, ruleJson: e.target.value })}
                  dir="ltr"
                />
              )}
            </div>

            {savedOk && (
              <div className="mt-3 p-2 bg-green-50 border border-green-200 rounded text-sm text-green-700 flex items-center gap-2">
                <Check size={16} /> تم الحفظ بنجاح — جاري الإغلاق...
              </div>
            )}

            <div className="flex gap-2 pt-4 mt-3 border-t border-edge">
              <button onClick={save} disabled={saving} className="btn-primary flex-1">
                {saving ? <Loader2 className="animate-spin" size={16} /> : <Save size={16} />}
                حفظ القاعدة
              </button>
              <button onClick={() => { setShowForm(false); setEditing(null); setError(null); }} className="btn-secondary">إلغاء</button>
            </div>
          </div>
        </div>
      )}

      {/* Test modal (unchanged from Sprint 34) */}
      {testModal && (
        <div className="fixed inset-0 bg-black/40 flex items-center justify-center z-50 p-4">
          <div className="bg-canvas dark:bg-neutral-900 rounded-card shadow-xl w-full max-w-2xl p-6">
            <div className="flex items-center justify-between mb-4">
              <h2 className="text-lg font-semibold">اختبار: {testModal.name}</h2>
              <button onClick={() => setTestModal(null)} className="text-ink-subtle hover:text-ink-muted">
                <X size={20} />
              </button>
            </div>
            <div className="space-y-3">
              <div>
                <label className="block text-sm font-medium mb-1">Event Payload (JSON)</label>
                <textarea
                  className="input font-mono text-xs"
                  rows={10}
                  value={testPayload}
                  onChange={(e) => setTestPayload(e.target.value)}
                  dir="ltr"
                />
              </div>
              <button onClick={runTest} disabled={testing} className="btn-primary w-full">
                {testing ? <Loader2 className="animate-spin" size={16} /> : <Play size={16} />}
                تشغيل الاختبار
              </button>
              {testResult && (
                <div>
                  <label className="block text-sm font-medium mb-1">النتيجة</label>
                  <pre className="bg-raised p-3 rounded text-xs font-mono overflow-auto max-h-60" dir="ltr">
                    {testResult}
                  </pre>
                </div>
              )}
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
