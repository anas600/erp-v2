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
/// Sprint 45 — Runtime mode switching.
/// The flag can now be set THREE ways (first match wins):
///   1. <see cref="SetOverride"/> — a static in-memory override set
///      by the admin endpoint POST /api/admin/set-trusted-mode.
///      This is the fast path for switching modes without redeploying.
///   2. Environment variable `SEEDER_TRUSTED_ACCOUNTANT_MODE = "true"`
///      Read on every property access as a fallback.
///   3. Explicit per-call trustedMode parameter passed to
///      <see cref="Features.Admin.FullYearSeeder.SeedAsync"/>.
///      This is what the auto-seed-on-startup task and the
///      admin seeder endpoint use so the value travels with
///      the call graph.
///
/// Outside of those paths the flag is false and the human
/// accountant is the only one who can post a JE.
///
/// Important: the override is process-local (in-memory). On
/// Render's free tier the service restarts on every cold start,
/// so the override resets to the env-var default after each
/// restart. On a paid tier with persistent state, the override
/// survives until explicitly cleared or the process restarts.
/// </summary>
public static class TrustedAccountantMode
{
    // Process-local override. -1 = unset (fall through to env var).
    private static int _override = -1;

    /// <summary>
    /// True when the seeder (or anyone else using trustedMode=true)
    /// may post JEs on the user's behalf. Returns the override
    /// if set, otherwise the env var.
    /// </summary>
    public static bool IsEnabled =>
        _override >= 0
            ? _override == 1
            : string.Equals(
                Environment.GetEnvironmentVariable("SEEDER_TRUSTED_ACCOUNTANT_MODE"),
                "true",
                StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Runtime toggle. Called by the admin endpoint to switch
    /// modes without redeploying. The override is in-memory
    /// and resets on process restart.
    /// </summary>
    /// <param name="enabled">true to force TRUSTED-ACCOUNTANT, false to force HUMAN-ONLY, null to clear the override</param>
    public static void SetOverride(bool? enabled)
    {
        if (enabled is null) _override = -1;
        else _override = enabled.Value ? 1 : 0;
    }

    /// <summary>
    /// True if the user has explicitly overridden the env var via
    /// the admin endpoint. Lets the admin UI show the actual
    /// effective value vs the env-var default.
    /// </summary>
    public static bool HasOverride => _override >= 0;

    /// <summary>The current effective value (0 = human-only, 1 = trusted).</summary>
    public static int OverrideValue => _override;

    /// <summary>One-line description used in log lines and the seeder result.</summary>
    public static string Label => IsEnabled
        ? "TRUSTED-ACCOUNTANT (Mavis-as-accountant — auto-approve + post for demo data only)"
        : "HUMAN-ONLY (each JE requires the accountant to approve and post)";

    /// <summary>For the admin UI: explains where the current value comes from.</summary>
    public static string Source => _override switch
    {
        1 => "runtime override (TRUSTED)",
        0 => "runtime override (HUMAN-ONLY)",
        _ => string.Equals(
            Environment.GetEnvironmentVariable("SEEDER_TRUSTED_ACCOUNTANT_MODE"),
            "true",
            StringComparison.OrdinalIgnoreCase)
            ? "env var SEEDER_TRUSTED_ACCOUNTANT_MODE=true"
            : "default (HUMAN-ONLY)"
    };
}
