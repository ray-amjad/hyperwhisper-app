// RUST SHARED-CORE KEY-VALUE STORE (Win-3)
//
// TODO-verify (Windows/CI): Rust shared-core swap. UNVERIFIED / compile-only —
// no dotnet/MSVC available in this environment, so none of this has been
// compiled. Self-checked against the generated binding
// (Generated/RustCore/hyperwhisper_core.cs) by hand.
//
// Backs the `hw-license` Rust core's persistence. The core is pure (no clock,
// no storage): it reads/writes license, remote-config, and usage state through
// this `KeyValueStore`, and takes `now_unix_secs` at every time-dependent call.
//
// BACKWARD-COMPATIBILITY (the #1 migration risk):
// Existing Windows users must keep their trial seconds, lifetime model-download
// count, and active license across the upgrade. Today's state lives in:
//   - Windows Credential Manager  → raw license key ("LicenseKey" credential).
//   - %LOCALAPPDATA%\HyperWhisper\license.json → CachedLicenseInfo (status etc).
//   - %LOCALAPPDATA%\HyperWhisper\usage.json   → daily seconds, lifetime models.
//   - %LOCALAPPDATA%\HyperWhisper\config.json  → remote trial-limit override.
//
// Routing of the core's storage keys:
//   1. The license-KEY key (com.hyperwhisper.license.key) → Credential Manager,
//      reusing the EXISTING "LicenseKey" credential (resource =
//      AppPaths.CredentialResource). 1:1 mapping, so no key migration needed.
//   2. Everything else (license.customerId / lastValidation / cachedStatus,
//      config.*, usage.*) → a single flat kvstore.json holding a
//      Dictionary<string,string>, loaded once and rewritten on Set/Delete.
//
// ONE-TIME MIGRATION (guarded by the "kvstore.migrated" marker key):
//   - usage.json  → usage.dailySeconds, usage.dayIndex (= floor(LastUsageDate
//     UTC / 86400)), usage.modelsDownloaded (lifetime — seeded UNCONDITIONALLY,
//     irreversible if lost).
//   - license.json → license.customerId / cachedStatus / lastValidation
//     (status enum → Rust Display strings "Active"/"Trial"/"Expired"/"Invalid").
//   - The license-key credential needs no migration (already 1:1).
// Migration runs once in the constructor, BEFORE any License* call.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using HyperWhisper.Models;
using uniffi.hyperwhisper_core;
using Windows.Security.Credentials;

// `KeyValueStore`, the constant routing target, lives in uniffi.hyperwhisper_core
// and is `internal` (single assembly — fine). There is no `HyperWhisper.*` type
// named KeyValueStore / Limits / TrialLimits / UsageSnapshot, so no qualification
// is needed for those. Only the app's `HyperWhisper.Models.LicenseStatus` is a
// distinct name from the binding's `HwLicenseStatus`, so they never collide.
namespace HyperWhisper.Services;

/// <summary>
/// Credential-Manager + flat-JSON backed <see cref="KeyValueStore"/> for the
/// Rust license/usage core. A single shared instance is held by
/// <see cref="LicenseManager"/> and passed to every <c>License*</c> call.
/// </summary>
internal sealed class RustCoreKeyValueStore : KeyValueStore
{
    // ---- Core storage keys (MUST match hw-license/src/{cache,usage}.rs) ----

    // Routed to Credential Manager:
    private const string KLicenseKey = "com.hyperwhisper.license.key";

    // Routed to kvstore.json (license cache):
    private const string KCustomerId = "com.hyperwhisper.license.customerId";
    private const string KLastValidation = "com.hyperwhisper.license.lastValidation";
    private const string KCachedStatus = "com.hyperwhisper.license.cachedStatus";

    // Routed to kvstore.json (usage):
    private const string KUsageDailySeconds = "com.hyperwhisper.usage.dailySeconds";
    private const string KUsageDayIndex = "com.hyperwhisper.usage.dayIndex";
    private const string KUsageModelsDownloaded = "com.hyperwhisper.usage.modelsDownloaded";

    // Migration marker (kvstore.json only — never read by the core):
    private const string KMigrated = "kvstore.migrated";

    private const int SecsPerDay = 86_400;

    // ---- Credential Manager (license key) ----

    private static string VaultResource => AppPaths.CredentialResource;
    private const string LicenseKeyCredentialName = "LicenseKey";
    private readonly PasswordVault _vault = new();

    // ---- Flat JSON store (everything else) ----

    private static readonly string KvStorePath = AppPaths.Combine("kvstore.json");
    private static readonly string LegacyUsagePath = AppPaths.Combine("usage.json");
    private static readonly string LegacyLicensePath = AppPaths.Combine("license.json");

    private readonly object _lock = new();
    private readonly Dictionary<string, string> _map;

    // ---- Singleton (one shared instance, like the native singletons) ----

    private static RustCoreKeyValueStore? _instance;
    private static readonly object _instanceLock = new();

    public static RustCoreKeyValueStore Instance
    {
        get
        {
            if (_instance == null)
            {
                lock (_instanceLock)
                {
                    _instance ??= new RustCoreKeyValueStore();
                }
            }
            return _instance;
        }
    }

    private RustCoreKeyValueStore()
    {
        _map = LoadMap();
        MigrateLegacyIfNeeded();
    }

    // =========================================================================
    // KeyValueStore conformance
    // =========================================================================

    public string? Get(string key)
    {
        // Exact-match the license-key constant → Credential Manager; else JSON.
        if (key == KLicenseKey)
        {
            return RetrieveLicenseKeyFromVault();
        }

        lock (_lock)
        {
            return _map.TryGetValue(key, out var value) ? value : null;
        }
    }

    public void Set(string key, string value)
    {
        if (key == KLicenseKey)
        {
            SaveLicenseKeyToVault(value);
            return;
        }

        lock (_lock)
        {
            _map[key] = value;
            SaveMap();
        }
    }

    public void Delete(string key)
    {
        if (key == KLicenseKey)
        {
            ClearLicenseKeyFromVault();
            return;
        }

        lock (_lock)
        {
            if (_map.Remove(key))
            {
                SaveMap();
            }
        }
    }

    // =========================================================================
    // Flat JSON persistence
    // =========================================================================

    private static Dictionary<string, string> LoadMap()
    {
        try
        {
            if (!File.Exists(KvStorePath))
            {
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }

            var json = File.ReadAllText(KvStorePath);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            return loaded == null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(loaded, StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"RustCoreKeyValueStore: Failed to load kvstore.json: {ex.Message}");
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    // Caller must hold _lock.
    private void SaveMap()
    {
        try
        {
            var directory = Path.GetDirectoryName(KvStorePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(KvStorePath, JsonSerializer.Serialize(_map, options));
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"RustCoreKeyValueStore: Failed to save kvstore.json: {ex.Message}");
        }
    }

    // =========================================================================
    // Credential Manager (license key) — mirrors LicenseNetworkService
    // =========================================================================

    private string? RetrieveLicenseKeyFromVault()
    {
        try
        {
            var credential = _vault.Retrieve(VaultResource, LicenseKeyCredentialName);
            credential.RetrievePassword();
            return string.IsNullOrWhiteSpace(credential.Password) ? null : credential.Password;
        }
        catch
        {
            return null;
        }
    }

    private void SaveLicenseKeyToVault(string? licenseKey)
    {
        try
        {
            ClearLicenseKeyFromVault();
            if (!string.IsNullOrWhiteSpace(licenseKey))
            {
                _vault.Add(new PasswordCredential(VaultResource, LicenseKeyCredentialName, licenseKey.Trim()));
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"RustCoreKeyValueStore: Failed to store license key in Credential Manager: {ex.Message}");
        }
    }

    private void ClearLicenseKeyFromVault()
    {
        try
        {
            var existing = _vault.Retrieve(VaultResource, LicenseKeyCredentialName);
            _vault.Remove(existing);
        }
        catch
        {
            // Missing credential is expected for trial users and fresh installs.
        }
    }

    // =========================================================================
    // One-time legacy migration (seed the core's keys from legacy files)
    // =========================================================================

    private void MigrateLegacyIfNeeded()
    {
        lock (_lock)
        {
            if (_map.ContainsKey(KMigrated))
            {
                return;
            }

            try
            {
                MigrateUsage();
                MigrateLicenseCache();
            }
            catch (Exception ex)
            {
                LoggingService.Warn($"RustCoreKeyValueStore: Legacy migration error: {ex.Message}");
            }

            // Mark migrated regardless of partial failure so we don't re-seed and
            // clobber values the core may have already updated this session.
            _map[KMigrated] = "1";
            SaveMap();
            LoggingService.Info("RustCoreKeyValueStore: Legacy usage/license migration complete");
        }
    }

    // Caller holds _lock.
    private void MigrateUsage()
    {
        if (!File.Exists(LegacyUsagePath))
        {
            return;
        }

        var json = File.ReadAllText(LegacyUsagePath);
        var legacy = JsonSerializer.Deserialize<LegacyUsageData>(json);
        if (legacy == null)
        {
            return;
        }

        // Lifetime, irreversible — seed UNCONDITIONALLY.
        _map[KUsageModelsDownloaded] = legacy.ModelsDownloaded.ToString();

        // Day index = floor(LastUsageDate UTC / 86400). Windows already reset
        // usage on the UTC calendar day, so a plain UTC day index matches the
        // core's `now/86400` bucket when the app later passes UTC `now`.
        var lastUtc = legacy.LastUsageDate.ToUniversalTime();
        var lastUnix = ((DateTimeOffset)DateTime.SpecifyKind(lastUtc, DateTimeKind.Utc)).ToUnixTimeSeconds();
        var dayIndex = (long)Math.Floor(lastUnix / (double)SecsPerDay);

        _map[KUsageDailySeconds] = legacy.DailyUsageSeconds.ToString();
        _map[KUsageDayIndex] = dayIndex.ToString();
    }

    // Caller holds _lock.
    private void MigrateLicenseCache()
    {
        if (!File.Exists(LegacyLicensePath))
        {
            return;
        }

        var json = File.ReadAllText(LegacyLicensePath);
        var legacy = JsonSerializer.Deserialize<CachedLicenseInfo>(json);
        if (legacy?.ValidationResult == null)
        {
            return;
        }

        legacy.ValidationResult.ParseStatus();

        if (!string.IsNullOrWhiteSpace(legacy.ValidationResult.CustomerId))
        {
            _map[KCustomerId] = legacy.ValidationResult.CustomerId!;
        }

        // status enum → Rust Display strings ("Active"/"Trial"/"Expired"/"Invalid").
        _map[KCachedStatus] = StatusToRustString(legacy.ValidationResult.Status);

        // lastValidation: core stores unix-seconds (string) and tolerates a
        // fractional value (splits on '.'); we write whole seconds.
        var validatedUtc = DateTime.SpecifyKind(legacy.LastOnlineValidation.ToUniversalTime(), DateTimeKind.Utc);
        var validatedUnix = ((DateTimeOffset)validatedUtc).ToUnixTimeSeconds();
        _map[KLastValidation] = validatedUnix.ToString();

        // License key: a hardened build (>=7bc0a1ea) already lazily moved the key
        // from license.json into the vault, so it's normally there already. For a
        // user upgrading straight from a very early build (<=46a93392) that never
        // re-launched a hardened build, seed it here so they aren't forced to
        // re-validate. Vault Get/Set bypass `_lock`, safe to call while held.
        if (!string.IsNullOrWhiteSpace(legacy.LicenseKey)
            && string.IsNullOrEmpty(Get(KLicenseKey)))
        {
            Set(KLicenseKey, legacy.LicenseKey!);
        }
    }

    /// <summary>
    /// Map the app's <see cref="LicenseStatus"/> to the Rust core's persisted
    /// Display string. MUST match hw-license/src/cache.rs `status_to_str`.
    /// </summary>
    private static string StatusToRustString(LicenseStatus status) => status switch
    {
        LicenseStatus.Active => "Active",
        LicenseStatus.Trial => "Trial",
        LicenseStatus.Expired => "Expired",
        LicenseStatus.Invalid => "Invalid",
        _ => "Invalid"
    };

    // ---- Legacy usage.json shape (mirrors LicenseUsageTracker.UsageData) ----

    private sealed class LegacyUsageData
    {
        [System.Text.Json.Serialization.JsonPropertyName("daily_usage_seconds")]
        public int DailyUsageSeconds { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("models_downloaded")]
        public int ModelsDownloaded { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("last_usage_date")]
        public DateTime LastUsageDate { get; set; } = DateTime.UtcNow.Date;
    }
}
