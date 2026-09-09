using Microsoft.EntityFrameworkCore;
using HyperWhisper.Data;
using HyperWhisper.Data.Entities;
using HyperWhisper.Models;
using HyperWhisper.PortableApplication.Persistence;
using HyperWhisper.SharedCore;
using HyperWhisper.Utilities;

namespace HyperWhisper.Services;

/// <summary>
/// MODE SERVICE
///
/// Manages mode persistence and CRUD operations.
/// Stores modes in SQLite database at %LOCALAPPDATA%\HyperWhisper\hyperwhisper.db
///
/// THREAD SAFETY:
/// - All operations are synchronized via lock
/// - Per-operation DbContext instances for safety
///
/// DEFAULT MODES:
/// - Default modes are seeded by DatabaseInitializer at app startup
/// - Selected mode ID is stored in SettingsService
/// </summary>
public class ModeService
{
    // =========================================================================
    // SINGLETON
    // =========================================================================

    private static ModeService? _instance;
    private static readonly object _lock = new();

    public static ModeService Instance
    {
        get
        {
            if (_instance == null)
            {
                lock (_lock)
                {
                    _instance ??= new ModeService();
                }
            }
            return _instance;
        }
    }

    // =========================================================================
    // EVENTS
    // =========================================================================

    /// <summary>Event fired when a mode is created, updated, or deleted.</summary>
    public event EventHandler<Mode>? ModeChanged;

    /// <summary>Event fired when the selected mode changes.</summary>
    public event EventHandler<Mode>? ModeSelected;

    // =========================================================================
    // CONSTRUCTOR
    // =========================================================================

    private ModeService()
    {
        // EF Core database is initialized by DatabaseInitializer at app startup
        // Migrate local modes to cloud when this build has no local transcription runtime.
        MigrateModesForArm64();
        // Heal any rows where ModelType is null but Model is a real local-model
        // identifier — the GUI reads ModelType. A short window of API-created
        // modes wrote Model only.
        HealMissingModelTypes();
        NormalizeLegacyCloudModeValues();
        // A database can arrive with two default modes or none — a backup
        // restored from another machine is the realistic route (issue #536).
        // Repair it once, before anything reads the flag.
        EnforceDefaultModeInvariant();
    }

    // =========================================================================
    // THE DEFAULT-MODE INVARIANT (issue #536)
    // =========================================================================

    /// <summary>
    /// Make exactly one mode the default again, if something left the database
    /// with two or with none.
    /// </summary>
    /// <remarks>
    /// Every write below already keeps the invariant, so this exists for the
    /// rows nothing here wrote: a restored backup, which
    /// <see cref="BackupService"/> puts straight into the DbSet, and a database
    /// that was already broken before this code shipped. Idempotent and silent
    /// when there is nothing to repair.
    /// </remarks>
    public void EnforceDefaultModeInvariant()
    {
        lock (_lock)
        {
            try
            {
                using var context = new HyperWhisperDbContext();
                if (!ApplyDefaultModeInvariant(context, null, null)) return;
                context.SaveChanges();
                LoggingService.Info(
                    "ModeService: repaired the default-mode flag — exactly one mode is the default again");
            }
            catch (Exception ex)
            {
                LoggingService.Warn($"ModeService: EnforceDefaultModeInvariant failed — {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Apply the shared decision (<c>hw-modes</c>, through
    /// <see cref="DefaultModePolicy"/>) to a context the caller is about to save.
    /// Returns whether anything changed.
    /// </summary>
    /// <param name="pending">
    /// A row that is being added and is therefore not in the database yet, so
    /// the query below cannot see it. The rule is a whole-set rule, and a plan
    /// computed without the new row would leave the set with two defaults.
    /// </param>
    /// <param name="preferred">
    /// The mode the caller is trying to make the default, or null when it has no
    /// opinion and this is only a repair.
    /// </param>
    private static bool ApplyDefaultModeInvariant(
        HyperWhisperDbContext context,
        Mode? pending,
        Guid? preferred)
    {
        var all = context.Modes.OrderBy(m => m.SortOrder).ToList();
        if (pending != null && all.All(m => m.Id != pending.Id))
        {
            all.Add(pending);
        }
        return DefaultModePolicy.Apply(all, preferred);
    }

    /// <summary>
    /// Whether <paramref name="stored"/> may be renamed to
    /// <paramref name="newName"/>. The default mode's name is fixed — the mode
    /// editor disables the field and says so (PR #535), and this is what makes
    /// that true for every other write path.
    /// </summary>
    public static bool CanRename(Mode stored, string newName) =>
        DefaultModePolicy.CheckRename(stored, newName) == PortableModeNameChange.Allowed;

    /// <summary>
    /// Whether <paramref name="id"/> may have its default flag written to
    /// <paramref name="requestedIsDefault"/>. Only one combination is refused:
    /// clearing the flag on the one mode that carries it, which would leave the
    /// app with no default at all.
    /// </summary>
    public bool CanWriteDefaultFlag(Guid id, bool requestedIsDefault) =>
        DefaultModePolicy.CheckDefaultFlag(GetAllModes(), id, requestedIsDefault)
            == PortableDefaultFlagChange.Allowed;

    /// <summary>
    /// Last line: a write that would rename the default mode keeps the stored
    /// name instead.
    /// </summary>
    /// <remarks>
    /// The two layers above this answer differently, on purpose. The Local API
    /// is a contract, so it calls <see cref="CanRename"/> first and REFUSES,
    /// because a caller told "ok" while its rename was dropped would keep
    /// sending it. The mode editor disables the field, so it never asks. This
    /// is for everything else — and it repairs rather than throws, because
    /// <see cref="SaveMode"/> is also how onboarding rolls a mode back, and a
    /// throw there would abandon the rollback over a field the caller did not
    /// mean to change.
    /// </remarks>
    private static void KeepDefaultModeName(Mode stored, Mode incoming)
    {
        if (CanRename(stored, incoming.Name)) return;
        LoggingService.Warn(
            $"ModeService: refused to rename the default mode '{stored.Name}' — its name is fixed");
        incoming.Name = stored.Name;
    }

    /// <summary>
    /// One-shot back-fill for Modes created via the Local API before it
    /// learned to write <c>ModelType</c>. The GUI keys off <c>ModelType</c>
    /// in seven reader sites; without this, API-created Whisper modes load
    /// the wrong (default) model in the GUI. Idempotent: only touches rows
    /// where ModelType is null and Model is a non-cloud value.
    /// </summary>
    private void HealMissingModelTypes()
    {
        lock (_lock)
        {
            try
            {
                using var context = new HyperWhisperDbContext();
                var rows = context.Modes
                    .Where(m => m.ModelType == null && m.Model != null && m.Model != "cloud")
                    .ToList();
                if (rows.Count == 0) return;
                foreach (var m in rows)
                {
                    m.ModelType = m.Model;
                }
                context.SaveChanges();
                LoggingService.Info($"ModeService: healed {rows.Count} mode(s) with missing ModelType");
            }
            catch (Exception ex)
            {
                LoggingService.Warn($"ModeService: HealMissingModelTypes failed — {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Rewrites legacy cloud routing identifiers to the canonical storage values
    /// used by the current mode editor and macOS migrations. Idempotent, and it
    /// preserves non-default user tier choices when folding retired standalone
    /// Azure/Google provider aliases into HyperWhisper Cloud tiers.
    /// </summary>
    internal static void NormalizeLegacyCloudModeValues()
    {
        lock (_lock)
        {
            try
            {
                using var context = new HyperWhisperDbContext();
                var modes = context.Modes.ToList();
                var changedFields = 0;

                foreach (var mode in modes)
                {
                    var existingTier = mode.CloudAccuracyTier;
                    var normalizedTier = CloudAccuracyTierExtensions
                        .FromString(existingTier)
                        .ToStorageValue();

                    if (!string.Equals(existingTier, normalizedTier, StringComparison.Ordinal))
                    {
                        mode.CloudAccuracyTier = normalizedTier;
                        changedFields++;
                    }

                    var normalizedPostProcessingModel = CloudPostProcessingModelExtensions
                        .FromString(mode.CloudPostProcessingModel)
                        .ToStorageValue();

                    if (!string.Equals(mode.CloudPostProcessingModel, normalizedPostProcessingModel, StringComparison.Ordinal))
                    {
                        mode.CloudPostProcessingModel = normalizedPostProcessingModel;
                        changedFields++;
                    }

                    var normalizedProvider = AppClassification.CloudSttCatalog.Shared
                        .NormalizeCloudProvider(mode.CloudProvider);
                    if (!string.Equals(normalizedProvider.Provider, mode.CloudProvider, StringComparison.Ordinal))
                    {
                        mode.CloudProvider = normalizedProvider.Provider;
                        changedFields++;
                    }

                    if (!string.IsNullOrEmpty(normalizedProvider.AccuracyTier)
                        && !string.Equals(mode.CloudAccuracyTier, normalizedProvider.AccuracyTier, StringComparison.Ordinal))
                    {
                        var tierWasDefaultOrEmpty =
                            string.IsNullOrWhiteSpace(existingTier)
                            || string.Equals(normalizedTier, CloudAccuracyTier.DeepgramNova3.ToStorageValue(), StringComparison.Ordinal);

                        if (tierWasDefaultOrEmpty)
                        {
                            mode.CloudAccuracyTier = normalizedProvider.AccuracyTier;
                            changedFields++;
                        }
                    }

                    if (!string.IsNullOrEmpty(mode.CloudTranscriptionModel))
                    {
                        var provider = CloudTranscriptionProviderExtensions.FromIdentifier(mode.CloudProvider);
                        var normalizedModel = CloudTranscriptionModels.ResolveModelAlias(mode.CloudTranscriptionModel, provider);
                        if (!string.Equals(mode.CloudTranscriptionModel, normalizedModel, StringComparison.Ordinal))
                        {
                            mode.CloudTranscriptionModel = normalizedModel;
                            changedFields++;
                        }
                    }
                }

                if (changedFields == 0) return;

                context.SaveChanges();
                LoggingService.Info($"ModeService: normalized {changedFields} legacy cloud mode field(s)");
            }
            catch (Exception ex)
            {
                LoggingService.Warn($"ModeService: NormalizeLegacyCloudModeValues failed - {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Migrates local modes to cloud when no local transcription runtime is
    /// available in the current build. This converts existing local modes to
    /// HyperWhisper Cloud (which doesn't require an API key).
    /// </summary>
    private void MigrateModesForArm64()
    {
        lock (_lock)
        {
            try
            {
                using var context = new HyperWhisperDbContext();

                var localModes = context.Modes
                    .Where(m => m.ProviderType == "local")
                    .AsEnumerable()
                    .Where(m => !IsLocalEngineSupported(m.LocalEngine))
                    .ToList();

                if (localModes.Count == 0)
                    return;

                foreach (var mode in localModes)
                {
                    LoggingService.Info($"ModeService: Migrating mode '{mode.Name}' from local to cloud because local transcription is unavailable");
                    mode.ProviderType = "cloud";
                    mode.CloudProvider = "hyperwhisper"; // HyperWhisper Cloud - no API key required
                    mode.ModifiedDate = DateTime.UtcNow;
                }

                context.SaveChanges();
                LoggingService.Info($"ModeService: local transcription migration complete - {localModes.Count} modes converted");
            }
            catch (DbUpdateException ex)
            {
                LoggingService.Error("ModeService: ARM64 migration failed", ex);
                throw;
            }
        }
    }

    private static bool IsLocalEngineSupported(string? localEngine)
    {
        return string.Equals(localEngine, "parakeet", StringComparison.OrdinalIgnoreCase)
            ? PlatformHelper.SupportsParakeetTranscription
            : PlatformHelper.SupportsWhisperTranscription;
    }

    // =========================================================================
    // PUBLIC METHODS - CRUD
    // =========================================================================

    /// <summary>Gets all modes sorted by SortOrder.</summary>
    public List<Mode> GetAllModes()
    {
        lock (_lock)
        {
            using var context = new HyperWhisperDbContext();
            return context.Modes
                .OrderBy(m => m.SortOrder)
                .ToList();
        }
    }

    /// <summary>Gets a mode by ID.</summary>
    public Mode? GetMode(Guid id)
    {
        lock (_lock)
        {
            using var context = new HyperWhisperDbContext();
            return context.Modes.Find(id);
        }
    }

    /// <summary>Gets the default mode.</summary>
    public Mode? GetDefaultMode()
    {
        lock (_lock)
        {
            using var context = new HyperWhisperDbContext();
            return context.Modes.FirstOrDefault(m => m.IsDefault)
                ?? context.Modes.OrderBy(m => m.SortOrder).FirstOrDefault();
        }
    }

    /// <summary>Gets the currently selected mode.</summary>
    public Mode? GetSelectedMode()
    {
        lock (_lock)
        {
            var selectedId = SettingsService.Instance.SelectedModeId;

            using var context = new HyperWhisperDbContext();

            if (selectedId.HasValue)
            {
                var mode = context.Modes.Find(selectedId.Value);
                if (mode != null) return mode;
            }

            // Fall back to default
            return context.Modes.FirstOrDefault(m => m.IsDefault)
                ?? context.Modes.OrderBy(m => m.SortOrder).FirstOrDefault();
        }
    }

    /// <summary>Creates or updates a mode.</summary>
    public void SaveMode(Mode mode)
    {
        lock (_lock)
        {
            try
            {
                using var context = new HyperWhisperDbContext();

                mode.ModifiedDate = DateTime.UtcNow;

                var existing = context.Modes.Find(mode.Id);
                if (existing != null)
                {
                    KeepDefaultModeName(existing, mode);
                    // Update existing - use Entry.CurrentValues pattern for clean update
                    context.Entry(existing).CurrentValues.SetValues(mode);
                    LoggingService.Info($"ModeService: Updated mode '{mode.Name}'");
                }
                else
                {
                    mode.CreatedDate = DateTime.UtcNow;
                    context.Modes.Add(mode);
                    LoggingService.Info($"ModeService: Created mode '{mode.Name}'");
                }

                ApplyDefaultModeInvariant(context, mode, mode.IsDefault ? mode.Id : null);
                context.SaveChanges();
            }
            catch (DbUpdateException ex)
            {
                LoggingService.Error($"ModeService: Database error saving mode '{mode.Name}'", ex);
                throw;
            }
        }

        // Fire event outside lock to prevent deadlock
        ModeChanged?.Invoke(this, mode);
    }

    /// <summary>Deletes a mode. Cannot delete the last remaining mode.</summary>
    public bool DeleteMode(Guid id)
    {
        Mode? mode = null;
        Mode? newSelectedMode = null;

        lock (_lock)
        {
            try
            {
                using var context = new HyperWhisperDbContext();

                // Cannot delete if it's the last mode
                if (context.Modes.Count() <= 1)
                {
                    LoggingService.Warn("ModeService: Cannot delete last mode");
                    return false;
                }

                mode = context.Modes.Find(id);
                if (mode == null)
                {
                    LoggingService.Warn($"ModeService: Mode {id} not found");
                    return false;
                }

                var modeName = mode.Name; // Capture for logging
                context.Modes.Remove(mode);
                context.SaveChanges();

                // Deleting the default mode used to leave no default at all, and
                // the app then merely ACTED as if the lowest-SortOrder mode were
                // one — with an editable name and no hint (issue #536). Move the
                // flag for real instead.
                if (ApplyDefaultModeInvariant(context, null, null))
                {
                    context.SaveChanges();
                }

                // If deleted mode was selected, select first remaining mode
                if (SettingsService.Instance.SelectedModeId == id)
                {
                    newSelectedMode = context.Modes.OrderBy(m => m.SortOrder).FirstOrDefault();
                    if (newSelectedMode != null)
                    {
                        SettingsService.Instance.SelectedModeId = newSelectedMode.Id;
                    }
                }

                LoggingService.Info($"ModeService: Deleted mode '{modeName}'");
            }
            catch (DbUpdateException ex)
            {
                LoggingService.Error($"ModeService: Database error deleting mode {id}", ex);
                return false;
            }
        }

        // Fire events outside lock to prevent deadlock
        if (newSelectedMode != null)
        {
            ModeSelected?.Invoke(this, newSelectedMode);
        }
        if (mode != null)
        {
            ModeChanged?.Invoke(this, mode);
        }
        return true;
    }

    /// <summary>Updates an existing mode.</summary>
    public void UpdateMode(Mode mode)
    {
        lock (_lock)
        {
            try
            {
                using var context = new HyperWhisperDbContext();

                var existing = context.Modes.Find(mode.Id);
                if (existing != null)
                {
                    KeepDefaultModeName(existing, mode);
                    context.Entry(existing).CurrentValues.SetValues(mode);
                    ApplyDefaultModeInvariant(context, mode, mode.IsDefault ? mode.Id : null);
                    context.SaveChanges();
                    LoggingService.Info($"ModeService: Updated mode '{mode.Name}'");
                }
            }
            catch (DbUpdateException ex)
            {
                LoggingService.Error($"ModeService: Database error updating mode '{mode.Name}'", ex);
                throw;
            }
        }

        // Fire event outside lock to prevent deadlock
        ModeChanged?.Invoke(this, mode);
    }

    /// <summary>Sets the selected mode.</summary>
    public void SetSelectedMode(Guid id)
    {
        Mode? mode = null;

        lock (_lock)
        {
            using var context = new HyperWhisperDbContext();

            mode = context.Modes.Find(id);
            if (mode != null)
            {
                SettingsService.Instance.SelectedModeId = id;
                LoggingService.Info($"ModeService: Selected mode '{mode.Name}'");
            }
        }

        // Fire event outside lock to prevent deadlock
        if (mode != null)
        {
            ModeSelected?.Invoke(this, mode);
        }
    }

}
