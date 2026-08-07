"use client";

import { useEffect, useState } from "react";
import { api, getErrorMessage } from "@/lib/api";
import { useAuth } from "@/lib/auth-context";
import { Zap, Plus, Loader2, X, Play, FileCode, Edit2, Trash2 } from "lucide-react";
import { formatDateTime } from "@/lib/utils";

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

export default function RulesPage() {
  const { activeCompany, user } = useAuth();
  const [rules, setRules] = useState<Rule[]>([]);
  const [loading, setLoading] = useState(true);
  const [editing, setEditing] = useState<Rule | null>(null);
  const [showForm, setShowForm] = useState(false);
  const [testModal, setTestModal] = useState<Rule | null>(null);
  const [testPayload, setTestPayload] = useState("{\n  \"invoice\": { \"number\": \"INV-001\", \"total\": 1000, \"tax\": 0 },\n  \"supplier\": { \"name\": \"Test\" }\n}");
  const [testResult, setTestResult] = useState<string | null>(null);
  const [testing, setTesting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const load = async () => {
    try {
      setLoading(true);
      const res = await api.get("/rules");
      setRules(res.data);
    } catch (err) {
      setError(getErrorMessage(err));
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => { load(); }, []);

  const openCreate = () => {
    setEditing({
      id: "",
      name: "",
      description: "",
      eventName: "InvoiceApproved",
      enabled: true,
      priority: 100,
      ruleJson: JSON.stringify({
        conditions: { all: [] },
        actions: [
          {
            type: "PostJournalEntry",
            narration: "قيد جديد",
            lines: [
              // Sprint 34: can use either accountCode (static) or
              // accountFrom (dynamic). Examples below.
              { accountFrom: "voucher.bankAccount", nature: "debit",  amountFormula: "payment.amount", description: "الصندوق/البنك" },
              { accountFrom: "contact.subLedger",   nature: "credit", amountFormula: "payment.amount", description: "تسوية العميل/المورّد" }
            ]
          }
        ]
      }, null, 2),
      isTemplate: false,
      createdAt: "",
      updatedAt: ""
    });
    setShowForm(true);
  };

  const openEdit = (rule: Rule) => {
    setEditing({ ...rule });
    setShowForm(true);
  };

  const save = async () => {
    if (!editing) return;
    try {
      const body = {
        name: editing.name,
        description: editing.description,
        eventName: editing.eventName,
        enabled: editing.enabled,
        priority: editing.priority,
        ruleJson: editing.ruleJson
      };
      if (editing.id) {
        await api.put(`/rules/${editing.id}`, body);
      } else {
        await api.post("/rules", body);
      }
      setShowForm(false);
      setEditing(null);
      await load();
    } catch (err) {
      setError(getErrorMessage(err));
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

  const remove = async (id: string) => {
    if (!confirm("هل أنت متأكد من حذف هذه القاعدة؟")) return;
    try {
      await api.delete(`/rules/${id}`);
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
        {user?.isSuperAdmin && (
          <button onClick={openCreate} className="btn-primary">
            <Plus size={18} />
            قاعدة جديدة
          </button>
        )}
      </div>

      {error && <div className="mb-4 p-3 bg-red-50 text-red-700 rounded-md text-sm">{error}</div>}

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

      {showForm && editing && (
        <div className="fixed inset-0 bg-black/40 flex items-center justify-center z-50 p-4 overflow-y-auto">
          <div className="bg-canvas dark:bg-neutral-900 rounded-card shadow-xl w-full max-w-3xl p-6 my-8">
            <div className="flex items-center justify-between mb-4">
              <h2 className="text-lg font-semibold">{editing.id ? "تعديل قاعدة" : "قاعدة جديدة"}</h2>
              <button onClick={() => { setShowForm(false); setEditing(null); }} className="text-ink-subtle hover:text-ink-muted">
                <X size={20} />
              </button>
            </div>
            <div className="space-y-3">
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
                    <option>PurchaseInvoiceApproved</option>
                    <option>SalesInvoiceApproved</option>
                    <option>SupplierPaymentMade</option>
                    <option>CustomerReceiptReceived</option>
                    <option>PeriodClose</option>
                    <option>ProjectMilestoneCompleted</option>
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
              <div>
                <label className="block text-sm font-medium mb-1">تعريف القاعدة (JSON) *</label>
                <textarea
                  className="input font-mono text-xs"
                  rows={16}
                  value={editing.ruleJson}
                  onChange={(e) => setEditing({ ...editing, ruleJson: e.target.value })}
                  dir="ltr"
                />
                {/* Sprint 34 — quick reference for accountFrom directives */}
                <details className="mt-2 text-xs">
                  <summary className="cursor-pointer text-primary-600 hover:text-primary-800">
                    💡 مرجع سريع للتوجيهات الديناميكية (accountFrom)
                  </summary>
                  <div className="mt-2 p-2 bg-primary-50 border border-primary-200 rounded text-ink-strong" dir="rtl">
                    <p className="font-semibold mb-1">قائمة accountFrom المدعومة:</p>
                    <ul className="space-y-1 mr-4">
                      <li><code className="bg-canvas dark:bg-neutral-900 px-1 rounded">"voucher.bankAccount"</code> — حساب الصندوق/البنك من السند (bankAccountId)</li>
                      <li><code className="bg-canvas dark:bg-neutral-900 px-1 rounded">"contact.subLedger"</code> — حساب العميل/المورّد التفصيلي (sub-ledger)</li>
                      <li><code className="bg-canvas dark:bg-neutral-900 px-1 rounded">"control.ar"</code> — حساب المدينون الرئيسي (1103)</li>
                      <li><code className="bg-canvas dark:bg-neutral-900 px-1 rounded">"control.ap"</code> — حساب الدائنون الرئيسي (2101)</li>
                      <li><code className="bg-canvas dark:bg-neutral-900 px-1 rounded">"control.cash"</code> — حساب الصندوق الافتراضي (1101-CASH-001)</li>
                    </ul>
                    <p className="mt-2 text-ink-muted">
                      أو استخدم <code className="bg-canvas dark:bg-neutral-900 px-1 rounded">"accountCode": "1103"</code> لرمز ثابت.
                    </p>
                  </div>
                </details>
              </div>
              <div className="flex gap-2 pt-2">
                <button onClick={save} className="btn-primary flex-1">حفظ</button>
                <button onClick={() => { setShowForm(false); setEditing(null); }} className="btn-secondary">إلغاء</button>
              </div>
            </div>
          </div>
        </div>
      )}

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
