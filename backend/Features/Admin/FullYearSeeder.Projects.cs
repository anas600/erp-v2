using Dapper;
using ErpV2.Common;
using ErpV2.Features.Projects;

namespace ErpV2.Features.Admin;

/// <summary>
/// FullYearSeeder partial — projects phase.
/// Creates 4 projects across the year, each with full BOQ, contract,
/// progress billings, and (where applicable) variations.
/// </summary>
public partial class FullYearSeedResult
{
    public int ProjectsCreated { get; set; }
    public int ContractsCreated { get; set; }
    public int BillingsCreated { get; set; }
    public int VariationsCreated { get; set; }
    public int LineItemsCreated { get; set; }
    public int CostCentersCreated { get; set; }
    public bool YearEndClosingCreated { get; set; }
    public int EntriesApproved { get; set; }
    public int EntriesPosted { get; set; }
}

public partial class FullYearSeeder
{
    /// <summary>
    /// 4 projects: construction (12 months), supply (4 months),
    /// services (3 months), maintenance (12 months small billings).
    /// </summary>
    private async Task SeedProjectsAsync(Guid companyId, Guid? userId)
    {
        // Pick 4 customers to be the project owners
        var constructionCustomer = _customerIds["CUST-001"]; // Ministry
        var supplyCustomer       = _customerIds["CUST-006"]; // Al-Baraka
        var servicesCustomer     = _customerIds["CUST-007"]; // Engineering office
        var maintenanceCustomer  = _customerIds["CUST-005"]; // Free zone

        // Project 1: Construction (Ministry) — 250,000 LYD, Sep 2025 → Aug 2026
        await SeedConstructionProjectAsync(companyId, "PRJ-001",
            "مبنى المكاتب الحكومي - المرحلة الأولى",
            constructionCustomer, 250_000m, FY_START, FY_END, userId);

        // Project 2: Supply (Al-Baraka) — 80,000 LYD, Jan → Apr 2026
        await SeedSupplyProjectAsync(companyId, "PRJ-002",
            "توريد حديد التسليح لمشروع سكني",
            supplyCustomer, 80_000m,
            new DateTime(2026, 1, 1), new DateTime(2026, 4, 30), userId);

        // Project 3: Services (Engineering Office) — 40,000 LYD, Mar → May 2026
        await SeedServicesProjectAsync(companyId, "PRJ-003",
            "استشارات هندسية لتقييم المنشآت",
            servicesCustomer, 40_000m,
            new DateTime(2026, 3, 1), new DateTime(2026, 5, 31), userId);

        // Project 4: Maintenance (Free Zone) — 25,000 LYD/year, monthly billings
        await SeedMaintenanceProjectAsync(companyId, "PRJ-004",
            "صيانة منشآت منطقة مصراتة الحرة",
            maintenanceCustomer, 25_000m, FY_START, FY_END, userId);
    }

    // ----------------------------------------------------------------
    // 1) Construction project
    // ----------------------------------------------------------------

    private async Task SeedConstructionProjectAsync(
        Guid companyId, string code, string name, Guid customerId,
        decimal contractValue, DateTime start, DateTime end, Guid? userId)
    {
        try
        {
            var project = await _projects.CreateAsync(new CreateProjectRequest(
                companyId, code, name, name,
                "مشروع بناء حكومي - هيكل خرساني + تشطيبات",
                start, end, contractValue, null,
                Type: "construction",
                CustomerId: customerId,
                ContractValue: contractValue,
                ExpectedEndDate: end,
                ProjectManager: "م. عبد الله الفيتوري",
                Location: "طرابلس - حي الأندلس"));

            // 8 BOQ line items
            var boq = new (string desc, string unit, decimal qty, decimal price)[]
            {
                ("حفر أساسات", "m3", 500m, 45m),      // 22,500
                ("خرسانة عادية", "m3", 300m, 380m),    // 114,000
                ("حديد تسليح", "ton", 25m, 4_500m),    // 112,500
                ("بناء جدران بلوك", "m2", 800m, 95m),  // 76,000
                ("أعمال كهرباء", "m2", 600m, 65m),     // 39,000
                ("أعمال سباكة", "m2", 600m, 55m),       // 33,000
                ("دهانات وتشطيبات", "m2", 1200m, 28m), // 33,600
                ("أعمال متنوعة", "lump", 1m, 18_400m)   // 18,400 (balance)
            };
            var totalBoq = boq.Sum(b => b.qty * b.price);

            // Create contract FIRST (line items go against contract)
            var contract = await _contracts.CreateAsync(project.Id, new CreateContractRequest(
                ContractNumber: "CNT-2025-001",
                ContractValue: contractValue,
                AdvancePercent: 10m,
                RetentionPercent: 5m,
                RetentionStartBilling: 2,
                StartDate: start,
                EndDate: end,
                Notes: "عقد إنشاءات - دفعة مقدمة 10% / ضمان 5% من المستخلص الثاني"));

            // Now create line items against the contract
            var lineItemIds = new List<Guid>();
            foreach (var (desc, unit, qty, price) in boq)
            {
                var li = await _lineItems.CreateAsync(contract.Id, new CreateLineItemRequest(
                    desc, unit, null, qty, price, null));
                lineItemIds.Add(li.Id);
                _result.LineItemsCreated++;
            }

            // 6 progress billings — distributed across 12 months
            var billingSchedule = new (int monthOffset, decimal percent)[]
            {
                (2, 0.10m),   // Nov 2025 — 10%
                (4, 0.15m),   // Jan 2026 — 15%
                (6, 0.20m),   // Mar 2026 — 20%
                (8, 0.25m),   // May 2026 — 25%
                (10, 0.20m),  // Jul 2026 — 20%
                (11, 0.10m)   // Aug 2026 — 10% (final)
            };

            for (int idx = 0; idx < billingSchedule.Length; idx++)
            {
                var (monthOffset, percent) = billingSchedule[idx];
                var billingDate = start.AddMonths(monthOffset);

                // Each billing takes percent of each line item (cumulative managed by service)
                var lineReqs = new List<CreateBillingLineItemRequest>();
                for (int i = 0; i < boq.Length; i++)
                {
                    var (desc, unit, qty, price) = boq[i];
                    var thisPortion = Math.Round(qty * percent, 3);
                    lineReqs.Add(new CreateBillingLineItemRequest(lineItemIds[i], thisPortion, null));
                }
                try
                {
                    var billing = await _billings.CreateAsync(project.Id, new CreateBillingRequest(
                        ContractId: contract.Id,
                        BillingNumber: $"BIL-001-{idx + 1:D2}",
                        BillingDate: billingDate,
                        PeriodFrom: start.AddMonths(Math.Max(0, monthOffset - 2)),
                        PeriodTo: billingDate,
                        WorkCompletedPercent: null, // calculated from line items
                        Notes: $"مستخلص {idx + 1}",
                        LineItems: lineReqs));
                    // Approve the billing → creates invoice + JE
                    await _billings.ApproveAsync(billing.Id, new ApproveBillingRequest(
                        billingDate.AddDays(5), null));
                    _result.BillingsCreated++;
                }
                catch (Exception ex)
                {
                    _result.Errors.Add($"Construction billing {idx + 1}: {ex.Message}");
                }
            }

            // 1 variation: add extra electrical work, +12,000 LYD
            try
            {
                var variation = await _variations.CreateAsync(contract.Id, new CreateVariationRequest(
                    "أعمال كهرباء إضافية - تركيب مولد طوارئ",
                    new DateTime(2026, 5, 15),
                    "أمر تغيير رقم 1 - بناءً على طلب الجهة"));
                await _variations.AddItemAsync(variation.Id, new AddVariationItemRequest(
                    "مولد طوارئ 50 KVA", "lump", null, 1m, 12_000m, true,
                    "يشمل التركيب والتوصيل"));
                await _variations.ApproveAsync(variation.Id, _mainUserId, new ApproveVariationRequest(DateTime.UtcNow));
                _result.VariationsCreated++;
            }
            catch (Exception ex) { _result.Errors.Add($"Construction variation: {ex.Message}"); }

            _result.ProjectsCreated++;
            _result.ContractsCreated++;
            _logger.LogInformation("FullYearSeeder: construction project done ({Billings} billings, BOQ total = {Total})",
                billingSchedule.Length, totalBoq);
        }
        catch (Exception ex) { _result.Errors.Add($"Construction project: {ex.Message}"); }
    }

    // ----------------------------------------------------------------
    // 2) Supply project
    // ----------------------------------------------------------------

    private async Task SeedSupplyProjectAsync(
        Guid companyId, string code, string name, Guid customerId,
        decimal contractValue, DateTime start, DateTime end, Guid? userId)
    {
        try
        {
            var project = await _projects.CreateAsync(new CreateProjectRequest(
                companyId, code, name, name,
                "توريد مواد بناء - حديد تسليح بمقاسات مختلفة",
                start, end, contractValue, null,
                Type: "supply",
                CustomerId: customerId,
                ContractValue: contractValue,
                ExpectedEndDate: end,
                ProjectManager: "م. سالم العريبي",
                Location: "مصراتة"));

            var boq = new (string desc, string unit, decimal qty, decimal price)[]
            {
                ("حديد تسليح 8مم",  "ton", 5m, 4_200m),  // 21,000
                ("حديد تسليح 12مم", "ton", 8m, 4_500m),  // 36,000
                ("حديد تسليح 16مم", "ton", 3m, 4_800m),  // 14,400
                ("أسلاك ربط",       "ton", 1m, 8_600m)   //  8,600
            };

            var contract = await _contracts.CreateAsync(project.Id, new CreateContractRequest(
                ContractNumber: "CNT-2026-002",
                ContractValue: contractValue,
                AdvancePercent: 0m,
                RetentionPercent: 0m,
                RetentionStartBilling: 1,
                StartDate: start,
                EndDate: end,
                Notes: "عقد توريد - بدون دفعة مقدمة"));

            var lineItemIds = new List<Guid>();
            foreach (var (desc, unit, qty, price) in boq)
            {
                var li = await _lineItems.CreateAsync(contract.Id, new CreateLineItemRequest(
                    desc, unit, null, qty, price, null));
                lineItemIds.Add(li.Id);
                _result.LineItemsCreated++;
            }

            // 3 billings: 40% / 35% / 25%
            var sched = new (int monthOffset, decimal pct)[]
            {
                (1, 0.40m), (2, 0.35m), (3, 0.25m)
            };
            for (int idx = 0; idx < sched.Length; idx++)
            {
                var (monthOffset, pct) = sched[idx];
                var bDate = start.AddMonths(monthOffset);
                var lineReqs = new List<CreateBillingLineItemRequest>();
                for (int i = 0; i < boq.Length; i++)
                {
                    var (desc, unit, qty, price) = boq[i];
                    lineReqs.Add(new CreateBillingLineItemRequest(
                        lineItemIds[i], Math.Round(qty * pct, 3), null));
                }
                try
                {
                    var billing = await _billings.CreateAsync(project.Id, new CreateBillingRequest(
                        ContractId: contract.Id,
                        BillingNumber: $"BIL-002-{idx + 1:D2}",
                        BillingDate: bDate,
                        PeriodFrom: start.AddMonths(Math.Max(0, monthOffset - 1)),
                        PeriodTo: bDate,
                        WorkCompletedPercent: null,
                        Notes: $"دفعة توريد {idx + 1}",
                        LineItems: lineReqs));
                    await _billings.ApproveAsync(billing.Id, new ApproveBillingRequest(
                        bDate.AddDays(7), null));
                    _result.BillingsCreated++;
                }
                catch (Exception ex) { _result.Errors.Add($"Supply billing {idx + 1}: {ex.Message}"); }
            }

            _result.ProjectsCreated++;
            _result.ContractsCreated++;
        }
        catch (Exception ex) { _result.Errors.Add($"Supply project: {ex.Message}"); }
    }

    // ----------------------------------------------------------------
    // 3) Services project
    // ----------------------------------------------------------------

    private async Task SeedServicesProjectAsync(
        Guid companyId, string code, string name, Guid customerId,
        decimal contractValue, DateTime start, DateTime end, Guid? userId)
    {
        try
        {
            var project = await _projects.CreateAsync(new CreateProjectRequest(
                companyId, code, name, name,
                "خدمات استشارية - فحص وتقييم",
                start, end, contractValue, null,
                Type: "service",
                CustomerId: customerId,
                ContractValue: contractValue,
                ExpectedEndDate: end,
                ProjectManager: "م. فاطمة الزهراء",
                Location: "طرابلس"));

            var boq = new (string desc, string unit, decimal qty, decimal price)[]
            {
                ("فحص بصري",        "day", 15m, 800m),    // 12,000
                ("اختبارات مختبرية", "piece", 50m, 320m), // 16,000
                ("كتابة التقارير",   "piece", 4m, 3_000m)  // 12,000
            };

            var contract = await _contracts.CreateAsync(project.Id, new CreateContractRequest(
                ContractNumber: "CNT-2026-003",
                ContractValue: contractValue,
                AdvancePercent: 20m,
                RetentionPercent: 0m,
                RetentionStartBilling: 1,
                StartDate: start,
                EndDate: end,
                Notes: "عقد خدمات - دفعة مقدمة 20%"));

            var lineItemIds = new List<Guid>();
            foreach (var (desc, unit, qty, price) in boq)
            {
                var li = await _lineItems.CreateAsync(contract.Id, new CreateLineItemRequest(
                    desc, unit, null, qty, price, null));
                lineItemIds.Add(li.Id);
                _result.LineItemsCreated++;
            }

            // 2 billings: 50% (advance recovery) + 50%
            var sched = new (int monthOffset, decimal pct, string n)[]
            {
                (0, 0.50m, "مستخلص دفعة مقدمة"),
                (2, 0.50m, "مستخلص ختامي")
            };
            for (int idx = 0; idx < sched.Length; idx++)
            {
                var (monthOffset, pct, n) = sched[idx];
                var bDate = start.AddMonths(monthOffset);
                var lineReqs = new List<CreateBillingLineItemRequest>();
                for (int i = 0; i < boq.Length; i++)
                {
                    var (desc, unit, qty, price) = boq[i];
                    lineReqs.Add(new CreateBillingLineItemRequest(
                        lineItemIds[i], Math.Round(qty * pct, 3), null));
                }
                try
                {
                    var billing = await _billings.CreateAsync(project.Id, new CreateBillingRequest(
                        ContractId: contract.Id,
                        BillingNumber: $"BIL-003-{idx + 1:D2}",
                        BillingDate: bDate,
                        PeriodFrom: start,
                        PeriodTo: bDate,
                        WorkCompletedPercent: null,
                        Notes: n,
                        LineItems: lineReqs));
                    await _billings.ApproveAsync(billing.Id, new ApproveBillingRequest(
                        bDate.AddDays(7), null));
                    _result.BillingsCreated++;
                }
                catch (Exception ex) { _result.Errors.Add($"Services billing {idx + 1}: {ex.Message}"); }
            }

            _result.ProjectsCreated++;
            _result.ContractsCreated++;
        }
        catch (Exception ex) { _result.Errors.Add($"Services project: {ex.Message}"); }
    }

    // ----------------------------------------------------------------
    // 4) Maintenance project (12 monthly small billings)
    // ----------------------------------------------------------------

    private async Task SeedMaintenanceProjectAsync(
        Guid companyId, string code, string name, Guid customerId,
        decimal yearlyValue, DateTime start, DateTime end, Guid? userId)
    {
        try
        {
            var project = await _projects.CreateAsync(new CreateProjectRequest(
                companyId, code, name, name,
                "صيانة دورية - 12 شهر",
                start, end, yearlyValue, null,
                Type: "maintenance",
                CustomerId: customerId,
                ContractValue: yearlyValue,
                ExpectedEndDate: end,
                ProjectManager: "أ. محمد الشريف",
                Location: "مصراتة - المنطقة الحرة"));

            var boq = new (string desc, string unit, decimal qty, decimal price)[]
            {
                ("صيانة كهربائية",  "lump", 12m, 800m),     // 9,600
                ("صيانة ميكانيكية", "lump", 12m, 700m),     // 8,400
                ("مواد استهلاكية",  "lump", 12m, 583.33m)   // 7,000
            };

            var contract = await _contracts.CreateAsync(project.Id, new CreateContractRequest(
                ContractNumber: "CNT-2025-004",
                ContractValue: yearlyValue,
                AdvancePercent: 0m,
                RetentionPercent: 0m,
                RetentionStartBilling: 1,
                StartDate: start,
                EndDate: end,
                Notes: "عقد صيانة سنوية"));

            var lineItemIds = new List<Guid>();
            foreach (var (desc, unit, qty, price) in boq)
            {
                var li = await _lineItems.CreateAsync(contract.Id, new CreateLineItemRequest(
                    desc, unit, null, qty, price, null));
                lineItemIds.Add(li.Id);
                _result.LineItemsCreated++;
            }

            // 12 monthly billings (1 unit of each line item per month)
            for (int m = 0; m < 12; m++)
            {
                var bDate = start.AddMonths(m).AddDays(15);
                var lineReqs = new List<CreateBillingLineItemRequest>();
                foreach (var liId in lineItemIds)
                    lineReqs.Add(new CreateBillingLineItemRequest(liId, 1m, null));
                try
                {
                    var billing = await _billings.CreateAsync(project.Id, new CreateBillingRequest(
                        ContractId: contract.Id,
                        BillingNumber: $"BIL-004-{m + 1:D2}",
                        BillingDate: bDate,
                        PeriodFrom: start.AddMonths(m),
                        PeriodTo: start.AddMonths(m).AddMonths(1).AddDays(-1),
                        WorkCompletedPercent: null,
                        Notes: $"صيانة شهر {m + 1}",
                        LineItems: lineReqs));
                    await _billings.ApproveAsync(billing.Id, new ApproveBillingRequest(
                        bDate.AddDays(7), null));
                    _result.BillingsCreated++;
                }
                catch (Exception ex) { _result.Errors.Add($"Maintenance billing {m + 1}: {ex.Message}"); }
            }

            _result.ProjectsCreated++;
            _result.ContractsCreated++;
        }
        catch (Exception ex) { _result.Errors.Add($"Maintenance project: {ex.Message}"); }
    }
}
