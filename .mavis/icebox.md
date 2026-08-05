# ICEBOX — قائمة الإصلاحات والمهام المؤجلة

> المهام المتراكمة عبر Sprint 18-19. كل عنصر يحتوي:
> - **ID**: BUG-NNN / UX-NNN / FEAT-NNN
> - **Priority**: P0 (عاجل) | P1 (مهم) | P2 (مفيد) | P3 (مؤجل)
> - **Discovered**: متى / من أي محادثة
> - **Effort**: S (ساعة) | M (نصف يوم) | L (يوم+)

---

## 🐛 الإصلاحات المؤجلة (BUGs)

### [BUG-006] ✅ FIXED — Manual journal entry: لا يحدّث الجدول بعد الحفظ
- **Discovered**: 2026-08-05 by user
- **Symptom**: User يضيف قيد manual، يحفظه كمسوده، لكن لا يظهر في الـ list
- **Root cause**: الـ modal كان يقفل قبل ما `load()` يكتمل، فالـ user ما شاف الـ row الجديد
- **Reality**: الـ DB فيه الـ entry (3 drafts: JV-2026-0007, 0008, 0009)
- **Fix**:
  - Success banner يعرض "تم حفظ القيد JV-XXXX كمسودة" بعد الـ API success
  - `load()` يحدث قبل إغلاق الـ modal
  - Auto-refresh كل 30 ثانية (يحلّ stale list)
- **Status**: ✅ **FIXED in `journal-save-feedback`**

### [BUG-001] ✅ FIXED — Pending page: المبلغ مضاعف
- **Discovered**: 2026-08-05 by user
- **Symptom**: قيد 3 بنود (DR 4080 / CR 4000 / CR 80) يعرض 8160 بدل 4080
- **Root cause**: `Math.max(debit, credit)` per line، يضاعف المبلغ
- **Fix**: `Σ debit` فقط (الـ balanced entry = debit = credit)
- **Status**: ✅ **FIXED in commit `8e4d0b3`**

### [BUG-002] ✅ FIXED — Sidebar groups UX
- **Status**: ✅ **FIXED in `8e4d0b3`** (5 groups, التقارير collapsible)

### [BUG-003] P2 — Invoice `contactId` غير محفوظ في الـ DB
- **Discovered**: 2026-08-05 (diagnosis)
- **Symptom**: `inv.contactId` = None حتى لو الـ user اختار contact في الـ frontend
- **Root cause**: `InvoiceService.CreateAsync` ما يحفظ `contact_id` في الـ INSERT
- **Fix**: أضف `contact_id` للـ INSERT statement
- **Effort**: S
- **Status**: ⏳ TODO

### [BUG-004] P3 — صفحة "قيد جديد" ما تدعم تعديل قيد موجود (PUT form)
- **Status**: ⏳ TODO

### [BUG-005] P3 — Audit log UI مفقود
- **Status**: ⏳ TODO

---

## 🎨 تحسينات UX

### [UX-001] ✅ FIXED — Sidebar group: "التقارير المالية"
- **Status**: ✅ **FIXED in `8e4d0b3`**

### [UX-002] P2 — Group "القيود اليومية" (sub: المعلقة)
- **Status**: ⏳ TODO

### [UX-003] P2 — PDF export للتقارير
- **Status**: ⏳ TODO (GL has print())

---

## 🚀 ميزات مقترحة (Sprint 20+)

### [FEAT-001] P2 — Bank reconciliation
### [FEAT-002] P2 — Multi-currency support (الآن LYD فقط)
### [FEAT-003] P3 — Year-end closing
### [FEAT-004] P3 — Customer statements (كشف حساب عميل)
### [FEAT-005] P3 — Supplier statements (كشف حساب مورّد)

---

## 📊 الإحصائيات

| الفئة | عدد |
|-------|-----|
| ✅ BUGs ثابتة | 3 (BUG-001, 002, 006) |
| 🐛 BUGs مفتوحة | 3 (BUG-003, 004, 005) |
| 🎨 UX | 2 |
| 🚀 Features | 5 |

## 🎯 الأولوية الآن

1. **BUG-003** — Invoice contactId (سريع)
2. **UX-002** — Journal sub-menu
3. **BUG-004** — Draft edit form
