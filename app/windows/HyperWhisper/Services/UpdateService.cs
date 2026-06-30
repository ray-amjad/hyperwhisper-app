using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using HyperWhisper.Localization;
using Microsoft.Win32;
using NetSparkleUpdater;
using NetSparkleUpdater.AppCastHandlers;
using NetSparkleUpdater.Enums;
using NetSparkleUpdater.Events;
using NetSparkleUpdater.Interfaces;
using NetSparkleUpdater.SignatureVerifiers;

namespace HyperWhisper.Services;

/// <summary>
/// UPDATE SERVICE
///
/// Manages automatic software updates using NetSparkle framework.
/// Follows the same singleton pattern as SentryService.
///
/// UPDATE FLOW:
/// 1. On app startup, if CheckForUpdatesAutomatically is enabled
/// 2. Silently check appcast URL for newer version
/// 3. If update found, show WPF dialog to user
/// 4. User can download/install or skip
/// 5. App relaunches after update installation
///
/// APPCAST URL:
/// https://www.hyperwhisper.com/appcast-windows.xml
///
/// SIGNATURE VERIFICATION:
/// Uses Ed25519 signatures for security (same as macOS Sparkle)
/// Public key must be generated and embedded before release.
/// During development, SecurityMode.Unsafe is used to skip verification.
/// </summary>
public static class UpdateService
{
    // =========================================================================
    // CONSTANTS
    // =========================================================================

    /// <summary>
    /// Appcast URL for Windows builds.
    /// This XML file contains version info, download URLs, and Ed25519 signatures.
    /// </summary>
    private const string AppcastUrl = "https://www.hyperwhisper.com/appcast-windows.xml";

    /// <summary>
    /// Ed25519 public key for signature verification.
    /// Generated with: netsparkle-generate-appcast --generate-keys
    /// Private key stored securely in 1Password.
    /// </summary>
    private const string Ed25519PublicKey = "6O28FZqlSb7s8LvcCb9+B/F8GbmLsbICkPkb/UgO6ro=";

    private const string InnoUninstallKeyPath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{8F4E9A2B-3C5D-4E6F-A1B2-C3D4E5F6A7B8}_is1";

    // =========================================================================
    // STATE
    // =========================================================================

    private static SparkleUpdater? _sparkle;
    private static bool _isInitialized = false;
    private static string? _installerPath;
    private static string? _installerSignature;
    private static int _manualCheckInProgress = 0;
    private static int _updateShutdownRequested = 0;
    private static int _installerLaunched = 0;
    private static string? _launchingInstallerPath;

    public static bool IsUpdateShutdownRequested => Volatile.Read(ref _updateShutdownRequested) == 1;

    // =========================================================================
    // INITIALIZATION
    // =========================================================================

    /// <summary>
    /// Initialize the update service.
    /// Call this early in app startup (after settings are loaded).
    /// Only starts update loop if user has enabled automatic updates.
    /// </summary>
    public static void Initialize()
    {
        if (_isInitialized)
        {
            LoggingService.Debug("UpdateService: Already initialized, skipping");
            return;
        }

        try
        {
            LoggingService.Info("UpdateService: Initializing NetSparkle updater");

            // Create signature verifier
            // IMPORTANT: Before release, set Ed25519PublicKey and use SecurityMode.Strict
            var signatureVerifier = CreateSignatureVerifier();

            // Determine architecture string for appcast filtering
            // Appcast items use sparkle:os="windows-x64" or "windows-arm64"
            var archString = GetArchitectureString();
            LoggingService.Info($"UpdateService: Running on architecture: {archString}");

            // Create SparkleUpdater with appropriate signature verification
            _sparkle = new SparkleUpdater(AppcastUrl, signatureVerifier)
            {
                // Use custom themed UI factory for update dialogs
                UIFactory = new HyperWhisperUIFactory(),

                // Don't automatically relaunch - let user control timing
                RelaunchAfterUpdate = false
            };

            // Filter appcast items by architecture so users only see updates for their arch
            _sparkle.AppCastHelper.AppCastFilter = new ArchitectureAppCastFilter(archString);

            // LIFECYCLE EVENT HANDLERS
            // Log all update events for debugging
            _sparkle.UpdateDetected += OnUpdateDetected;
            _sparkle.DownloadStarted += OnDownloadStarted;
            _sparkle.DownloadFinished += OnDownloadFinished;
            _sparkle.DownloadHadError += OnDownloadError;
            _sparkle.PreparingToExit += OnPreparingToExit;

            _isInitialized = true;
            LoggingService.Info("UpdateService: Initialization complete");

            // Start background update checking if enabled
            if (SettingsService.Instance.CheckForUpdatesAutomatically)
            {
                StartBackgroundCheck();
            }
            else
            {
                LoggingService.Info("UpdateService: Automatic updates disabled by user preference");
            }
        }
        catch (Exception ex)
        {
            LoggingService.Error("UpdateService: Failed to initialize", ex);
            // Don't throw - update service failing shouldn't crash the app
        }
    }

    /// <summary>
    /// Start the background update check loop.
    /// Checks once on startup (after short delay) then periodically.
    /// </summary>
    public static void StartBackgroundCheck()
    {
        if (_sparkle == null)
        {
            LoggingService.Warn("UpdateService: Cannot start check - not initialized");
            return;
        }

        try
        {
            LoggingService.Info("UpdateService: Starting background update check");

            // StartLoop parameters:
            // - doInitialCheck: true = check immediately on startup
            // - forceInitialCheck: false = respect "skip this version" preferences
            _sparkle.StartLoop(doInitialCheck: true, forceInitialCheck: false);
        }
        catch (Exception ex)
        {
            LoggingService.Error("UpdateService: Failed to start background check", ex);
        }
    }

    /// <summary>
    /// Stop the background update check loop.
    /// Call when user disables automatic updates.
    /// </summary>
    public static void StopBackgroundCheck()
    {
        if (_sparkle == null) return;

        try
        {
            LoggingService.Info("UpdateService: Stopping background update check");
            _sparkle.StopLoop();
        }
        catch (Exception ex)
        {
            LoggingService.Error("UpdateService: Failed to stop background check", ex);
        }
    }

    /// <summary>
    /// Manually check for updates at user request.
    /// Shows NetSparkle UI dialogs for results.
    /// </summary>
    public static async Task CheckForUpdatesNow()
    {
        if (Interlocked.Exchange(ref _manualCheckInProgress, 1) == 1)
        {
            LoggingService.Info("UpdateService: Manual update check already in progress");
            return;
        }

        // GUARD CLAUSE: Check if initialized
        try
        {
            if (_sparkle == null)
            {
                LoggingService.Warn("UpdateService: Cannot check - not initialized");
                throw new InvalidOperationException("Update service not initialized");
            }

            LoggingService.Info("UpdateService: Manual update check requested by user");

            // Use the quiet status API and show result UI ourselves. The
            // NetSparkle user-request UI task can remain incomplete after the
            // no-update dialog closes, leaving About/tray controls disabled.
            var updateInfo = await _sparkle.CheckForUpdatesQuietly(ignoreSkippedVersions: false);
            ShowManualCheckResult(updateInfo);
        }
        catch (Exception ex)
        {
            LoggingService.Error("UpdateService: Manual check failed", ex);
            throw;
        }
        finally
        {
            Interlocked.Exchange(ref _manualCheckInProgress, 0);
        }
    }

    /// <summary>
    /// Shutdown the update service.
    /// Call on app exit to clean up resources.
    /// </summary>
    public static void Shutdown()
    {
        if (!_isInitialized || _sparkle == null) return;

        try
        {
            LoggingService.Debug("UpdateService: Shutting down");
            _sparkle.StopLoop();
            _sparkle.Dispose();
            _sparkle = null;
            _isInitialized = false;
            LoggingService.Debug("UpdateService: Shutdown complete");
        }
        catch (Exception ex)
        {
            LoggingService.Error("UpdateService: Error during shutdown", ex);
        }
    }

    private static void ShowManualCheckResult(UpdateInfo updateInfo)
    {
        if (_sparkle == null)
            throw new InvalidOperationException("Update service not initialized");

        var uiFactory = _sparkle.UIFactory
            ?? throw new InvalidOperationException("Update UI factory not initialized");

        switch (updateInfo.Status)
        {
            case UpdateStatus.UpdateAvailable:
                _sparkle.ShowUpdateNeededUI(updateInfo.Updates, isUpdateAlreadyDownloaded: false);
                break;
            case UpdateStatus.UpdateNotAvailable:
                uiFactory.ShowVersionIsUpToDate();
                break;
            case UpdateStatus.UserSkipped:
                uiFactory.ShowVersionIsSkippedByUserRequest();
                break;
            case UpdateStatus.CouldNotDetermine:
            default:
                throw new InvalidOperationException(Loc.S("update.error.appcast"));
        }
    }

    // =========================================================================
    // HELPERS
    // =========================================================================

    /// <summary>
    /// Get the architecture string for appcast filtering.
    /// Returns "windows-x64" or "windows-arm64" based on current process architecture.
    /// </summary>
    private static string GetArchitectureString()
    {
        return RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "windows-x64",
            Architecture.Arm64 => "windows-arm64",
            Architecture.X86 => "windows-x86",
            _ => "windows" // Fallback
        };
    }

    /// <summary>
    /// Build the Ed25519 signature verifier used both for NetSparkle's download-time
    /// check and our launch-time re-verification, so the two can never diverge.
    /// Production embeds a public key (OnlyVerifySoftwareDownloads); development with
    /// no key configured falls back to Unsafe so updates can still be tested locally.
    /// </summary>
    private static Ed25519Checker CreateSignatureVerifier()
    {
        SecurityMode securityMode;
        string publicKey;

        if (string.IsNullOrWhiteSpace(Ed25519PublicKey))
        {
            LoggingService.Warn("UpdateService: No Ed25519 public key configured - signature verification DISABLED (development mode)");
            securityMode = SecurityMode.Unsafe;
            publicKey = "placeholder"; // Ed25519Checker requires non-empty key even in Unsafe mode
        }
        else
        {
            securityMode = SecurityMode.OnlyVerifySoftwareDownloads;
            publicKey = Ed25519PublicKey;
        }

        return new Ed25519Checker(securityMode, publicKey);
    }

    // =========================================================================
    // EVENT HANDLERS
    // =========================================================================

    /// <summary>
    /// Called when an update is detected in the appcast.
    /// </summary>
    private static void OnUpdateDetected(object? sender, UpdateDetectedEventArgs e)
    {
        var version = e.LatestVersion?.Version ?? "unknown";
        LoggingService.Info($"UpdateService: Update detected - v{version}");
    }

    /// <summary>
    /// Called when update download begins.
    /// </summary>
    private static void OnDownloadStarted(AppCastItem? item, string path)
    {
        LoggingService.Info($"UpdateService: Download started - v{item?.Version ?? "unknown"} to {path}");
    }

    /// <summary>
    /// Called when update download completes successfully.
    /// Wrapped in try-catch because this fires from inside a NetSparkle callback
    /// (CreateAndShowProgressWindow → _actionToRunOnProgressWindowShown) and an
    /// unhandled exception tears down the WPF dispatcher context mid-callback,
    /// causing NullReferenceException (HYPERWHISPER-M0, 15 users).
    /// </summary>
    private static void OnDownloadFinished(AppCastItem? item, string path)
    {
        try
        {
            LoggingService.Info($"UpdateService: Download completed - v{item?.Version ?? "unknown"}");

            // A manual check and the background loop can both finish a download and
            // arrive here. Only the first completed download owns the pending launch;
            // later callbacks must not overwrite its path/signature or delete the same
            // temp installer file while the installer is being verified and launched.
            var activeInstallerPath = Interlocked.CompareExchange(ref _launchingInstallerPath, path, null);
            if (activeInstallerPath != null)
            {
                LoggingService.Warn($"UpdateService: Installer launch already in progress, ignoring duplicate download: {path}");
                if (!IsSameInstallerPath(path, activeInstallerPath))
                    TryDeleteInstaller(path);
                return;
            }

            // Store installer path and the expected Ed25519 signature from the
            // (already-verified) appcast item. The signature is re-checked against
            // the on-disk file immediately before launch to close the TOCTOU window
            // where a same-user process could swap the installer in %TEMP%.
            _installerPath = path;
            _installerSignature = item?.DownloadSignature;

            // Launch installer and exit
            LaunchInstallerAndExit();
        }
        catch (Exception ex)
        {
            LoggingService.Error("UpdateService: OnDownloadFinished failed", ex);
            SentryService.Capture(ex, "OnDownloadFinished failed");
        }
    }

    /// <summary>
    /// Shuts the application down gracefully, then launches the downloaded installer.
    ///
    /// ORDER MATTERS: the installer runs with /FORCECLOSEAPPLICATIONS. If it starts
    /// before the app exits, the installer can force-kill HyperWhisper.exe before
    /// App.OnExit flushes Sentry and persistent state.
    /// </summary>
    private static void LaunchInstallerAndExit()
    {
        // GUARD CLAUSE: Validate installer path
        if (string.IsNullOrEmpty(_installerPath))
        {
            LoggingService.Error("UpdateService: Cannot launch installer - path is null");
            ResetInstallerLaunchState();
            return;
        }

        if (!File.Exists(_installerPath))
        {
            LoggingService.Error($"UpdateService: Cannot launch installer - file not found: {_installerPath}");
            ResetInstallerLaunchState();
            return;
        }

        var app = System.Windows.Application.Current;
        var dispatcher = app?.Dispatcher;

        // No WPF application means there is no orderly app exit to wait for.
        if (app == null || dispatcher == null)
        {
            StartInstallerProcess();
            return;
        }

        app.Exit += OnApplicationExitForUpdate;
        Interlocked.Exchange(ref _updateShutdownRequested, 1);

        if (dispatcher.HasShutdownStarted)
        {
            LoggingService.Info("UpdateService: Shutdown already in progress; installer will launch after application exit");
            return;
        }

        // Exit app to allow installer to replace files
        LoggingService.Info("UpdateService: Shutting down for update installation");

        // Shutdown on UI thread — use BeginInvoke (async post) instead of Invoke
        // so the NetSparkle callback stack can unwind before we tear down WPF.
        // Invoke caused NullReferenceException (HYPERWHISPER-M0) because Shutdown()
        // destroyed the progress window while CreateAndShowProgressWindow was still
        // on the call stack.
        dispatcher.BeginInvoke(() =>
        {
            System.Windows.Application.Current?.Shutdown();
        });
    }

    /// <summary>
    /// Runs after App.OnExit has finished cleanup. Safe to start the installer now
    /// because persistent state and Sentry have been flushed.
    /// </summary>
    private static void OnApplicationExitForUpdate(object? sender, ExitEventArgs e)
    {
        if (sender is System.Windows.Application app)
        {
            app.Exit -= OnApplicationExitForUpdate;
        }

        StartInstallerProcess();
    }

    /// <summary>
    /// Spawns the downloaded installer with Inno Setup silent flags.
    /// </summary>
    private static void StartInstallerProcess()
    {
        if (Interlocked.Exchange(ref _installerLaunched, 1) == 1)
        {
            return;
        }

        if (string.IsNullOrEmpty(_installerPath))
        {
            LoggingService.Error("UpdateService: Cannot launch installer - path is null");
            ResetInstallerLaunchState();
            return;
        }

        if (!File.Exists(_installerPath))
        {
            LoggingService.Error($"UpdateService: Cannot launch installer - file not found: {_installerPath}");
            ResetInstallerLaunchState();
            return;
        }

        // SECURITY: Close the TOCTOU window completely by holding an exclusive
        // lock on the installer across verification AND launch. The installer sits
        // in user-writable %TEMP% from download until launch, so a same-user process
        // could swap it after a plain re-verify returns but before Process.Start.
        //
        // We open the file denying writes and DELETES (FileShare.Read only) so no
        // other process can replace, rename, truncate or delete it while our handle
        // is open. We then verify the signature against the on-disk bytes (which are
        // now immutable under our lock) and keep the handle open until the installer
        // process has started — the launched Inno Setup process only needs read+execute
        // access, which FileShare.Read permits. This guarantees the bytes the installer
        // executes are the exact bytes we verified.
        FileStream? lockStream = null;
        var installerStarted = false;
        try
        {
            try
            {
                lockStream = new FileStream(
                    _installerPath,
                    FileMode.Open,
                    FileAccess.Read,
                    // Deny write + delete from other processes; allow read so the
                    // launched installer (and the signature verifier) can read it.
                    FileShare.Read);
            }
            catch (Exception ex)
            {
                LoggingService.Error("UpdateService: Could not obtain exclusive lock on installer before launch - refusing to launch", ex);
                SentryService.Capture(ex, "Installer lock acquisition failed");
                ResetInstallerLaunchState();
                return;
            }

            if (!ReVerifyInstallerSignature())
            {
                ResetInstallerLaunchState();
                return;
            }

            // Detect whether the running app was installed per-machine (Program Files)
            // or per-user (%LOCALAPPDATA%\Programs). This drives both elevation and the
            // Inno scope flag so a silent update preserves the original install location.
            var isPerMachine = IsPerMachineInstall();

            LoggingService.Info(
                $"UpdateService: Launching installer ({(isPerMachine ? "per-machine" : "per-user")}): {_installerPath}");

            // Launch installer with Inno Setup silent flags.
            //
            // Common flags:
            // /VERYSILENT             = No UI during install
            // /FORCECLOSEAPPLICATIONS = Force-close running HyperWhisper.exe (kills if needed)
            // Note: actual relaunch is handled by [Run] skipifnotsilent entry in .iss
            //
            // Scope flags (enabled because PrivilegesRequiredOverridesAllowed=dialog
            // implicitly allows command-line overrides):
            // /ALLUSERS    = Force all-users (per-machine) install. Without this,
            //                /VERYSILENT suppresses the scope dialog and Inno falls
            //                back to PrivilegesRequired=lowest, silently duplicating
            //                the app into %LOCALAPPDATA% instead of Program Files.
            // /CURRENTUSER = Force per-user install (keeps the existing scope explicit).
            //
            // A per-machine install must run elevated; Inno's manifest is asInvoker,
            // so ShellExecute does NOT auto-elevate. Verb="runas" requests UAC.
            var startInfo = new ProcessStartInfo
            {
                FileName = _installerPath,
                UseShellExecute = true,
                Arguments = isPerMachine
                    ? "/VERYSILENT /FORCECLOSEAPPLICATIONS /ALLUSERS"
                    : "/VERYSILENT /FORCECLOSEAPPLICATIONS /CURRENTUSER",
                Verb = isPerMachine ? "runas" : string.Empty
            };

            // The lock is still held here: the file an attacker would need to swap
            // cannot be replaced between the verify above and this launch.
            Process.Start(startInfo);
            installerStarted = true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED (1223): user declined the UAC elevation prompt for a
            // per-machine update. Leave the installer on disk and allow retry if
            // this process is still running. Not a bug — don't report to Sentry.
            LoggingService.Warn(
                "UpdateService: User cancelled UAC elevation; per-machine update not installed");
        }
        catch (Exception ex)
        {
            LoggingService.Error("UpdateService: Failed to launch installer", ex);
            SentryService.Capture(ex, "Failed to launch installer");

            // Reset only if the installer was not actually spawned. Once Process.Start
            // succeeds, keep the guard closed so later shutdown/logging failures cannot
            // let a duplicate callback launch a second installer.
            if (!installerStarted)
                ResetInstallerLaunchState();
        }
        finally
        {
            // Release the lock only after the installer process has been started
            // (or on failure). The launched installer keeps its own read+execute
            // handle, so closing ours here does not interrupt it.
            lockStream?.Dispose();
        }
    }

    private static void ResetInstallerLaunchState()
    {
        _installerPath = null;
        _installerSignature = null;
        Interlocked.Exchange(ref _installerLaunched, 0);
        Interlocked.Exchange(ref _launchingInstallerPath, null);
    }

    private static bool IsSameInstallerPath(string path, string activeInstallerPath)
    {
        try
        {
            path = Path.GetFullPath(path);
            activeInstallerPath = Path.GetFullPath(activeInstallerPath);
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"UpdateService: Failed to normalize installer paths before duplicate cleanup: {ex.Message}");
        }

        return string.Equals(path, activeInstallerPath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Best-effort deletion of a downloaded installer file. Used to clean up a duplicate
    /// download once another download has already started launching the installer.
    /// </summary>
    private static void TryDeleteInstaller(string path)
    {
        if (string.IsNullOrEmpty(path))
            return;

        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"UpdateService: Failed to delete duplicate installer '{path}': {ex.Message}");
        }
    }

    /// <summary>
    /// Re-verifies the downloaded installer's Ed25519 signature against the on-disk
    /// file immediately before launch (TOCTOU protection — see callers). Callers must
    /// already hold an exclusive (write/delete-denying) handle on the file so the bytes
    /// verified here cannot be swapped before the installer is launched.
    /// Returns true if the file is safe to launch.
    ///
    /// Mirrors NetSparkle's own security mode: when a public key is embedded
    /// (production), a tampered file yields <see cref="ValidationResult.Invalid"/>
    /// and is rejected. When no key is configured (development) the verifier returns
    /// <see cref="ValidationResult.Unchecked"/>, matching the download-time behavior,
    /// so local update testing is not broken.
    /// </summary>
    private static bool ReVerifyInstallerSignature()
    {
        var installerPath = _installerPath;
        if (string.IsNullOrEmpty(installerPath))
        {
            return false;
        }

        try
        {
            var verifier = CreateSignatureVerifier();
            var result = verifier.VerifySignatureOfFile(_installerSignature ?? string.Empty, installerPath);

            if (result == ValidationResult.Invalid)
            {
                LoggingService.Error("UpdateService: Installer signature failed re-verification before launch - refusing to launch (possible tampering)");
                SentryService.Capture(
                    new InvalidOperationException("Installer Ed25519 signature re-verification failed before launch"),
                    "Installer TOCTOU re-verification failed");
                return false;
            }

            if (result == ValidationResult.Unchecked)
            {
                LoggingService.Warn("UpdateService: Installer signature not re-verified (no public key/signature) - launching unverified");
            }

            return true;
        }
        catch (Exception ex)
        {
            // Treat an unexpected verification failure as unsafe rather than launching blindly.
            LoggingService.Error("UpdateService: Installer signature re-verification threw - refusing to launch", ex);
            SentryService.Capture(ex, "Installer re-verification threw");
            return false;
        }
    }

    /// <summary>
    /// Determines whether the running app was installed per-machine (HKLM) versus
    /// per-user (HKCU). Registry scope is authoritative because users can choose a
    /// custom per-user destination outside %LOCALAPPDATA% during interactive setup.
    /// Falls back to the executable directory only when the installer registry entry
    /// is missing or ambiguous.
    /// </summary>
    private static bool IsPerMachineInstall()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
            {
                LoggingService.Warn(
                    "UpdateService: Could not resolve executable path; assuming per-machine install");
                return true;
            }

            if (TryGetPerMachineInstallFromRegistry(exePath, out var isPerMachine))
                return isPerMachine;

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(localAppData))
                return true;

            // Fallback for missing registry entries: default per-user installs live
            // under %LOCALAPPDATA%; everything else should request elevation.
            return !exePath.StartsWith(localAppData, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            LoggingService.Warn(
                $"UpdateService: Failed to detect install scope, assuming per-machine: {ex.Message}");
            return true;
        }
    }

    private static bool TryGetPerMachineInstallFromRegistry(string exePath, out bool isPerMachine)
    {
        isPerMachine = true;

        var currentUserMatches = TryGetInstallDirectory(
            RegistryHive.CurrentUser,
            out var currentUserInstallDirectory)
            && IsExecutableUnderDirectory(exePath, currentUserInstallDirectory);

        if (currentUserMatches)
        {
            LoggingService.Debug("UpdateService: Install scope detected from HKCU uninstall entry");
            isPerMachine = false;
            return true;
        }

        var localMachineMatches = TryGetInstallDirectory(
            RegistryHive.LocalMachine,
            out var localMachineInstallDirectory)
            && IsExecutableUnderDirectory(exePath, localMachineInstallDirectory);

        if (localMachineMatches)
        {
            LoggingService.Debug("UpdateService: Install scope detected from HKLM uninstall entry");
            isPerMachine = true;
            return true;
        }

        return false;
    }

    private static bool TryGetInstallDirectory(RegistryHive hive, out string installDirectory)
    {
        installDirectory = string.Empty;

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
            using var uninstallKey = baseKey.OpenSubKey(InnoUninstallKeyPath);
            if (uninstallKey == null)
                return false;

            installDirectory =
                ReadRegistryString(uninstallKey, "InstallLocation")
                ?? ReadRegistryString(uninstallKey, "Inno Setup: App Path")
                ?? GetDirectoryFromDisplayIcon(ReadRegistryString(uninstallKey, "DisplayIcon"))
                ?? string.Empty;

            return !string.IsNullOrWhiteSpace(installDirectory);
        }
        catch (Exception ex)
        {
            LoggingService.Warn(
                $"UpdateService: Failed to read {hive} uninstall entry: {ex.Message}");
            return false;
        }
    }

    private static string? ReadRegistryString(RegistryKey key, string valueName)
    {
        var value = key.GetValue(valueName) as string;
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string? GetDirectoryFromDisplayIcon(string? displayIcon)
    {
        if (string.IsNullOrWhiteSpace(displayIcon))
            return null;

        var iconPath = displayIcon.Trim().Trim('"');
        var commaIndex = iconPath.LastIndexOf(',');
        if (commaIndex > 0)
            iconPath = iconPath[..commaIndex].Trim().Trim('"');

        return Path.GetDirectoryName(iconPath);
    }

    private static bool IsExecutableUnderDirectory(string exePath, string installDirectory)
    {
        try
        {
            var normalizedExePath = Path.GetFullPath(exePath);
            var normalizedInstallDirectory = Path
                .GetFullPath(installDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;

            return normalizedExePath.StartsWith(
                normalizedInstallDirectory,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            LoggingService.Warn(
                $"UpdateService: Failed to compare install paths: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Called when update download fails.
    /// </summary>
    private static void OnDownloadError(AppCastItem? item, string? path, Exception ex)
    {
        LoggingService.Error($"UpdateService: Download failed - v{item?.Version ?? "unknown"}", ex);
    }

    /// <summary>
    /// Called when app is about to exit for update installation.
    /// Allows app to save state before exit.
    /// </summary>
    private static void OnPreparingToExit(object? sender, CancelEventArgs e)
    {
        LoggingService.Info("UpdateService: Preparing to exit for update installation");
        // Allow app to save state before exit
        // Do NOT cancel (e.Cancel = true) or update won't install
    }

    // =========================================================================
    // APPCAST FILTER
    // =========================================================================

    /// <summary>
    /// Filters appcast items by CPU architecture so users only see updates
    /// matching their platform (e.g. windows-x64 vs windows-arm64).
    ///
    /// Also filters out versions &lt;= installed, because AppCastHelper skips
    /// its own version filtering when an IAppCastFilter is set.
    /// </summary>
    private sealed class ArchitectureAppCastFilter : IAppCastFilter
    {
        private readonly string _arch; // e.g. "windows-x64"

        public ArchitectureAppCastFilter(string arch)
        {
            _arch = arch;
        }

        public IEnumerable<AppCastItem> GetFilteredAppCastItems(SemVerLike installed, IEnumerable<AppCastItem> items)
        {
            return items
                .Where(item =>
                {
                    var os = item.OperatingSystem;

                    // Keep items with no OS set (universal)
                    if (string.IsNullOrEmpty(os))
                        return true;

                    // If OS contains a hyphen (e.g. "windows-x64"), require exact match
                    if (os.Contains('-'))
                        return string.Equals(os, _arch, StringComparison.OrdinalIgnoreCase);

                    // Generic "windows" (no hyphen) = universal fallback, keep it
                    return true;
                })
                .Where(item =>
                {
                    // Filter out versions <= installed
                    var itemVersion = SemVerLike.Parse(item.Version);
                    return itemVersion.CompareTo(installed) > 0;
                })
                .OrderByDescending(item => SemVerLike.Parse(item.Version), Comparer<SemVerLike>.Create(
                    (a, b) => a.CompareTo(b)))
                .ToList();
        }
    }
}
