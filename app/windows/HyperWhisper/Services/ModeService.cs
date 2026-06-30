using Microsoft.EntityFrameworkCore;
using HyperWhisper.Data;
using HyperWhisper.Data.Entities;
using HyperWhisper.Models;
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
    // CONSTANTS
    // =========================================================================

    // Well-known mode IDs for default modes (matching macOS)
    public static readonly Guid DefaultModeId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    public static readonly Guid VoiceToTextModeId = Guid.Parse("00000000-0000-0000-0000-000000000002");
    public static readonly Guid MessageModeId = Guid.Parse("00000000-0000-0000-0000-000000000003");
    public static readonly Guid MailModeId = Guid.Parse("00000000-0000-0000-0000-000000000004");
    public static readonly Guid NoteModeId = Guid.Parse("00000000-0000-0000-0000-000000000005");
    public static readonly Guid MeetingModeId = Guid.Parse("00000000-0000-0000-0000-000000000006");

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
                    context.Entry(existing).CurrentValues.SetValues(mode);
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

    /// <summary>Sets the default mode.</summary>
    public void SetDefaultMode(Guid id)
    {
        lock (_lock)
        {
            try
            {
                using var context = new HyperWhisperDbContext();

                var modes = context.Modes.ToList();
                foreach (var mode in modes)
                {
                    mode.IsDefault = mode.Id == id;
                }

                context.SaveChanges();
                LoggingService.Info($"ModeService: Set default mode to {id}");
            }
            catch (DbUpdateException ex)
            {
                LoggingService.Error("ModeService: Error setting default mode", ex);
                throw;
            }
        }
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
