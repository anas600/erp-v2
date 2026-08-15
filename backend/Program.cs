using System.Text;
using ErpV2.Common;
using ErpV2.Features.Auth;
using ErpV2.Features.Companies;
using ErpV2.Features.Accounts;
using ErpV2.Features.Journal;
using ErpV2.Features.Rules;
using ErpV2.Features.Reports;
using ErpV2.Features.Invoicing;
using ErpV2.Features.Intercompany;
using ErpV2.Features.FiscalYears;
using ErpV2.Features.Projects;
using ErpV2.Features.Contacts;
using ErpV2.Features.Users;
using ErpV2.Features.Products;
using ErpV2.Features.Admin;
using ErpV2.Features.Receipts;
using ErpV2.Features.Payments;
using ErpV2.Features.CostCenters;
using ErpV2.Migrations;
using FluentMigrator.Runner;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;

// ============================================================
// Connection string normalization
// ============================================================
// Render's `fromDatabase` reference returns the connection string in
// `postgresql://user:pass@host:port/db` URL format. Npgsql does NOT
// accept this format in its ConnectionString property — it requires
// `Host=...;Port=...;Database=...;Username=...;Password=...;`.
//
// We normalize the env var before any other code runs (including the
// seed migration, which calls `Environment.GetEnvironmentVariable`
// directly and would otherwise fail with
// "Format of the initialization string does not conform to
// specification starting at index 0").
//
// This makes render.yaml self-sufficient: `fromDatabase` works out of
// the box, no manual `ConnectionStrings__Default` env var required.
static string NormalizeConnectionString(string raw)
{
    if (string.IsNullOrWhiteSpace(raw)) return raw;
    if (!raw.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase) &&
        !raw.StartsWith("postgres://",    StringComparison.OrdinalIgnoreCase))
    {
        // Already in key=value (Npgsql) format — nothing to do.
        return raw;
    }

    // It's a URL — parse it manually. We can't pass it to
    // NpgsqlConnectionStringBuilder because that class only accepts
    // key=value format (it inherits from DbConnectionStringBuilder,
    // which uses '=' as separator and throws ArgumentException on
    // 'postgresql://' with the exact message we used to see in prod).
    var uri = new Uri(raw);
    var userInfo = uri.UserInfo.Split(':', 2);
    var user = Uri.UnescapeDataString(userInfo[0]);
    var pass = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : "";

    var builder = new Npgsql.NpgsqlConnectionStringBuilder
    {
        Host     = uri.Host,
        Port     = uri.IsDefaultPort ? 5432 : uri.Port,
        Database = uri.AbsolutePath.TrimStart('/'),
        Username = user,
        Password = pass,
    };

    // Map common query-string params to Npgsql equivalents. Render's
    // postgresql:// URLs may include ?sslmode=Require and similar.
    if (!string.IsNullOrEmpty(uri.Query))
    {
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length != 2) continue;
            var key   = Uri.UnescapeDataString(kv[0]).ToLowerInvariant();
            var value = Uri.UnescapeDataString(kv[1]);
            switch (key)
            {
                case "sslmode":
                    builder.SslMode = value.ToLowerInvariant() switch
                    {
                        "disable"     => Npgsql.SslMode.Disable,
                        "allow"       => Npgsql.SslMode.Allow,
                        "prefer"      => Npgsql.SslMode.Prefer,
                        "require"     => Npgsql.SslMode.Require,
                        "verify-ca"   => Npgsql.SslMode.VerifyCA,
                        "verify-full" => Npgsql.SslMode.VerifyFull,
                        _             => Npgsql.SslMode.Require
                    };
                    break;
                case "sslcert":
                case "sslkey":
                case "sslrootcert":
                    // Path-based TLS settings — pass through if present.
                    // NpgsqlConnectionStringBuilder exposes them as named
                    // properties; we set by name to keep this switch lean.
                    try
                    {
                        builder.GetType().GetProperty(
                            key == "sslrootcert" ? "RootCertificate" : key,
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
                        )?.SetValue(builder, value);
                    }
                    catch { /* best-effort */ }
                    break;
            }
        }
    }

    return builder.ConnectionString;
}

var rawConn = Environment.GetEnvironmentVariable("ConnectionStrings__Default");
string? normalized = null;
if (!string.IsNullOrWhiteSpace(rawConn))
{
    normalized = NormalizeConnectionString(rawConn);
    if (normalized != rawConn)
    {
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", normalized);
    }
}

// Use CreateSlimBuilder instead of CreateBuilder: it does NOT add the
// default JSON config sources (appsettings.json + appsettings.{env}.json),
// which means no FileSystemWatcher is ever created. Without this, the
// container hits the inotify instance limit (128) at startup and crashes
// before any of our code runs.
//
// `Sources.Clear()` in a CreateBuilder() flow is too late: the watchers
// are constructed inside CreateBuilder() before any user code runs.
//
// SlimBuilder also skips Kestrel HTTPS binding and the developer page,
// which is exactly what we want in a Render free-tier container.
var builder = WebApplication.CreateSlimBuilder(args);

// CreateSlimBuilder does NOT register the regex route constraint by
// default. Swashbuckle (Swagger) uses regex constraints in its internal
// route templates, so without this, the app crashes at startup with:
//
//   "A route parameter uses the regex constraint, which isn't registered.
//    If this application was configured using CreateSlimBuilder(...) or
//    AddRoutingCore(...) then this constraint is not registered by default."
//
// We re-register the regex constraint here. This is the only required
// manual re-registration when using SlimBuilder for this app.
builder.Services.Configure<Microsoft.AspNetCore.Routing.RouteOptions>(options =>
    options.SetParameterPolicy<Microsoft.AspNetCore.Routing.Constraints.RegexInlineRouteConstraint>("regex"));

// Configuration comes exclusively from environment variables in production
// (the 12-factor way). We deliberately do NOT add any JSON file sources:
// they require file watchers, and the env var is the source of truth on
// Render anyway. Local dev can `dotnet run` with .env loaded by the shell
// or use the appsettings.Development.json pattern (not added here because
// the production image must not depend on file watching).
builder.Configuration.AddEnvironmentVariables();

// Re-apply the normalized connection string to the configuration object
// (AddEnvironmentVariables above cached the raw URL value).
if (!string.IsNullOrWhiteSpace(normalized))
{
    builder.Configuration["ConnectionStrings:Default"] = normalized;
}

// ============================================================
// Services
// ============================================================

// CORS
var corsOrigins = builder.Configuration["CORS:Origins"]?.Split(',') ?? new[] { "http://localhost:3000" };
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins(corsOrigins)
     .AllowAnyHeader()
     .AllowAnyMethod()
     .AllowCredentials()));

// Authentication
var jwtKey = builder.Configuration["Jwt:Key"] ?? throw new InvalidOperationException("Jwt:Key not configured");
var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? "erp-v2";
var jwtAudience = builder.Configuration["Jwt:Audience"] ?? "erp-v2-client";
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtIssuer,
            ValidAudience = jwtAudience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            ClockSkew = TimeSpan.FromMinutes(5)
        };
    });
builder.Services.AddAuthorization();

// Database + Migrations
var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("ConnectionStrings:Default not configured");
builder.Services.AddSingleton<IDbConnectionFactory>(_ => new NpgsqlConnectionFactory(connectionString));
builder.Services.AddSingleton<IPasswordHasher, BcryptPasswordHasher>();
builder.Services.AddSingleton<JwtTokenService>();

// FluentMigrator
builder.Services
    .AddFluentMigratorCore()
    .ConfigureRunner(rb => rb
        .AddPostgres()
        .WithGlobalConnectionString(connectionString)
        .ScanIn(typeof(ErpV2.Migrations.MigrationRunner).Assembly).For.Migrations())
    .AddLogging(lb => lb.AddFluentMigratorConsole());

// Feature services
builder.Services.AddSingleton<AuthService>();
builder.Services.AddSingleton<CompanyService>();
builder.Services.AddSingleton<AccountService>();
builder.Services.AddSingleton<JournalService>();
builder.Services.AddSingleton<PostingEngine>();
builder.Services.AddSingleton<RuleService>();
builder.Services.AddSingleton<RuleEvaluator>();
builder.Services.AddSingleton<ReportService>();
builder.Services.AddSingleton<ReportingGate>();          // Sprint 32: TB gate
builder.Services.AddSingleton<InvoiceService>();
builder.Services.AddSingleton<ProjectCostAccountService>();     // Sprint 50: project L4 sub-ledgers
builder.Services.AddSingleton<ProjectService>();
builder.Services.AddSingleton<UserService>();
builder.Services.AddSingleton<ProductService>();
builder.Services.AddSingleton<ContactService>();
builder.Services.AddSingleton<CostCenterService>();
builder.Services.AddSingleton<ContactStatementService>();   // Sprint 25: per-contact view (كشف حساب)
builder.Services.AddSingleton<FiscalYearService>();          // Sprint 25: fiscal years + periods
builder.Services.AddSingleton<AdminService>();               // Sprint 26: cleanup + seed admin endpoints
builder.Services.AddSingleton<CoaSeeder>();                  // Sprint 31: full COA reseed
builder.Services.AddSingleton<DemoDataSeeder>();             // Sprint 26: seed 5 customers + 3 suppliers + 10 invoices + 5 receipts + 2 payments
builder.Services.AddSingleton<FullYearSeeder>();
builder.Services.AddSingleton<RealisticProjectSeeder>();     // Sprint 50: focused scenario seeder            // Sprint 39: full 12-month realistic data (use /api/admin/seed-full-year)
// FIX 2026-08-05: ReceiptService and PaymentService were created in
// Sprint 21 but never registered in DI. Same pattern as Intercompany
// in Sprint 24. Sprint 25 ContactStatementService depends on them, so
// the new endpoints (balance, statement, invoices) all 500'd.
// This is a re-apply — the original fix was lost in a force-push.
builder.Services.AddSingleton<ReceiptService>();
builder.Services.AddSingleton<PaymentService>();

// Sprint 36 — Contracting workflow (contracts + progress billings +
// WIP + client statement). Both services are additive; no existing
// service depends on them yet, but ContractService is the source of
// truth for advance/retention terms that BillingService reads.
builder.Services.AddSingleton<ContractService>();
builder.Services.AddSingleton<BillingService>();
// Sprint 55 — Field Measurement Book service
builder.Services.AddSingleton<FieldMeasurementService>();

// Sprint 38 — BOQ (Bill of Quantities) + Variations. Order matters
// for readability: LineItemService has no deps; VariationService
// depends on LineItemService (for the shared unit validator);
// BillingService now depends on VariationService (for the effective
// contract value used in work_completed_percent computation).
builder.Services.AddSingleton<LineItemService>();
builder.Services.AddSingleton<VariationService>();

// Web
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "ERP-V2 API",
        Version = "v1",
        Description = "Multi-Company Accounting System"
    });
});

var app = builder.Build();

// ============================================================
// Pipeline
// ============================================================
// FIX 2026-08-05: Global exception handler — surfaces real error
// to the client instead of an empty HTTP 500 body. Without this,
// any DI failure, SQL error, or runtime exception in a minimal
// API endpoint returns 200 OK with no body, making debugging
// impossible. See DEC-090 / cross-project lesson.
app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        var feature = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>();
        var ex = feature?.Error;
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("GlobalExceptionHandler");
        logger.LogError(ex, "Unhandled exception on {Method} {Path}",
            context.Request.Method, context.Request.Path);

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new
        {
            error = ex?.Message ?? "Unknown error",
            type = ex?.GetType().FullName ?? "Unknown",
            path = context.Request.Path.Value,
            method = context.Request.Method
        });
    });
});

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseSwagger();
app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "ERP-V2 v1"));

// Health check
app.MapGet("/", () => Results.Ok(new { name = "ERP-V2", status = "running", version = "1.0" }));
app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));

// ============================================================
// Feature endpoints
// ============================================================
AuthEndpoints.Map(app);
CompanyEndpoints.Map(app);
AccountEndpoints.Map(app);
JournalEndpoints.Map(app);
RuleEndpoints.Map(app);
ReportEndpoints.Map(app);
InvoiceEndpoints.Map(app);
ProjectEndpoints.Map(app);
UserEndpoints.Map(app);
ProductEndpoints.Map(app);
ContactEndpoints.Map(app);
AdminEndpoints.Map(app);
ReceiptEndpoints.Map(app);
PaymentEndpoints.Map(app);
CostCenterEndpoints.Map(app);
IntercompanyEndpoints.Map(app);
IntercompanyEliminationEndpoints.Map(app);

// Sprint 35 — Project P&L company-wide report (lives in /api/reports).
ProjectPnLReportEndpoints.Map(app);

// Sprint 36 — Contracting workflow. ContractEndpoints owns
// /api/contracts/* and /api/projects/{id}/contract. BillingEndpoints
// owns /api/billings/* and /api/projects/{id}/billings, /wip,
// /statement. The grouping is by URL prefix; both call
// RequireAuthorization via their own Map().
ContractEndpoints.Map(app);
BillingEndpoints.Map(app);

// Sprint 38 — BOQ (line items) + variations. These extend the
// Sprint 36 contracting surface without touching any existing
// routes. /api/contracts/{id}/line-items/* owns the BOQ CRUD +
// reorder + Excel/clipboard import. /api/contracts/{id}/variations
// + /api/variations/{id}/* own the variation lifecycle.
LineItemEndpoints.Map(app);
VariationEndpoints.Map(app);
// Sprint 55 — Field Measurement Book
FieldMeasurementEndpoints.MapFieldMeasurementEndpoints(app);

// Sprint 25 — Receivable/Payable settlement + Contact Statement + Fiscal Year
ContactStatementEndpoints.Map(app);
FiscalYearEndpoints.Map(app);
FiscalYearEndpoints.MapPeriods(app);

// ============================================================
// Run migrations on startup
// ============================================================
using (var scope = app.Services.CreateScope())
{
    var migrator = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();
    migrator.MigrateUp();
}

// ============================================================
// Sprint 41 — Auto-seed demo company on startup (test/preview only)
//
// Gated by env var `AUTO_SEED_DEMO=true`. When set, the backend
// spawns a background task ~5s after startup that:
//   1. Checks if the demo company has any invoices
//   2. If empty → runs the FullYearSeeder in trusted-accountant mode
//   3. Logs the result; doesn't block the HTTP listener
//
// Why: Render free tier's "deploy" is the fastest way to reset the
// demo data. Without this, every redeploy leaves the previous
// broken/partial state in place. With it, the moment the backend
// is reachable, the system is in a known-good demo state.
//
// Sprint 45 — DISABLED. The user wanted the Render free-tier server
// to sleep naturally (15 min idle → cold start). The auto-seed made
// 1 SQL query on every cold start, which counted as activity and
// kept the server from sleeping. The user can still re-seed manually
// via POST /api/admin/seed-full-year if needed.
// ============================================================
// Sprint 45: disabled auto-seed to let the server sleep. To re-enable,
// uncomment the block below AND set AUTO_SEED_DEMO=true in env.
if (false)
{
var autoSeedFlag = Environment.GetEnvironmentVariable("AUTO_SEED_DEMO");
var demoCompanyId = Environment.GetEnvironmentVariable("DEMO_COMPANY_ID");
if (string.Equals(autoSeedFlag, "true", StringComparison.OrdinalIgnoreCase)
    && !string.IsNullOrEmpty(demoCompanyId)
    && Guid.TryParse(demoCompanyId, out var demoCompanyGuid))
{
    var logger = app.Services.GetRequiredService<ILogger<Program>>();
    logger.LogInformation(
        "AUTO_SEED_DEMO=true: scheduling demo seed for company {CompanyId} in 5s",
        demoCompanyGuid);

    // Fire-and-forget so we don't block app.Run().
    _ = Task.Run(async () =>
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5));

            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>();
            var seeder = scope.ServiceProvider.GetRequiredService<FullYearSeeder>();

            // Check if the demo company already has data. If yes,
            // skip — we don't want to wipe a manually-edited state.
            using (var conn = db.CreateConnection())
            {
                var invoiceCount = await Dapper.SqlMapper.ExecuteScalarAsync<long>(
                    conn, "SELECT COUNT(*) FROM invoices WHERE company_id = @id;",
                    new { id = demoCompanyGuid });
                if (invoiceCount > 0)
                {
                    logger.LogInformation(
                        "AUTO_SEED: demo company already has {Count} invoices, skipping seed",
                        invoiceCount);
                    return;
                }
            }

            logger.LogInformation("AUTO_SEED: starting FullYearSeeder for {CompanyId}", demoCompanyGuid);
            var result = await seeder.SeedAsync(demoCompanyGuid, null, trustedMode: true);
            logger.LogInformation(
                "AUTO_SEED: completed — invoices={Inv}, receipts={Rec}, payments={Pay}, JEs={JE}, posted={Posted}, errors={Err}",
                result.InvoicesCreated, result.ReceiptsCreated, result.PaymentsCreated,
                result.JournalEntriesCreated, result.EntriesPosted, result.Errors.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "AUTO_SEED: background seed failed");
        }
    });
}
}

app.Run();


// Sprint 52 force-rebuild marker (2026-08-14T20:53:00Z)
// This comment changes the source so Docker's COPY layer cache
// invalidates and the new binary (with auto L4 sub-ledger
// creation in ContactService.CreateAsync) is actually built.
