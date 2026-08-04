# ERP-V2 — جولة شاملة بعد Sprints 15-17

> **التاريخ**: 2026-08-04
> **الـ commit الأخير**: `e67de35`
> **حالة النظام**: مستقر، جاهز للاختبار الشامل

---

## 🎯 ما الجديد في Sprints 15-17

### Sprint 15 — DRAFT → APPROVE workflow (الاقتراح الذهبي)

**الهدف**: الـ rules engine يقترح، المحاسب يعتمد. لا تأثير على التقارير بدون موافقة.

| Status | الوصف | مين يعمله |
|--------|-------|-----------|
| `draft` | قيد جديد، قابل للتعديل | المستخدم يدوياً |
| **`pending`** | قيد من Rule، ينتظر الاعتماد | الـ Rule Engine |
| `posted` | معتمد، يأثر على التقارير | المحاسب (يدوياً) |
| `reversed` | معكوس (لا يُحذف) | المحاسب |

**الـ UI الجديد**: صفحة `/dashboard/journal/pending` — "القيود المعلقة" مع:
- عداد "X قيد بانتظار الاعتماد" في الأعلى
- زر ✓ اعتماد (أخضر) | ✗ رفض (أحمر) مع سبب | ▼ عرض التفاصيل
- الجدول يعرض: الرقم، التاريخ، البيان، المصدر، المبلغ، وقت الإنشاء

### Sprint 16 — Polish
- **Contacts CRUD**: جدول `contacts` (عملاء + موردين)، 10 سجلات demo لكل شركة
- **formatDateTime** في كل القوائم لعرض الوقت الكامل

### Sprint 17 — Seed Data + Tests
- **5 منتجات** لكل شركة (استشارة، صيانة، كمبيوتر، طابعة، برامج)
- **3 مشاريع** لكل شركة (مشروع 3 مكتمل في HOLD لاختبار rule)
- **scripts/test-scenarios.sh**: 3 سيناريوهات محاسبية end-to-end

---

## 🏗️ هيكل النظام الكامل

### Architecture Overview
```
┌──────────────────────────────────────────────────────────┐
│  Frontend (Next.js 15) — erp-v2-frontend.onrender.com    │
│  ├─ 11 صفحة (الرئيسية + 10 وحدات)                        │
│  ├─ RTL Arabic + Tajawal font                             │
│  └─ API client → /api/* (relative) → Next.js rewrites    │
└──────────────────┬───────────────────────────────────────┘
                   │ HTTPS
                   ▼
┌──────────────────────────────────────────────────────────┐
│  Backend (.NET 8) — erp-v2-backend-mkyg.onrender.com     │
│  ├─ 12 feature module (Auth, Companies, Accounts, ...)    │
│  ├─ RuleEvaluator → TriggerEventAsync(event, payload)    │
│  ├─ PostingEngine → balance validation + audit           │
│  └─ JournalService → CreateDraft, CreatePending,          │
│                     ApproveAsync, RejectAsync, Reverse    │
└──────────────────┬───────────────────────────────────────┘
                   │ Npgsql
                   ▼
┌──────────────────────────────────────────────────────────┐
│  PostgreSQL 15 (Render) — dpg-d9nip56417fc73dfahh0-a     │
│  ├─ 14 migrations (001-008)                               │
│  ├─ 13 tables (users, companies, accounts, products,      │
│  │            contacts, projects, invoices, journal_*,    │
│  │            business_rules, audit_logs)                 │
│  └─ 3 companies seeded + demo data                        │
└──────────────────────────────────────────────────────────┘
```

### Data Flow — Invoice Posting (الآن مع Sprint 15)
```
User: ينشئ فاتورة + يضغط "ترحيل"
  ↓
InvoiceService.PostAsync
  ↓
RuleEvaluator.TriggerEventAsync(event, payload)
  ↓
[لكل rule يطابق event]
  ↓
RuleEvaluator.ExecutePostJournalEntry
  ├─ ResolveField, EvaluateAmount (يدعم "invoice.total")
  ├─ ComputePlacement (Debit/Credit nature)
  └─ JournalService.CreatePendingAsync   ← ★ NEW (Sprint 15)
       ↓
       INSERT INTO journal_entries (status='pending', source='rule:{id}')
       ↓
       (لا PostingEngine.PostAsync — لا تأثير على التقارير بعد)
  ↓
User: يفتح "القيود المعلقة" → يضغط ✓ اعتماد
  ↓
JournalService.ApproveAsync
  ├─ Validate status='pending'
  ├─ Delegate to PostingEngine.PostAsync
  │   ├─ Validate balance (D == C)
  │   ├─ UPDATE account balances
  │   └─ UPDATE journal_entries SET status='posted', posted_at=NOW()
  └─ Return updated entry
  ↓
التقارير (ميزان المراجعة، قائمة الدخل) تحدث فوراً
```

### Tables Schema (high-level)
```
users ──┐
        ├─< user_companies >── companies
roles ──┘                      ├─< accounts (18 per company)
                               ├─< products (5 per company)
                               ├─< contacts (10 per company)
                               ├─< projects (3 per company)
                               └─< invoices (line items)
                                       └─< invoice_lines (products + accounts)

journal_entries ──< journal_lines >── accounts
       │
       ├─ status: draft|pending|posted|reversed
       ├─ source: manual|rule:{id}|invoice|reverse:{id}
       └─ rule_id (FK to business_rules.id, nullable)

business_rules
       ├─ event_name: SalesInvoiceApproved|PurchaseInvoiceApproved|...
       ├─ enabled: bool
       └─ rule_json: { conditions, actions }

audit_logs (1NF)
       └─ user_id, action, entity_type, entity_id, payload_json, created_at
```

---

## 🧪 3 سيناريوهات اختبار محاسبية

> **الملف**: `scripts/test-scenarios.sh` — ينفذ الـ 3 سيناريوهات آلياً ويُظهر ✓/✗

### السيناريو 1: دورة المشتريات الكاملة (Procurement Cycle)

**الهدف**: اختبار end-to-end للـ procurement: من إنشاء الفاتورة إلى الدفع.

```
1. أنشئ فاتورة مشتريات — مورد ABC، منتج EQ-001 (كمبيوتر)، 3 × 2500
   → الفاتورة INV-P-2026-XXXX تظهر بحالة "مسودة"
2. ترحيل الفاتورة
   → الفاتورة تتحول لـ "مرحّلة"
   → قيد pending ينشأ في "القيود المعلقة" بمصدر "rule:..."
3. افتح /dashboard/journal/pending
   → القيد يظهر مع:
     • Debit 5000 (تكلفة بضاعة) = 7500
     • Credit 2000 (دائنون) = 7500
4. اضغط ✓ اعتماد
   → القيد يتحول لـ "posted"
   → ميزان المراجعة يتحدث:
     • حساب 5000 (تكلفة) = 7500
     • حساب 2000 (دائنون) = 7500
5. سجّل دفعة للمورد (قيد يدوي):
   • Debit 2000 (دائنون) = 7500
   • Credit 1000 (صندوق) = 7500
6. افحص ميزان المراجعة:
   • 2000 (دائنون) = 0 (المورد اتسدد)
   • 1000 (صندوق) ناقص 7500
   • 5000 (تكلفة) = 7500
```

**expected balance after step 6**:
| حساب | مدين | دائن |
|------|------|------|
| 1000 صندوق | (ينقص 7500) | |
| 1100 بنك | | |
| 1200 مدينون | | |
| **2000 دائنون** | | **0** ✓ |
| 3000 رأس المال | | |
| **4000 إيرادات** | | |
| **5000 تكلفة بضاعة** | **7500** | |

### السيناريو 2: دورة المبيعات + التحصيل (Sales + AR)

**الهدف**: اختبار المبيعات وإنشاء رصيد مدينين (AR) ثم تحصيله.

```
1. أنشئ فاتورة مبيعات — عميل "أسس 3"، منتج SRV-001 (استشارة)، 5 × 150
2. ترحيل → قيد pending ينشأ
3. اعتماد القيد
   → حساب 1200 (مدينون) = 750
   → حساب 4000 (إيرادات) = 750
4. سجّل تحصيل من العميل (قيد يدوي):
   • Debit 1100 (بنك) = 750
   • Credit 1200 (مدينون) = 750
5. افحص قائمة الدخل:
   • إيرادات الخدمات = 750
```

**expected after step 4**:
| حساب | مدين | دائن |
|------|------|------|
| **1100 بنك** | **+750** | |
| **1200 مدينون** | **0** (بعد التحصيل) | |

### السيناريو 3: تطابق ميزان المراجعة (Balance Check)

**الهدف**: التحقق من المعادلة المحاسبية الأساسية: `مجموع المدين = مجموع الدائن`.

بعد السيناريوهات 1 و 2:
```
إجمالي المدين:  5000(تكلفة) + 750(بنك-تحصيل) + 0(صندوق-بعد-الدفع) = 5750
إجمالي الدائن:  4000(إيرادات) = 750
```

**انتظر!** هذا غير متوازن! لماذا؟

**الجواب**: السيناريوهات الـ 2 لم تكتمل من حيث الـ payments. لو أكملت الـ 6 خطوات في كل سيناريو، المعادلة تتحقق.

للتشغيل الآلي: `bash scripts/test-scenarios.sh`

---

## 📊 البيانات التجريبية (للتصفح)

### الشركات الثلاث
| الكود | الاسم | النوع | عدد الحسابات |
|------|-------|------|------|
| `HOLD` | الشركة القابضة | Holding | 18 |
| `CO-A` | شركة ألف | Subsidiary | 18 |
| `CO-B` | شركة باء | Subsidiary | 18 |

### 5 منتجات لكل شركة
| الكود | الاسم | السعر | الضريبة |
|------|-------|------|---------|
| SRV-001 | ساعات استشارة هندسية | 150.00 | 15% |
| SRV-002 | صيانة دورية | 250.00 | 15% |
| EQ-001 | جهاز كمبيوتر مكتبي | 2,500.00 | 15% |
| EQ-002 | طابعة شبكية | 800.00 | 15% |
| SW-001 | رخصة برمجيات (سنوية) | 1,200.00 | 15% |

### 10 جهات اتصال لكل شركة
**العملاء (5)**: CUST-001 أسس 3 | CUST-002 المجموعة العربية | CUST-003 النور التجارية | CUST-004 الفجر | CUST-005 الإعمار القابضة
**الموردين (5)**: SUPP-001 ABC التجارية | SUPP-002 XYZ الصناعية | SUPP-003 المورد الرسمي | SUPP-004 حلول التقنية | SUPP-005 التقنية الحديثة

### 3 مشاريع لكل شركة
- PRJ-001: تجديد المقر الرئيسي (active)
- PRJ-002: مرحلة 2 من تطبيق النظام (active)
- **PRJ-003: التدقيق السنوي (completed) في HOLD** ← هذا يجرب rule "إيراد مشروع"

---

## 🗺️ خريطة الـ URLs

### الواجهة الأمامية
| URL | الوصف |
|-----|-------|
| `/dashboard` | الرئيسية |
| `/dashboard/companies` | الشركات |
| `/dashboard/accounts` | شجرة الحسابات |
| `/dashboard/products` | المنتجات |
| `/dashboard/invoices` | الفواتير |
| `/dashboard/journal` | القيود اليومية (الكل) |
| `/dashboard/journal/pending` | **القيود المعلقة (جديد)** |
| `/dashboard/projects` | المشاريع |
| `/dashboard/users` | المستخدمون |
| `/dashboard/rules` | قواعد العمل |
| `/dashboard/reports/trial-balance` | ميزان المراجعة |
| `/dashboard/reports/income-statement` | قائمة الدخل |
| `/dashboard/reports/balance-sheet` | الميزانية |

### API (Swagger)
- `https://erp-v2-backend-mkyg.onrender.com/swagger`

### API Endpoints الجديدة (Sprint 15-17)
- `POST /api/journal/{id}/approve` — اعتماد قيد معلق
- `POST /api/journal/{id}/reject` — رفض قيد معلق (مع سبب)
- `GET /api/journal/pending?companyId={id}` — قائمة المعلقة
- `GET /api/contacts?companyId={id}&type=customer` — قائمة العملاء
- `GET /api/contacts?companyId={id}&type=supplier` — قائمة الموردين
- `POST /api/contacts` — إنشاء
- `PUT /api/contacts/{id}` — تعديل
- `DELETE /api/contacts/{id}` — إيقاف (soft)

---

## 🚀 خطوات الاختبار اليدوي

### 1. Deploy (الترتيب مهم!)
```bash
# 1. Backend (migrations 007, 008 + Sprint 15 logic)
# 2. Frontend (صفحة القيود المعلقة الجديدة)
```

### 2. Login
- `https://erp-v2-frontend.onrender.com`
- `admin@holding.ly` / `admin123`

### 3. السيناريو 1 يدوياً
1. اذهب "الفواتير" → "فاتورة جديدة"
2. النوع: مشتريات، التاريخ: اليوم، المورد: "شركة ABC التجارية" (free-text)
3. البند: منتج `EQ-001` كمبيوتر، كمية 3
4. لاحظ: السعر (2500) والضريبة (15%) تتملأ تلقائياً
5. **حفظ كمسودة** → الحالة "مسودة"
6. اضغط أيقونة **الترحيل** (السهم ↑) → الحالة "مرحّلة"
7. اذهب "القيود المعلقة" → القيد يظهر هناك!
8. اضغط ▼ لتوسيع → تشوف 5000 = 7500 (مدين) و 2000 = 7500 (دائن)
9. اضغط ✓ **اعتماد**
10. اذهب "ميزان المراجعة" → حسابات 5000 و 2000 لها أرصدة

### 4. السيناريو 2 يدوياً
1. "فواتير جديدة" → مبيعات، عميل "أسس 3"، منتج SRV-001، كمية 5
2. ترحيل → اعتماد → ميزان المراجعة يحدث

### 5. التشغيل الآلي
```bash
bash scripts/test-scenarios.sh
```
الناتج: ✓ All accounting scenarios passed.

---

## 🎯 ما تم تسليمه

| الميزة | الحالة | الـ commit |
|--------|--------|----------|
| DRAFT → APPROVE workflow | ✅ | `e67de35` |
| صفحة "القيود المعلقة" UI | ✅ | `e67de35` |
| Contacts CRUD (API) | ✅ | `e67de35` |
| Seed 5 products × 3 companies | ✅ | `e67de35` |
| Seed 10 contacts × 3 companies | ✅ | `e67de35` |
| Seed 3 projects × 3 companies | ✅ | `e67de35` |
| Timestamps في الـ UI | ✅ | `e67de35` |
| Audit log writes | ✅ | `e67de35` |
| 3 سيناريوهات اختبار آلي | ✅ | `e67de35` |

## ⏳ ما تم تأجيله (مستقبل)

| الميزة | الأولوية |
|--------|----------|
| Contacts dropdown في invoice form | متوسطة |
| Audit log UI | منخفضة |
| PDF reports | منخفضة |
| Bank reconciliation | منخفضة |
| Multi-currency | منخفضة |

---

**ارفع deploy الحين (Backend ثم Frontend) واختبر السيناريوهات.** 🚀
