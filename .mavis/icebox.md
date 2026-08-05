# ICEBOX — قائمة الإصلاحات والمهام المؤجلة

> المهام المتراكمة عبر Sprint 18-19. كل عنصر يحتوي:
> - **ID**: PREFIX-NNN
> - **Priority**: P0 (عاجل) | P1 (مهم) | P2 (مفيد)
> - **Triggered by**: متى / من أي محادثة
> - **Effort**: S (ساعة) | M (نصف يوم) | L (يوم+)

---

## 🐛 الإصلاحات المؤجلة

### [BUG-001] P1 — Pending page: المبلغ مضاعف (Math.max per line)
- **Discovered**: 2026-08-05 by user
- **Symptom**: صفحة `/journal/pending` يعرض المبلغ = Σ debit + Σ credit (مضاعف)
  - مثال: قيد 3 بنود (DR 4080 / CR 4000 / CR 80) يعرض 8160 بدل 4080
- **Root cause**: `pending/page.tsx` line 172: `e.lines.reduce((s, l) => s + Math.max(l.debit, l.credit), 0)`
- **Fix**: استخدم `s + l.debit` فقط (الـ balanced entry = debit = credit)
- **Status**: ✅ **FIXED in commit `pending-amount-fix`**

### [BUG-002] P2 — Page navigation layout
- **Discovered**: 2026-08-05 by user
- **Symptom**: لا يوجد "Expand/collapse" أو menu groups في الـ sidebar
- **Plan**: تجميع "التقارير المالية" تحت dropdown واحد (4 تقارير) لتخفيف ازدحام الـ sidebar
- **Effort**: S
- **Status**: 🚧 **IN PROGRESS**

### [BUG-003] P2 — Invoice `contactId` غير محفوظ في الـ DB
- **Discovered**: 2026-08-05 (diagnosis)
- **Symptom**: `inv.contactId` = None حتى لو الـ user اختار contact في الـ frontend
- **Root cause**: `InvoiceService.CreateAsync` ما يحفظ `contactId` في الـ INSERT
- **Fix**: أضف `contact_id` للـ INSERT statement
- **Effort**: S
- **Status**: ⏳ TODO

### [BUG-004] P3 — صفحة "قيد جديد" ما تدعم تعديل قيد موجود (PUT form)
- **Discovered**: Sprint 18
- **Symptom**: الـ user يقدر يحذف draft فقط، لكن ما يقدر يعدّل الـ lines
- **Status**: ⏳ TODO

### [BUG-005] P3 — Audit log UI مفقود
- **Discovered**: Sprint 18
- **Symptom**: جدول `audit_logs` يُكتب فيه لكن لا توجد صفحة لعرضه
- **Status**: ⏳ TODO (low priority)

---

## 🎨 تحسينات UX

### [UX-001] P1 — Sidebar group: "التقارير المالية"
- **Goal**: تجميع 4 تقارير في dropdown واحد
- **Why**: الـ sidebar فيه 14+ رابط، مزدحم
- **Plan**:
  - في `dashboard/layout.tsx`:
    - حوّل `navItems` إلى array من `groups`
    - الـ sidebar يعرض groups + sub-items
    - الـ active state يبقى على الـ sub-item المختار
- **Status**: 🚧 **IN PROGRESS** (this commit)

### [UX-002] P2 — Group "القيود اليومية" (sub: المعلقة)
- **Goal**: نفس الفكرة للتداخل
- **Status**: ⏳ TODO

### [UX-003] P2 — PDF export للتقارير
- **Goal**: زرار "طباعة" في كل تقرير (بعضهم موجود، بعضهم لا)
- **Status**: ⏳ TODO (already has print() in GL)

---

## 🚀 ميزات مقترحة (Sprint 20+)

### [FEAT-001] P2 — Bank reconciliation
### [FEAT-002] P2 — Multi-currency support (الآن LYD فقط)
### [FEAT-003] P3 — Year-end closing (إقفال السنة المالية)
### [FEAT-004] P3 — Customer statements (كشف حساب عميل)
### [FEAT-005] P3 — Supplier statements (كشف حساب مورّد)

---

## 📊 الإحصائيات

| الفئة | عدد المهام |
|-------|------------|
| 🐛 BUGs مفتوحة | 4 (1 ثابت، 1 جاري، 2 TODO) |
| 🎨 UX | 3 |
| 🚀 Features | 5 |
| **المجموع** | **12** |

## 🎯 الأولوية الآن

1. **UX-001** — Sidebar groups (في العمل الآن)
2. **BUG-003** — Invoice contactId
3. **UX-002** — Journal sub-menu
4. **BUG-004** — Draft edit form
