# Deploy to Render.com (Free Tier)

دليل خطوة بخطوة لنشر ERP-V2 على Render.com باستخدام الـ Free Tier.

## 📌 قبل ما تبدأ

**ما تحتاج**:
- ✅ حساب على GitHub (أو GitLab)
- ✅ حساب على Render.com (مجاني — سجّل من <https://render.com>)
- ✅ ملف الـ ZIP اللي أعطيتك إياه

**ما تحتاجش**:
- ❌ بطاقة ائتمان (Free Tier لا يطلبها)
- ❌ أي خبرة في DevOps

## 🏗️ المعمارية على Render

| الخدمة | النوع | الخطة | المنفذ | URL |
|--------|------|------|--------|-----|
| `erp-v2-db` | PostgreSQL | Free (90 يوم ثم مدفوع) | 5432 | داخلي فقط |
| `erp-v2-backend` | Docker (ASP.NET) | Free (مع sleep) | 8080 | `https://erp-v2-backend.onrender.com` |
| `erp-v2-frontend` | Docker (Next.js) | Free (مع sleep) | 3000 | `https://erp-v2-frontend.onrender.com` |

**ملاحظة عن Free Tier**:
- الـ web services **تنام بعد 15 دقيقة** عدم نشاط
- أول طلب بعدها يأخذ **~30 ثانية** (cold start)
- PostgreSQL مجاني **90 يوم** فقط، بعدها $7/شهر

---

## 📋 الخطوات

### الخطوة 1: ارفع المشروع على GitHub

```powershell
# 1. فك الـ ZIP في مكان ما
# 2. ادخل المجلد
cd erp-v2

# 3. تهيئة git
git init
git add .
git commit -m "Initial commit: ERP-V2"

# 4. أنشئ repo جديد على GitHub (عبر الموقع)
#    اسمه: erp-v2 (أو أي اسم)
#    اجعله Private لو حاب (الموضوع للعميل)

# 5. اربط وارفع
git remote add origin https://github.com/<your-username>/erp-v2.git
git branch -M main
git push -u origin main
```

### الخطوة 2: أنشئ Blueprint على Render

1. روح على **https://dashboard.render.com**
2. اضغط **New +** → **Blueprint**
3. اختر **Connect a repository**
4. اربط حساب GitHub (أول مرة فقط)
5. اختر الـ repo اللي رفعته (`erp-v2`)
6. Render **سيكتشف `render.yaml` تلقائياً** ويعرض الخدمات الثلاث
7. اضغط **Apply**

### الخطوة 3: انتظر البناء

Render يبني الـ 3 خدمات **بالتوازي**:
- 🗄️ **Database**: ~1 دقيقة (PostgreSQL جاهز)
- ⚙️ **Backend**: ~3-5 دقائق (.NET restore + build + publish)
- 🌐 **Frontend**: ~3-5 دقائق (npm install + next build)

**راقب التقدم** في صفحة الـ Dashboard — كل service له build log خاص.

### الخطوة 4: تحقق من الترتيب

الترتيب مهم — الـ Backend يحتاج الـ DB أولاً:

```
1. ✅ erp-v2-db         (status: Available)
2. ✅ erp-v2-backend    (status: Live)
3. ✅ erp-v2-frontend   (status: Live)
```

### الخطوة 5: افتح التطبيق

```
https://erp-v2-frontend.onrender.com
```

**أول مرة** ستنتظر ~30 ثانية (cold start — الـ service نائم). بعدها يفتح.

### الخطوة 6: سجّل دخول

```
admin@holding.ly  /  admin123
```

**لو نجح**: مبروك! 🎉 الـ demo شغّال على Render.

---

## 🔧 حل المشاكل

### "Cold start بطيء"
**السبب**: Free tier services تنام بعد 15 دقيقة.
**الحل**:
- استنى 30 ثانية في أول طلب
- لو مزعج للعميل: ارفع للـ Starter Plan ($7/شهر لكل service = always-on)

### "Database connection refused"
**السبب**: الـ DB لسا ما جهز.
**الحل**: انتظر 1-2 دقيقة بعد deploy، ثم أعد تشغيل الـ Backend من Dashboard.

### "CORS error" في الـ console
**السبب**: الـ Frontend URL ما تطابق `CORS__Origins` في الـ Backend.
**الحل**: روح لـ Backend Service → Environment → عدّل `CORS__Origins` → احفظ.

### "Service keeps restarting"
**السبب**: فشل في الـ health check.
**الحل**: شوف logs الـ service — غالباً مشكلة في الـ migrations أو DB connection.

### "Out of memory"
**السبب**: Free tier = 512 MB RAM فقط.
**الحل**: ارفع لـ Starter Plan ($7/شهر، 2 GB RAM).

---

## 💡 كيف تتجنب الـ Sleep (مجاناً)

استخدم **cron-job.org** (مجاني):
1. سجّل حساب على https://cron-job.org
2. أنشئ cron job:
   - URL: `https://erp-v2-backend.onrender.com/health`
   - Interval: كل **14 دقيقة** (قبل ما ينام)
3. الـ service يصير "دايقظ" بشكل دائم

⚠️ هذا حل مؤقت — لو العميل يستخدم النظام بانتظام، Starter Plan أفضل.

---

## 💰 التكلفة بعد 90 يوم

| الخدمة | Free Tier | Starter (مدفوع) |
|--------|-----------|-----------------|
| PostgreSQL | $0 (90 يوم) | $7/شهر |
| Backend | $0 (مع sleep) | $7/شهر (always-on) |
| Frontend | $0 (مع sleep) | $7/شهر (always-on) |
| **المجموع** | **$0** | **$21/شهر** |

**لو ميزانية العميل محدودة**: استخدم Free Tier مع cron-job للديمو.
**لو الإنتاج**: Starter Plan (أو ارجع لـ Hostinger VPS 2 بـ $5/شهر).

---

## 🚀 ترقية المشروع

لما تسوي تغيير في الكود:
```bash
git add .
git commit -m "Update feature X"
git push
```

Render **يكتشف الـ push تلقائياً** ويعيد البناء والنشر (auto-deploy).

---

## 🔐 تغيير بيانات الدخول (مهم!)

الـ seed accounts لها كلمات مرور افتراضية (`admin123`، `acc123`، `eng123`). **للإنتاج** غيّرها:

### طريقة 1: عبر الواجهة
- بعد الدخول، روح لـ Dashboard → Users → غيّر كلمات المرور

### طريقة 2: عبر الـ API
```bash
curl -X PUT https://erp-v2-backend.onrender.com/api/users/{id}/password \
  -H "Authorization: Bearer <admin-token>" \
  -H "Content-Type: application/json" \
  -d '{"newPassword": "strong-password-here"}'
```
> (الـ endpoint هذا غير موجود في الـ MVP — نضيفه في Sprint 2.)

### طريقة 3: عبر psql
```bash
# اتصل بالـ DB
render psql erp-v2-db

# غيّر كلمة مرور admin
UPDATE users SET password_hash = crypt('new-password', gen_salt('bf', 12)) WHERE email = 'admin@holding.ly';
```

---

## 📞 الدعم

- Render Docs: <https://docs.render.com>
- Render Community: <https://community.render.com>
- لمشاكل ERP-V2 نفسها: ابعث لي على طول

---

**الخلاصة**: Render.com = **أسرع طريقة لإطلاق demo مجاني** للعميل. لو وافق، ارفع للـ Hostinger VPS أو Render Starter للإنتاج.
