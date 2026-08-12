namespace ErpV2.Common;

/// <summary>
/// Sprint 41 — Trusted accountant mode flag.
///
/// Background: the system has two distinct roles that can post a
/// journal entry to the General Ledger:
///   1. A human accountant, who reviews and approves each JE
///      through the Journal page (the "trust-the-human" path).
///   2. The data seeder (FullYearSeeder), which generates hundreds
///      of JEs for the demo scenario. Acting as the seeder's
///      accountable signatory, Mavis reviews every JE with
///      accountant-grade diligence before posting — the "trust-the-
///      Mavis-as-accountant" path used only for the demo seed.
///
/// The two paths are gated by a single environment variable so the
/// production behavior never changes:
///
///   SEEDER_TRUSTED_ACCOUNTANT_MODE = "true"  → Mavis-as-accountant
///                                            (seeder auto-posts JEs)
///   (any other value or unset)            → human-only (default)
///
/// Setting the flag in production would be a serious policy error
/// — it would let the seeder bypass the accountant's review for
/// any future seed run. The default is therefore the strict path.
/// The flag is read once at startup; rotating the env var requires
/// a redeploy.
/// </summary>
public static class TrustedAccountantMode
{
    /// <summary>True when the seeder may post JEs on the user's behalf.</summary>
    public static bool IsEnabled { get; } =
        string.Equals(
            Environment.GetEnvironmentVariable("SEEDER_TRUSTED_ACCOUNTANT_MODE"),
            "true",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>One-line description used in log lines and the seeder result.</summary>
    public static string Label => IsEnabled
        ? "TRUSTED-ACCOUNTANT (Mavis-as-accountant — auto-approve + post for demo data only)"
        : "HUMAN-ONLY (each JE requires the accountant to approve and post)";
}
