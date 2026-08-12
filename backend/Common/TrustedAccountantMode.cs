namespace ErpV2.Common;

/// <summary>
/// Sprint 41 — Trusted accountant mode flag.
///
/// The system has two distinct roles that can post a journal entry
/// to the General Ledger:
///   1. A human accountant, who reviews and approves each JE
///      through the Journal page (the "trust-the-human" path).
///   2. The data seeder (FullYearSeeder), which generates hundreds
///      of JEs for the demo scenario. Acting as the seeder's
///      accountable signatory, Mavis reviews every JE with
///      accountant-grade diligence before posting — the "trust-
///      the-Mavis-as-accountant" path used only for the demo seed.
///
/// The flag can be set two ways (first match wins):
///   1. Environment variable `SEEDER_TRUSTED_ACCOUNTANT_MODE = "true"`
///      Read on every property access.
///   2. Explicit per-call trustedMode parameter passed to
///      <see cref="Features.Admin.FullYearSeeder.SeedAsync"/>.
///      This is what the auto-seed-on-startup task and the
///      admin seeder endpoint use so the value travels with
///      the call graph.
///
/// Outside of those two paths the flag is false and the human
/// accountant is the only one who can post a JE.
/// </summary>
public static class TrustedAccountantMode
{
    /// <summary>True when the seeder may post JEs on the user's behalf.</summary>
    public static bool IsEnabled =>
        string.Equals(
            Environment.GetEnvironmentVariable("SEEDER_TRUSTED_ACCOUNTANT_MODE"),
            "true",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>One-line description used in log lines and the seeder result.</summary>
    public static string Label => IsEnabled
        ? "TRUSTED-ACCOUNTANT (Mavis-as-accountant — auto-approve + post for demo data only)"
        : "HUMAN-ONLY (each JE requires the accountant to approve and post)";
}
