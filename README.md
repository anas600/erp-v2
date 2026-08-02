# ERP-V2 — Multi-Company Accounting System

نظام ERP مبسّط قائم على **Next.js 15 + .NET 8 + PostgreSQL**، مصمم ليعمل على **VPS خاص بالعميل** مع قابلية تخصيص كاملة عبر **محرك قواعد عمل (Business Rules Engine)**.

> **📚 التوثيق الجديد (نمط DOX):** المشروع يتبع إطار [agent0ai/dox](https://github.com/agent0ai/dox). كل مجلد رئيسي له `AGENTS.md` يشرح مسؤوليته وقواعده. اقرأ [`AGENTS.md`](./AGENTS.md) في الجذر أولاً.

---

## ⚡ التشغيل السريع (5 دقائق)

```bash
# 1. انسخ ملف البيئة
cp .env.example .env

# 2. (اختياري) عدّل إصدار PostgreSQL لو عندك 15 محلياً
#    عدّل POSTGRES_VERSION=15 في .env

# 3. شغّل
docker compose up -d --build

# 4. افتح المتصفح
#    الواجهة:  http://localhost:3000
#    الـ API:   http://localhost:5000
#    الـ Swagger:  http://localhost:5000/swagger
#    الـ DB:    localhost:5432
```

**بيانات الدخول الافتراضية** (بعد التشغيل):
| المستخدم | كلمة المرور | الدور |
|---------|------------|------|
| `admin@holding.ly` | `admin123` | Super Admin |
| `accountant@company-a.ly` | `acc123` | Accountant |
| `engineer@company-a.ly` | `eng123` | Project Engineer |

---

## 🗂️ هيكل المشروع + التوثيق (DOX Tree)

كل مجلد رئيسي له ملف `AGENTS.md` يشرح الغرض والقواعد. اقرأها بالترتيب من الجذر للأسفل:

```
erp-v2/
├── AGENTS.md                 ← 🚀 ابدأ هنا (قواعد المشروع الكاملة)
├── CONSTITUTION.md           ← 📜 القواعد غير القابلة للتغيير
├── README.md                 ← هذا الملف
├── docker-compose.yml
├── .env.example
│
├── backend/AGENTS.md         ← .NET 8 backend root
│   ├── Common/AGENTS.md
│   ├── Features/AGENTS.md
│   │   ├── Auth/AGENTS.md
│   │   ├── Companies/AGENTS.md
│   │   ├── Accounts/AGENTS.md
│   │   ├── Journal/AGENTS.md        ← ⭐ Posting Engine
│   │   ├── Rules/AGENTS.md          ← ⭐ Rules Engine
│   │   └── Reports/AGENTS.md
│   └── Migrations/AGENTS.md
│
├── frontend/AGENTS.md        ← Next.js 15 frontend root
│   └── src/
│       ├── AGENTS.md
│       ├── app/AGENTS.md
│       └── lib/AGENTS.md
│
└── docs/AGENTS.md            ← أدلة بشرية
    ├── architecture.md       ← معمارية النظام
    ├── user-guide.md         ← دليل المستخدم بالعربية
    └── deployment.md         ← دليل النشر على Hostinger VPS
```

---

## 💡 الميزات الرئيسية

### 1. إدارة الشركات (Multi-Company)
- شركة قابضة (Holding) + شركات تابعة (Self-reference tree)
- تبديل بين الشركات عبر Company Switcher
- عزل بيانات كامل بـ `company_id`

### 2. الصلاحيات والأدوار (RBAC)
- 6 أدوار جاهزة: `super_admin`, `holding_admin`, `company_admin`, `accountant`, `project_engineer`, `viewer`
- إدارة من لوحة الأدمن
- تحكم دقيق بالموديول (محاسب = Finance، مهندس = Projects)

### 3. محرك القواعد (Business Rules Engine) ⭐
- العميل يضيف قواعد جديدة من الواجهة
- تخزين JSON في DB → مرنة وقابلة للتعديل
- تطبيق فوري بدون نشر كود جديد
- 6 قوالب جاهزة (purchase, sales, payment, receipt, depreciation, project milestone)

### 4. محرك الترحيل (Posting Engine) ⭐
- مدعوم بطبيعة الحساب (Account Nature)
- **A = L + E** يتحقق منها تلقائياً في كل قيد
- **Expenses = Revenues** في قائمة الدخل

### 5. التقارير
- ميزان المراجعة (Trial Balance)
- قائمة الدخل (Income Statement)
- الميزانية (Balance Sheet)

---

## 🔧 التخصيص

### إضافة قاعدة جديدة
1. افتح `/dashboard/rules` كـ Super Admin
2. اضغط **"قاعدة جديدة"**
3. اختر نوع الحدث، الشروط، الأكشنز
4. اختبر بـ Test Sandbox قبل التفعيل

### إضافة حساب جديد لشجرة الحسابات
1. افتح `/dashboard/accounts`
2. اضغط **"حساب جديد"**
3. حدد: الكود، الاسم، النوع، الطبيعة

للمزيد من التفاصيل، راجع [`docs/user-guide.md`](./docs/user-guide.md).

---

## 📋 سيناريو الديمو (5 دقائق)

1. **Login** كـ `admin@holding.ly` / `admin123`
2. **Dashboard** → إجمالي الأصول والخصوم
3. **Companies** → إضافة/تعديل شركة
4. **Accounts** → شجرة الحسابات + إضافة حساب
5. **Journal** → إنشاء قيد يدوي + ترحيل
6. **Trial Balance** → تحقق من توازن الميزان
7. **Rules** → عرض القواعد الجاهزة وتشغيلها (▶️)

---

## 🛠️ Troubleshooting

### `npm error ERESOLVE unable to resolve dependency tree` (في بناء الفرونتند)
**السبب**: تعارض peer dependency بين Next.js و React.
**الحل**: 
- في `frontend/Dockerfile` أضفنا `--legacy-peer-deps` إلى `npm install` كحماية
- لو لسا يطلع الخطأ، شغّل يدوياً:
  ```powershell
  cd frontend
  npm install --legacy-peer-deps
  ```
- أو رقّي Next.js لآخر إصدار مستقر متوافق مع React 19 (15.1.6 أو أحدث)

### "open //./pipe/dockerDesktopLinuxEngine: The system cannot find the file specified"
**السبب**: Docker Desktop مش شغّال على جهازك.
**الحل**:
1. افتح **Docker Desktop** من قائمة Start
2. انتظر حتى يصير الـ icon في System Tray **أخضر** (10-30 ثانية)
3. تحقق:
   ```powershell
   docker ps
   # لازم يطلع list (حتى لو فاضي) بدون error
   ```
4. أعد `docker compose up -d --build`

### Port already in use
```bash
# غيّر المنافذ في .env
POSTGRES_PORT=5433
BACKEND_PORT=5001
FRONTEND_PORT=3001
```

### DB connection refused
```bash
docker compose ps
docker compose logs db
```

### Frontend can't reach backend
- لو تشغّل خارج Docker، غيّر `NEXT_PUBLIC_API_URL` في `.env` ليشير لـ `http://localhost:5000`
- لو خلف proxy، عدّل `CORS_ORIGINS` في `.env`

### PostgreSQL محلي يتعارض مع Docker
- لو عندك PG محلي شغّال على port 5432، غيّر `POSTGRES_PORT=5433` في `.env`
- أو أوقف الـ PG المحلي قبل تشغيل Docker

### Reset everything (مسح كل البيانات + صور Docker)
```bash
# امسح المجلد القديم كلياً
rmdir /s /q erp-v2

# فك الضغط الجديد
# ثم:
cd erp-v2
docker compose down -v
docker system prune -a   # امسح كل الصور المخبأة
docker compose up -d --build
```

### Login fails with 401
- تأكد إن الـ DB شغّال والـ migrations تمت
- `docker compose logs backend` يجب يعرض "Database connection established"

---

## 📚 مراجع إضافية

- [`docs/architecture.md`](./docs/architecture.md) — معمارية النظام
- [`docs/user-guide.md`](./docs/user-guide.md) — دليل المستخدم (عربي)
- [`docs/deployment.md`](./docs/deployment.md) — النشر على Hostinger VPS
- [`docs/deploy-render.md`](./docs/deploy-render.md) — النشر المجاني على Render.com
- [`docs/deploy-hf.md`](./docs/deploy-hf.md) — النشر على Hugging Face
- [`CONSTITUTION.md`](./CONSTITUTION.md) — القواعد غير القابلة للتغيير
- [`AGENTS.md`](./AGENTS.md) — القواعد الهندسية (ابدأ هنا)
- [نمط DOX](https://github.com/agent0ai/dox) — الإطار المستخدم للتوثيق

## 🚀 خيارات النشر

| الخيار | التكلفة | المناسب لـ | الدليل |
|--------|---------|-----------|--------|
| **Hostinger VPS 2** (docker-compose) | $4-8/شهر | **الإنتاج** الحقيقي | [deployment.md](./docs/deployment.md) |
| **Render.com** (Blueprint) | **$0** (Free Tier) | Demo سريع للعميل | [deploy-render.md](./docs/deploy-render.md) |
| **Hugging Face Space** (Docker) | $9/شهر (PRO) | Demo محدود | [deploy-hf.md](./docs/deploy-hf.md) |

---

**🔚 مبروك! النظام جاهز.**
لو عندك أي سؤال بعد التشغيل، تواصل معي على طول.
