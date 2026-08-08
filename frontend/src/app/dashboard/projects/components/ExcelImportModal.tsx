"use client";

/**
 * Sprint 38 — Bulk import line items from Excel or clipboard.
 *
 * Two import surfaces in one modal:
 *   1. File upload — drag-and-drop OR click to pick .xlsx/.xls
 *      Sends the file to POST /api/contracts/{id}/line-items/import-excel
 *      (multipart/form-data). The backend uses ClosedXML to parse
 *      the workbook and returns the inserted items.
 *
 *   2. Paste from clipboard — a textarea where the user pastes
 *      tab-separated rows (Excel's default copy format).
 *      Sends the raw text to
 *      POST /api/contracts/{id}/line-items/import-clipboard.
 *
 * Expected format (5 columns, tab-separated):
 *   line_number [TAB] description [TAB] unit [TAB] quantity [TAB] unit_price
 *   1 [TAB] حفر أساسات [TAB] m3 [TAB] 1000 [TAB] 5
 *
 * Why "paste" instead of "CSV"?
 *   The most common workflow is: open Excel → select rows → Ctrl+C
 *   → paste here. The result is tab-separated, not CSV. Matching
 *   the user's natural copy is friendlier than asking them to
 *   save-as-csv. (If we ever need CSV, it's a one-line change in
 *   the split() below.)
 *
 * Preview state:
 *   We preview rows *client-side* by splitting on newlines + tabs
 *   so the user can see what they're about to send. The server is
 *   the source of truth — if the server rejects rows, the error
 *   panel below shows them.
 */
import { useEffect, useRef, useState } from "react";
import {
  X,
  Upload,
  FileSpreadsheet,
  Clipboard,
  AlertCircle,
  CheckCircle2,
  Loader2,
  Download,
} from "lucide-react";
import { api, getErrorMessage } from "@/lib/api";
import { formatNumber, cn } from "@/lib/utils";

type Method = "upload" | "paste";

interface PreviewRow {
  lineNumber: string;
  description: string;
  unit: string;
  quantity: string;
  unitPrice: string;
  /** First parse error, if any. */
  error?: string;
}

interface Props {
  open: boolean;
  onClose: () => void;
  /** Called after a successful import with the inserted items. */
  onImported: (items: any[]) => void;
  contractId: string;
}

export default function ExcelImportModal({
  open,
  onClose,
  onImported,
  contractId,
}: Props) {
  const [method, setMethod] = useState<Method>("upload");
  const [file, setFile] = useState<File | null>(null);
  const [pasteText, setPasteText] = useState("");
  const [preview, setPreview] = useState<PreviewRow[]>([]);
  const [importing, setImporting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [success, setSuccess] = useState<string | null>(null);
  const [serverErrors, setServerErrors] = useState<string[]>([]);
  const [dragging, setDragging] = useState(false);
  const inputRef = useRef<HTMLInputElement | null>(null);

  // Reset on open
  useEffect(() => {
    if (!open) return;
    setMethod("upload");
    setFile(null);
    setPasteText("");
    setPreview([]);
    setError(null);
    setSuccess(null);
    setServerErrors([]);
  }, [open]);

  // Live preview for paste method: parse on every keystroke so
  // the user sees exactly what will be sent. (Cheap — pure split.)
  useEffect(() => {
    if (method !== "paste") {
      setPreview([]);
      return;
    }
    if (!pasteText.trim()) {
      setPreview([]);
      return;
    }
    const rows = parseClipboard(pasteText);
    setPreview(rows);
  }, [pasteText, method]);

  if (!open) return null;

  const handleFileSelect = (f: File | null) => {
    setError(null);
    setSuccess(null);
    setServerErrors([]);
    if (!f) {
      setFile(null);
      return;
    }
    const lower = f.name.toLowerCase();
    if (!lower.endsWith(".xlsx") && !lower.endsWith(".xls")) {
      setError("يجب أن يكون الملف من نوع Excel (.xlsx أو .xls)");
      return;
    }
    setFile(f);
  };

  const onFileInput = (e: React.ChangeEvent<HTMLInputElement>) => {
    handleFileSelect(e.target.files?.[0] || null);
  };

  const onDrop = (e: React.DragEvent) => {
    e.preventDefault();
    setDragging(false);
    const f = e.dataTransfer.files?.[0] || null;
    handleFileSelect(f);
  };

  const submit = async () => {
    setError(null);
    setSuccess(null);
    setServerErrors([]);

    if (method === "upload") {
      if (!file) {
        setError("اختر ملف Excel أولاً");
        return;
      }
      const form = new FormData();
      form.append("file", file);
      setImporting(true);
      try {
        const res = await api.post(
          `/contracts/${contractId}/line-items/import-excel`,
          form,
          { headers: { "Content-Type": "multipart/form-data" } }
        );
        const inserted = res.data?.items || res.data || [];
        setSuccess(`تم استيراد ${inserted.length} بند بنجاح`);
        onImported(inserted);
        setTimeout(() => onClose(), 700);
      } catch (err) {
        const msg = getErrorMessage(err);
        setError(msg);
        const list = (err as any)?.response?.data?.errors;
        if (Array.isArray(list)) setServerErrors(list.map(String));
      } finally {
        setImporting(false);
        return;
      }
    }

    // paste method
    if (!pasteText.trim()) {
      setError("الصق بيانات جدول Excel في الحقل أولاً");
      return;
    }
    if (preview.length === 0) {
      setError("لم يتم العثور على صفوف صالحة");
      return;
    }
    const rowErrors = preview.filter((r) => r.error).map((r) => r.error!);
    if (rowErrors.length > 0) {
      setError("يوجد أخطاء في بعض الصفوف. صحح البيانات أولاً.");
      return;
    }
    setImporting(true);
    try {
      const res = await api.post(
        `/contracts/${contractId}/line-items/import-clipboard`,
        { text: pasteText }
      );
      const inserted = res.data?.items || res.data || [];
      setSuccess(`تم استيراد ${inserted.length} بند بنجاح`);
      onImported(inserted);
      setTimeout(() => onClose(), 700);
    } catch (err) {
      const msg = getErrorMessage(err);
      setError(msg);
      const list = (err as any)?.response?.data?.errors;
      if (Array.isArray(list)) setServerErrors(list.map(String));
    } finally {
      setImporting(false);
    }
  };

  return (
    <div className="fixed inset-0 bg-black/40 flex items-center justify-center z-50 p-2 sm:p-4">
      <div className="bg-canvas dark:bg-neutral-900 rounded-card shadow-xl w-full max-w-3xl p-4 sm:p-6 max-h-[95vh] overflow-y-auto">
        <div className="flex items-center justify-between mb-4">
          <h2 className="text-lg font-semibold flex items-center gap-2">
            <FileSpreadsheet size={18} className="text-primary-600" />
            استيراد بنود من Excel
          </h2>
          <button
            onClick={onClose}
            className="text-ink-subtle hover:text-ink-muted"
            type="button"
            aria-label="إغلاق"
          >
            <X size={20} />
          </button>
        </div>

        {/* Method tabs */}
        <div className="flex border-b border-edge mb-4">
          <MethodTab
            active={method === "upload"}
            onClick={() => setMethod("upload")}
            icon={<Upload size={14} />}
            label="رفع ملف"
          />
          <MethodTab
            active={method === "paste"}
            onClick={() => setMethod("paste")}
            icon={<Clipboard size={14} />}
            label="لصق من الحافظة"
          />
        </div>

        {/* Format hint */}
        <div className="text-xs text-ink-muted bg-raised border border-edge rounded p-2 mb-3 font-mono" dir="ltr">
          line_number [TAB] description [TAB] unit [TAB] quantity [TAB] unit_price
          <br />
          1 [TAB] حفر أساسات [TAB] m3 [TAB] 1000 [TAB] 5
          <br />
          2 [TAB] خرسانة عادية [TAB] m3 [TAB] 500 [TAB] 50
        </div>

        {method === "upload" ? (
          <div
            onDragOver={(e) => {
              e.preventDefault();
              setDragging(true);
            }}
            onDragLeave={() => setDragging(false)}
            onDrop={onDrop}
            className={cn(
              "border-2 border-dashed rounded-md p-6 text-center cursor-pointer transition-colors",
              dragging
                ? "border-primary-500 bg-primary-50"
                : "border-edge bg-raised hover:border-ink-subtle"
            )}
            onClick={() => inputRef.current?.click()}
          >
            <input
              ref={inputRef}
              type="file"
              accept=".xlsx,.xls"
              className="hidden"
              onChange={onFileInput}
            />
            <Upload size={32} className="mx-auto text-ink-subtle mb-2" />
            {file ? (
              <div>
                <p className="text-sm font-medium">{file.name}</p>
                <p className="text-xs text-ink-muted mt-1">
                  {(file.size / 1024).toFixed(1)} KB
                </p>
                <button
                  type="button"
                  onClick={(e) => {
                    e.stopPropagation();
                    handleFileSelect(null);
                  }}
                  className="text-xs text-red-600 hover:underline mt-2"
                >
                  إزالة
                </button>
              </div>
            ) : (
              <div>
                <p className="text-sm text-ink-muted">
                  اسحب ملف Excel هنا أو انقر للاختيار
                </p>
                <p className="text-xs text-ink-muted mt-1">
                  (.xlsx, .xls — حد أقصى 5 ميغابايت)
                </p>
              </div>
            )}
          </div>
        ) : (
          <div>
            <label className="block text-sm font-medium mb-1">
              الصق البيانات من Excel
            </label>
            <textarea
              className="input font-mono text-xs"
              rows={8}
              value={pasteText}
              onChange={(e) => setPasteText(e.target.value)}
              dir="ltr"
              placeholder={"1\tحفر أساسات\tm3\t1000\t5\n2\tخرسانة عادية\tm3\t500\t50"}
            />
            <p className="text-xs text-ink-muted mt-1">
              الصق من Excel (Ctrl+V) — يفصل بين الأعمدة بـ Tab
            </p>
          </div>
        )}

        {/* Preview / errors */}
        {method === "paste" && preview.length > 0 && (
          <div className="mt-3">
            <h4 className="text-sm font-semibold mb-2">
              معاينة ({preview.length} صف)
            </h4>
            <div className="border border-edge rounded-md overflow-x-auto max-h-64 overflow-y-auto">
              <table className="w-full text-xs">
                <thead className="bg-raised sticky top-0">
                  <tr>
                    <th className="text-right py-1 px-2 font-semibold text-ink-muted">#</th>
                    <th className="text-right py-1 px-2 font-semibold text-ink-muted">الوصف</th>
                    <th className="text-right py-1 px-2 font-semibold text-ink-muted">الوحدة</th>
                    <th className="text-left py-1 px-2 font-semibold text-ink-muted">الكمية</th>
                    <th className="text-left py-1 px-2 font-semibold text-ink-muted">سعر الوحدة</th>
                    <th className="text-left py-1 px-2 font-semibold text-ink-muted">الإجمالي</th>
                  </tr>
                </thead>
                <tbody>
                  {preview.map((r, i) => {
                    const qty = Number(r.quantity);
                    const price = Number(r.unitPrice);
                    const total = isNaN(qty) || isNaN(price) ? 0 : qty * price;
                    return (
                      <tr
                        key={i}
                        className={cn(
                          "border-t border-edge",
                          r.error && "bg-red-50 dark:bg-red-900/20"
                        )}
                      >
                        <td className="py-1 px-2 font-mono">{r.lineNumber || "—"}</td>
                        <td className="py-1 px-2 truncate max-w-xs" title={r.description}>
                          {r.description || "—"}
                        </td>
                        <td className="py-1 px-2 text-ink-muted">{r.unit || "—"}</td>
                        <td className="py-1 px-2 text-left font-mono" dir="ltr">
                          {r.quantity || "—"}
                        </td>
                        <td className="py-1 px-2 text-left font-mono" dir="ltr">
                          {r.unitPrice || "—"}
                        </td>
                        <td className="py-1 px-2 text-left font-mono" dir="ltr">
                          {formatNumber(total)}
                        </td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>
            {preview.some((r) => r.error) && (
              <ul className="mt-2 text-xs text-red-700 space-y-1">
                {preview
                  .filter((r) => r.error)
                  .map((r, i) => (
                    <li key={i} className="flex items-start gap-1">
                      <AlertCircle size={12} className="mt-0.5 shrink-0" />
                      <span>صف {r.lineNumber || i + 1}: {r.error}</span>
                    </li>
                  ))}
              </ul>
            )}
          </div>
        )}

        {error && (
          <div className="mt-3 p-3 bg-red-50 text-red-700 rounded-md text-sm flex items-start gap-2">
            <AlertCircle size={16} className="mt-0.5 flex-shrink-0" />
            <span>{error}</span>
          </div>
        )}
        {success && (
          <div className="mt-3 p-3 bg-green-50 text-green-700 rounded-md text-sm flex items-center gap-2">
            <CheckCircle2 size={16} />
            <span>{success}</span>
          </div>
        )}
        {serverErrors.length > 0 && (
          <ul className="mt-2 text-xs text-red-700 space-y-1">
            {serverErrors.map((e, i) => (
              <li key={i} className="flex items-start gap-1">
                <AlertCircle size={12} className="mt-0.5 shrink-0" />
                <span>{e}</span>
              </li>
            ))}
          </ul>
        )}

        <div className="mt-4 flex gap-2">
          <button
            type="button"
            onClick={submit}
            disabled={
              importing ||
              (method === "upload" && !file) ||
              (method === "paste" && preview.length === 0)
            }
            className="btn-primary flex-1"
          >
            {importing ? (
              <>
                <Loader2 className="animate-spin" size={16} /> جاري الاستيراد...
              </>
            ) : (
              <>
                <Download size={16} /> استيراد البنود
              </>
            )}
          </button>
          <button type="button" onClick={onClose} className="btn-secondary">
            إلغاء
          </button>
        </div>
      </div>
    </div>
  );
}

function MethodTab({
  active,
  onClick,
  icon,
  label,
}: {
  active: boolean;
  onClick: () => void;
  icon: React.ReactNode;
  label: string;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      className={cn(
        "flex items-center gap-1 px-3 py-2 text-sm font-medium border-b-2 -mb-px",
        active
          ? "border-primary-600 text-primary-700"
          : "border-transparent text-ink-muted hover:text-ink-muted"
      )}
    >
      {icon}
      {label}
    </button>
  );
}

/**
 * Parse tab/newline-separated text into structured rows. Returns
 * a per-row error message when fields are missing or non-numeric.
 */
function parseClipboard(text: string): PreviewRow[] {
  const lines = text.split(/\r?\n/).map((l) => l.replace(/\r$/, ""));
  const out: PreviewRow[] = [];
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    if (!line.trim()) continue;
    const cols = line.split("\t");
    // Be tolerant of comma fallback (some apps copy as CSV)
    const cells =
      cols.length >= 5
        ? cols
        : line.split(",").map((c) => c.trim());
    const [lineNumber, description, unit, quantity, unitPrice] =
      cells.map((c) => c?.trim() ?? "");
    let error: string | undefined;
    if (!description) error = "الوصف فارغ";
    else if (!unit) error = "الوحدة فارغة";
    else if (!quantity || isNaN(Number(quantity)) || Number(quantity) <= 0)
      error = "الكمية غير صحيحة";
    else if (unitPrice === "" || isNaN(Number(unitPrice)) || Number(unitPrice) < 0)
      error = "سعر الوحدة غير صحيح";
    out.push({ lineNumber, description, unit, quantity, unitPrice, error });
  }
  return out;
}
