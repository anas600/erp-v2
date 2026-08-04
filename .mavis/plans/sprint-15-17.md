# Sprints 15-17 — خطة شاملة

> **التاريخ**: 2026-08-04
> **الهدف من السلسلة**: تحويل ERP-V2 من "demo محاسبي" إلى "نظام محاسبي إنتاجي" يدعم مراجعة واعتماد القيود قبل التأثير على التقارير.

---

## 🧭 نظرة عامة

| Sprint | الميزة الأساسية | الحجم | الحالة |
|--------|----------------|-------|--------|
| 15 | **DRAFT → APPROVE workflow** (المحاسب يعتمد قبل التأثير) | كبير | 🔵 TODO |
| 16 | **Polish** — Timestamps + Customers/Suppliers CRUD + Audit basics | متوسط | 🔵 TODO |
| 17 | **Seed data + Accounting test scenarios** للـ demo والاختبار | متوسط | 🔵 TODO |

**الاعتماد التسلسلي**: Sprint 15 ضروري قبل 16 (لأن 16 يبني على الـ statuses الجديدة).

---

## 🔵 Sprint 15 — DRAFT → APPROVE workflow (الاقتراح الذهبي)

### المشكلة الحالية

```
Invoice posted → Rule fires → JournalEntry auto-created → auto-POSTED
                                                          ↓
                                          Reports update فوراً (بدون مراجعة)
```

**العيب**: المحاسب ما يشوف القيد إلا إذا بحث عنه. لو الـ rule غلط، التأثير على القوائم المالية فوري.

### الحل: 4 حالات للقيد (instead of 3)

| Status | المعنى | مين يعمله |
|--------|--------|-----------|
| `draft` | قيد جديد، لم يُراجع | المستخدم أو الـ Rule |
| **`pending`** (جديد) | قيد من Rule، ينتظر اعتماد المحاسب | الـ Rule فقط |
| `posted` | معتمد، يأثر على التقارير | المحاسب (يدوياً) |
| `reversed` | مُعكس (لا يُحذف) | المحاسب |

### التغيرات المطلوبة

#### Backend
1. **Migration 007_DraftApproveWorkflow.cs**:
   - لا تغيير على الـ schema (status column يقبل أي string بطول 20)
   - لا يلزم index (الـ status queries قليلة)

2. **RuleEvaluator.cs**: عند `ExecutePostJournalEntry`:
   - لا تستدعي `PostingEngine.PostAsync` (اللي يحول draft → posted)
   - فقط استدعي `JournalService.CreateDraftAsync` بحالة ابتدائية
   - الـ entry يبدأ بحالة `pending` (مش `draft`) — يميّزها عن اليدوية

3. **JournalService.cs**: 
   - إضافة method `ApproveAsync(Guid id, Guid userId)` — يحول pending → posted
   - إضافة method `RejectAsync(Guid id, Guid userId, string reason)` — يحذف أو يحوّل لـ draft (للتعديل)

4. **PostingEngine.cs**: 
   - تحديث validation ليتعامل مع الـ 4 حالات
   - إضافة helper `TransitionStatusAsync(id, from, to, userId, reason?)` للـ audit

5. **JournalEndpoints.cs**:
   - `POST /api/journal/{id}/approve` — اعتماد
   - `POST /api/journal/{id}/reject` — رفض (يحذف أو يحوّل لـ draft)

6. **InvoiceService.cs**: 
   - حالياً يستدعي `_rules.TriggerEventAsync` اللي كان يعمل auto-post
   - مع التغيير، الـ rule يخلق entry بحالة `pending` — هذا ما نحتاجه بالضبط
   - **تغيير صفر مطلوب** — السلوك الجديد يأتي من تغيير الـ RuleEvaluator فقط ✨

#### Frontend
1. **صفحة جديدة**: `/dashboard/journal/pending` — "القيود المعلقة"
   - جدول بكل القيود ذات الحالة `pending`
   - لكل صف: "اعتماد" (أخضر) | "رفض" (أحمر) | "عرض" (neutral)
   - عداد في الـ sidebar: "القيود المعلقة (5)"

2. **صفحة `/dashboard/journal` محدّثة**:
   - تبويب جديد "معلقة" بجانب "الكل"
   - الـ entries الجديدة من الـ rules تظهر بحالة `pending` (badge أصفر مختلف عن `draft`)

3. **صفحة `/dashboard/rules` محدّثة**:
   - بطاقة جديدة أعلى: "القيود المعلقة من هذه القاعدة" (مع رابط للصفحة الجديدة)
   - إذا القاعدة تنتج قيود معلقة، المحاسب يعرف من أين جاءت

4. **API client** (`frontend/src/lib/api.ts`): إضافة `approveJournal`, `rejectJournal`

### Acceptance bar

1. لو مستخدم أنشأ فاتورة مبيعات، ضغط ترحيل:
   - الفاتورة تتحول لـ `posted` ✓
   - **قيد جديد ينشأ بحالة `pending`** (مش `posted` مباشرة)
   - يظهر في "القيود المعلقة"
   - ميزان المراجعة **لا يتأثر** (لأن القيد ليس posted)
2. لما المحاسب يفتح "القيود المعلقة" ويضغط "اعتماد":
   - القيد يتحول لـ `posted`
   - ميزان المراجعة يتحدث
3. لو الـ rule فشل (حساب غير موجود، إلخ):
   - الـ exception يظهر في server logs (موجود من Sprint 13)
   - الـ invoice لا تُرفض (لأن الـ invoice posting منفصل عن الـ rule)
   - الـ rule result يبقى فاضي بدون كسر الـ flow

---

## 🟢 Sprint 16 — Polish (Timestamps + Contacts + Audit Basics)

### 16.1 Timestamps في الـ UI
- **Backend**: لا تغيير (created_at/updated_at موجودان أصلاً)
- **Frontend**: تحديث `formatDate` → `formatDateTime` يعرض `2026-08-04 14:32:15`
- الجداول الـ 3 الأهم: journal, invoices, products
- إضافة tooltip "قبل X دقائق" (relative time)

### 16.2 Customers/Suppliers CRUD
- **Backend**: migration 008_Contacts.cs
  - جدول `contacts`: id, company_id, type ('customer' | 'supplier'), code, name, name_ar, tax_id, phone, email, is_active
  - Foreign keys: company_id → companies (CASCADE)
  - Composite unique (company_id, code, type)
- **Backend**: `Features/Contacts/`
  - `ContactService.cs` — CRUD
  - `ContactEndpoints.cs` — REST routes
  - `ContactModels.cs` — DTOs
- **Frontend**:
  - صفحة `/dashboard/contacts` — جدول بتبويبات (عملاء / موردين / الكل)
  - في `/dashboard/invoices`، الـ dropdown "الطرف" → "اختر عميل/مورد من القائمة" (مع زر "+ جديد" inline)

### 16.3 Audit Basics
- **Backend**: 
  - الـ `audit_logs` table موجود (001_InitialSchema)، لا يلزم migration
  - إنشاء `AuditService.cs` بسيط يكتب entry: `INSERT INTO audit_logs (user_id, action, entity_type, entity_id, payload_json, created_at)`
  - استدعاء من `PostingEngine.ApproveAsync` و `RejectAsync` فقط (الـ audit الأكثر أهمية)
- **Frontend**:
  - لا UI في هذا Sprint (نكتفي بحفظ الـ logs للاستعلام اليدوي)

---

## 🟡 Sprint 17 — Seed Data + Accounting Test Scenarios

### 17.1 Seed Data (`Migration 009_DemoData.cs`)

عشان الـ user يقدر يختبر النظام بدون ما يدخل بيانات يدوياً:

| لكل شركة | البيانات |
|----------|---------|
| **5 عملاء** | أسس 3, المجموعة العربية, النور, الفجر, الإعمار |
| **5 موردين** | ABC التجارية, XYZ الصناعية, المورد الرسمي, التقنية, التقنية الحديثة |
| **5 منتجات** | استشارة (150 د.ل), صيانة (250), كمبيوتر (2500), طابعة (800), برامج (1200) |
| **3 مشاريع** | مشروع 1, 2, 3 (1 منها بمرحلة مكتملة لاختبار الـ Milestone rule) |

**Toggle**: migration يضيف عمود `is_demo_data` (boolean) على كل جدول seeded عشان المستخدم يقدر يحذفها بـ `DELETE WHERE is_demo_data = true` بدون تأثير على بياناته الحقيقية.

### 17.2 Accounting Test Scenarios (3 سيناريوهات)

كل سيناريو يكون "محاكاة end-to-end" تختبر منطق محاسبي محدد:

#### السيناريو 1: "دورة المشتريات الكاملة"
```
الهدف: اختبار full procurement cycle
الخطوات:
  1. أنشئ PO (فاتورة مشتريات) — شركة ABC، منتج "كمبيوتر"، 3 × 2500
  2. ترحيل الفاتورة → ينشأ قيد pending
  3. (Sprint 15) اعتمد القيد → يُسجّل في دفتر اليومية
  4. افحص: حساب 2000 (دائنون) = 7500، حساب 1300 (مخزون) = 7500
  5. اعمل قيد دفع: Debit 2000, Credit 1000 — ينشأ تلقائياً عبر "دفع مورد" rule
  6. افحص: حساب 1000 (صندوق) ينقص، حساب 2000 يعود لـ 0
```

#### السيناريو 2: "دورة المبيعات + التحصيل"
```
الهدف: اختبار sales + receivables + customer payment
الخطوات:
  1. أنشئ فاتورة مبيعات — عميل "أسس 3"، منتج "استشارة"، 5 × 150
  2. ترحيل → ينشأ قيد pending
  3. اعتماد القيد → حساب 1200 (مدينون) = 750
  4. استلام دفعة: Debit 1100 (بنك), Credit 1200 — عبر "تحصيل من عميل" rule
  5. افحص: حساب 1200 يعود لـ 0، حساب 1100 يزيد
  6. افحص تقرير "قائمة الدخل": إيرادات الخدمات = 750
```

#### السيناريو 3: "تطابق ميزان المراجعة"
```
الهدف: التأكد من المعادلة المحاسبية الأساسية
الخطوات:
  1. بعد السيناريوهات 1 و 2، افتح ميزان المراجعة
  2. تحقق: مجموع المدين = مجموع الدائن (متوازن)
  3. تحقق: حساب 3000 (رأس المال) = الافتتاحي
  4. تحقق: حسابات الإيرادات (4xxx) = صافي المبيعات - صافي المشتريات (المنطقي)
  5. (اختياري) اطبع ميزان المراجعة (PDF) — لو الـ PDF feature أضيف
```

### 17.3 الـ Document الذي تريده

وثيقة واحدة:
- **`docs/sprint-15-17-summary.md`** — ملخص شامل:
  - هيكل النظام (data model + business flow)
  - ما تم في كل sprint
  - كل السيناريوهات المحاسبية مع expected output
  - روابط للـ verification commands

---

## 📦 خطة التنفيذ (للجلسة الحالية)

| الخطوة | العمل | الوقت |
|--------|-------|-------|
| 1 | تنفيذ Sprint 15 كامل (الـ DRAFT-APPROVE) | 60% |
| 2 | إضافة timestamps بسيطة (Sprint 16.1) | 10% |
| 3 | Seed data (Sprint 17.1) — migrations + script | 20% |
| 4 | توثيق السيناريوهات (Sprint 17.2-3) | 10% |

**نتائج هذه الجلسة**:
- ✅ Sprint 15 شغّال
- ✅ Timestamps في الـ UI
- ✅ 30+ seed records (5 contacts + 5 products + 3 projects per company × 3 = ~40 records)
- ✅ 3 سيناريوهات اختبار جاهزة + verification script
- ✅ وثيقة ملخص النظام

**مؤجل لـ Sprint 17.5 (مستقبل)**:
- Sprint 16.2 Customers/Suppliers CRUD الكامل (الـ dropdown في الـ invoice form)
- Sprint 16.3 Audit log UI

---

## 🎯 معايير القبول النهائية

1. **وظيفياً**: 
   - Invoice posted → قيد pending (not posted) ✓
   - المحاسب يعتمد → posted → يأثر على التقارير ✓
   - Manual journal entry → draft → المحاسب ينشره → posted ✓
   
2. **بيانات**:
   - 3 شركات، كل شركة فيها: 18 حساب + 5 عملاء + 5 موردين + 5 منتجات + 3 مشاريع
   - بيانات كافية لـ "demo كامل" بدون إدخال يدوي
   
3. **اختبار**:
   - 3 سيناريوهات محاسبية محددة، لكل واحد expected output
   - `scripts/test-scenarios.sh` ينفذ السيناريوهات آلياً
   
4. **توثيق**:
   - `docs/sprint-15-17-summary.md` — شامل ومرئي
   - `AGENTS.md` محدّث في كل feature
