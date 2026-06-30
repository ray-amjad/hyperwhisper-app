using System.Windows;
using HyperWhisper.Data;
using HyperWhisper.Localization;
using HyperWhisper.Services;
using HyperWhisper.Services.LocalApi;
using HyperWhisper.Services.Transcription;

namespace HyperWhisper;

public partial class App : WpfApplication
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // SINGLE-INSTANCE CHECK: Prevent duplicate instances (e.g., installer + auto-restart)
        if (!SingleInstanceGuard.TryAcquire())
        {
            SingleInstanceGuard.SignalExistingInstance();
            Shutdown(0);
            return;
        }

        // CRITICAL: Register exception handlers FIRST, before any initialization,
        // so that startup crashes are logged instead of silently terminating.
        // Without this, background thread crashes and native library failures
        // cause the app to show a white screen and close with no error info.
        DispatcherUnhandledException += (s, args) =>
        {
            LoggingService.Error("Unhandled UI exception", args.Exception);
            SentryService.Capture(args.Exception, "Unhandled UI exception");
            WpfMessageBox.Show(Loc.S("errors.unhandled.message", args.Exception.Message), Loc.S("errors.unhandled.title"), MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            LoggingService.Error("Unhandled non-UI exception (IsTerminating=" + args.IsTerminating + ")", ex);
            SentryService.Capture(ex, "Unhandled non-UI exception");
        };

        TaskScheduler.UnobservedTaskException += (s, args) =>
        {
            LoggingService.Error("Unobserved task exception", args.Exception);
            SentryService.Capture(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };

        // Clean up old log files (older than 7 days) on startup
        LoggingService.CleanupOldLogs(keepDays: 7);

        LoggingService.Info("========== APPLICATION STARTING ==========");
        LoggingService.LogSystemInfo();
        LoggingService.LogHangHypotheses();

        // DATABASE INITIALIZATION
        // Initialize database with auto-migration before any services that use it
        try
        {
            DatabaseInitializer.InitializeAsync().Wait();
        }
        catch (Exception ex)
        {
            LoggingService.Error("Failed to initialize database", ex);
            WpfMessageBox.Show(Loc.S("errors.database.initFailed", ex.Message), Loc.S("errors.database.title"), MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        // ORPHANED TRANSCRIPT RECOVERY
        // Any transcript still in Processing status at startup is from a previous
        // session whose worker is long dead (crash, kill, OS restart mid-transcription).
        // Flip them to Failed with a clear reason so they stop spinning in History
        // and become retryable. See tasks/windows/phils-feedback/05-processing-audio-stuck-state.md
        try
        {
            int recovered = HistoryService.Instance.RecoverOrphanedProcessingTranscripts();
            if (recovered > 0)
            {
                LoggingService.Info($"Recovered {recovered} orphaned Processing transcript(s) on startup");
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"Failed to recover orphaned Processing transcripts: {ex.Message}");
            // Continue — recovery failure shouldn't block app startup
        }

        // SENTRY INITIALIZATION
        // Initialize Sentry for error tracking if user has opted in (default: true)
        // Must be done early to catch any startup errors, but after logging is available
        if (SettingsService.Instance.EnableErrorLogging)
        {
            SentryService.Initialize();
            LoggingService.Info("Sentry error logging enabled");
        }
        else
        {
            LoggingService.Info("Sentry error logging disabled by user preference");
        }

        // FIRST-LAUNCH DEFAULTS
        // On fresh install, automatically register the app to start with Windows.
        // Users can disable this later in Settings > General.
        if (SettingsService.Instance.IsFirstLaunch && !AppPaths.IsAppDataRootOverridden)
        {
            LoggingService.Info("First launch detected - enabling launch at startup");
            StartupService.Instance.Enable();
        }
        else if (SettingsService.Instance.IsFirstLaunch)
        {
            LoggingService.Info("First launch detected in isolated app-data profile - skipping launch at startup registration");
        }

        // THEME INITIALIZATION
        // Apply the user's preferred theme (or system theme if set to System mode)
        // Must be done after SettingsService is accessible but before UI is shown
        ThemeService.Instance.Initialize();

        // AUTO-UPDATE INITIALIZATION
        // Initialize NetSparkle update service after settings are loaded
        // Will only start background checking if user has enabled it
        UpdateService.Initialize();

        // AUTO-DELETE INITIALIZATION
        // Initialize auto-delete cleanup service after database is ready
        // Will periodically clean up old transcripts if user has enabled it
        try
        {
            AutoDeleteService.Instance.Initialize();
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"Failed to initialize auto-delete service: {ex.Message}");
            // Continue - auto-delete failure shouldn't block app startup
        }

        // LICENSE INITIALIZATION
        // Load stored license from cache (validates against server if cache expired)
        // Fire-and-forget to avoid blocking UI thread - license will validate in background
        // Using Task.Run to avoid deadlock from .Wait() on UI thread
        _ = Task.Run(async () =>
        {
            try
            {
                await LicenseManager.Instance.LoadStoredLicenseAsync();
            }
            catch (Exception ex)
            {
                LoggingService.Warn($"Failed to load stored license: {ex.Message}");
                // Continue with trial mode - don't block app startup
            }
        });

        // LOCAL API SERVER
        // Off by default. When the user has enabled it via Settings → Local API,
        // bring up the loopback HTTP host so MCP/cURL scripts can talk to the
        // app immediately on launch.
        try
        {
            var whisperModels = new WhisperModelService();
            var parakeetModels = new ParakeetModelService();
            var localLlmModels = new LocalLlmModelService();
            var modelLibrary = new ModelLibraryManager(
                whisperModels,
                parakeetModels,
                localLlmModels,
                ApiKeyService.Instance,
                CloudProviderHealthService.Instance);

            // Phase 3: hand the API server the same orchestrator + local
            // provider that `MainViewModel` consumes via
            // <see cref="TranscriptionRuntime"/>. The shared local provider
            // is the one the GUI calls `InitializeAsync(modelPath)` on, so
            // once the user opens HyperWhisper and a model loads, the API
            // server sees `IsAvailable == true` on the same instance and
            // `POST /transcribe` against a local Mode succeeds.
            LocalApiServer.Instance.Configure(
                modelLibrary,
                whisperModels,
                parakeetModels,
                localLlmModels,
                CloudProviderHealthService.Instance,
                ApiKeyService.Instance,
                TranscriptionRuntime.Orchestrator,
                TranscriptionRuntime.LocalProvider,
                TranscriptionRuntime.ParakeetProvider);

            if (SettingsService.Instance.LocalApiServerEnabled)
            {
                LocalApiServer.Instance.Start();
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"Failed to initialize Local API server: {ex.Message}");
            // Continue — Local API failures must not block app startup.
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Stop the loopback HTTP host so the discovery file gets removed and
        // the port frees up cleanly. The shared orchestrator + local provider
        // owned by TranscriptionRuntime are disposed implicitly by process exit.
        try { LocalApiServer.Instance.Shutdown(); } catch { /* best-effort on shutdown */ }

        // Release single-instance mutex
        SingleInstanceGuard.Release();

        // Clean up theme service event subscriptions
        ThemeService.Instance.Shutdown();

        // Clean up update service
        UpdateService.Shutdown();

        // Clean up auto-delete service
        AutoDeleteService.Instance.Shutdown();

        // Clean up sound effects service
        SoundEffectsService.Shutdown();

        // Mark any in-flight local-LLM GPU load as cleanly exited so a normal quit
        // mid-load is not misread as a native CUDA crash (which would needlessly
        // pin that model to CPU and emit a misattributing Sentry event) on the next
        // launch. Only a hard process death leaves the in-flight flag set.
        LocalLlmService.ClearInFlightGpuLoads();

        // Flush and shutdown Sentry to ensure all pending events are sent
        SentryService.Shutdown();

        LoggingService.Info("========== APPLICATION EXITING ==========");
        base.OnExit(e);
    }
}
