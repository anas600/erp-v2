using System.Text;
using ErpV2.Common;
using ErpV2.Features.Auth;
using ErpV2.Features.Companies;
using ErpV2.Features.Accounts;
using ErpV2.Features.Journal;
using ErpV2.Features.Rules;
using ErpV2.Features.Reports;
using ErpV2.Features.Invoicing;
using ErpV2.Features.Projects;
using ErpV2.Features.Contacts;
using ErpV2.Features.Users;
using ErpV2.Features.Products;
using ErpV2.Features.Admin;
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
builder.Services.AddSingleton<InvoiceService>();
builder.Services.AddSingleton<ProjectService>();
builder.Services.AddSingleton<UserService>();
builder.Services.AddSingleton<ProductService>();
builder.Services.AddSingleton<ContactService>();

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

// ============================================================
// Run migrations on startup
// ============================================================
using (var scope = app.Services.CreateScope())
{
    var migrator = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();
    migrator.MigrateUp();
}

app.Run();
