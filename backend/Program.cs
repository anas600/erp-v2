using System.Text;
using ErpV2.Common;
using ErpV2.Features.Auth;
using ErpV2.Features.Companies;
using ErpV2.Features.Accounts;
using ErpV2.Features.Journal;
using ErpV2.Features.Rules;
using ErpV2.Features.Reports;
using ErpV2.Migrations;
using FluentMigrator.Runner;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

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
        .ScanIn(typeof(MigrationRunner).Assembly).For.Migrations())
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

// ============================================================
// Run migrations on startup
// ============================================================
using (var scope = app.Services.CreateScope())
{
    var migrator = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();
    migrator.MigrateUp();
}

app.Run();
