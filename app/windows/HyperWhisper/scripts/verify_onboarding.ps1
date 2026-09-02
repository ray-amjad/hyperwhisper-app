# verify_onboarding.ps1
#
# Reusable gate for the Windows onboarding flow. The port of
# app/macos/scripts/verify-onboarding.sh, in the shape the 25 other verify_*.ps1
# scripts in this folder use.
#
#   1. Repo hygiene: no client-side entitlement bypass, no command-line trigger.
#   2. Static design conformance over the onboarding XAML.
#   3. Localization: every referenced key exists, all 40 .resx agree, no em dash.
#   4. Trigger and lifecycle wiring (Windows only; macOS has no equivalent).
#   5. Builds the head and runs the smoke suite.
#
# Behavioural regression guards are deliberately NOT pattern matches here. They
# live in HyperWhisper.SmokeTests, which asserts on behaviour, and section 5 is
# what runs them.
#
# NOTE: none of the scripts in this folder are wired into CI, so every check that
# has to hold on every PR also exists as a smoke case.
#
# Exit codes:
#   0   every hard check passed and the build + smoke suite passed
#   1   a hard check failed, or the build / smoke suite failed
#   2   the static checks passed but the build or the suite was skipped
#
# Usage:
#   pwsh app\windows\HyperWhisper\scripts\verify_onboarding.ps1
#   pwsh app\windows\HyperWhisper\scripts\verify_onboarding.ps1 -StaticOnly

param(
    [switch] $StaticOnly,
    [switch] $NoTests
)

$ErrorActionPreference = "Stop"

$ProjectRoot = Split-Path -Parent $PSScriptRoot
$WindowsRoot = Split-Path -Parent $ProjectRoot
$RepoRoot = Resolve-Path (Join-Path $ProjectRoot "..\..\..")

$ProjectFile = Join-Path $ProjectRoot "HyperWhisper.csproj"
$SmokeProject = Join-Path $WindowsRoot "HyperWhisper.SmokeTests\HyperWhisper.SmokeTests.csproj"
$ResourcesDir = Join-Path $ProjectRoot "Resources"
$BaseResx = Join-Path $ResourcesDir "Strings.resx"

$AppPath = Join-Path $ProjectRoot "App.xaml.cs"
$MainWindowPath = Join-Path $ProjectRoot "Views\Windows\MainWindow.xaml.cs"
$MainViewModelPath = Join-Path $ProjectRoot "ViewModels\MainViewModel.cs"
$OnboardingWindowXaml = Join-Path $ProjectRoot "Views\Windows\OnboardingWindow.xaml"
$OnboardingWindowCode = Join-Path $ProjectRoot "Views\Windows\OnboardingWindow.xaml.cs"
$LaunchPolicyPath = Join-Path $ProjectRoot "Services\Onboarding\OnboardingLaunchPolicy.cs"
$SettingsServicePath = Join-Path $ProjectRoot "Services\SettingsService.cs"
$SmokeProgramPath = Join-Path $WindowsRoot "HyperWhisper.SmokeTests\Program.cs"

$StepPagesDir = Join-Path $ProjectRoot "Views\Pages\Onboarding"
$ControlsDir = Join-Path $ProjectRoot "Views\Controls\Onboarding"
$OnboardingResourcesXaml = Join-Path $ControlsDir "OnboardingResources.xaml"
$FlowDir = Join-Path $ProjectRoot "ViewModels\Onboarding"
$AdaptersDir = Join-Path $ProjectRoot "Services\Onboarding"

$EmDash = [string][char]0x2014

# The eight steps, in order. Load-bearing: Advance() is step+1 and 8 is the
# progress segment count, so a step added or dropped is a flow change.
$Steps = @("Welcome", "Permissions", "Source", "Configure", "Setup", "Microphone", "TryIt", "Done")

# CornerRadius literals allowed in onboarding XAML. 9/7/8 are the design tokens
# (CardCornerRadius / ButtonCornerRadius / InputCornerRadius in Themes\Generic.xaml);
# 0 is a deliberate square edge; 3 is a fully rounded 6px progress track, the same
# literal UpdateDownloadProgressWindow.xaml:54 already uses for the same shape.
$AllowedCornerRadii = @("0", "3", "7", "8", "9")

# ---------------------------------------------------------------------------
# Reporting
# ---------------------------------------------------------------------------

$script:HardFails = 0
$script:Warns = 0
$script:Passes = 0
$script:Blocked = $false
$script:FailLines = New-Object System.Collections.Generic.List[string]

function Write-Pass { param([string] $Message) $script:Passes++; Write-Host "[PASS] $Message" }
function Write-Fail {
    param([string] $Message)
    $script:HardFails++
    $script:FailLines.Add($Message) | Out-Null
    Write-Host "[FAIL] $Message"
}
function Write-Warn { param([string] $Message) $script:Warns++; Write-Host "[WARN] $Message" }
function Write-Info { param([string] $Message) Write-Host "[INFO] $Message" }
function Write-Section { param([string] $Title) Write-Host ""; Write-Host "== $Title ==" }
function Write-Detail { param([string] $Message) Write-Host "        $Message" }

function Show-Hits {
    param([object[]] $Hits, [int] $Limit = 8)

    $shown = 0
    foreach ($hit in $Hits) {
        if ($shown -ge $Limit) { Write-Detail "... (more suppressed)"; break }
        $text = "$hit"
        if ($text.Length -gt 200) { $text = $text.Substring(0, 200) }
        Write-Detail $text
        $shown++
    }
}

# Get-Content -Raw returns $null for a zero-byte file, and [regex]::Matches
# throws ArgumentNullException on it. Every read in this script goes through here.
function Read-Text {
    param([string] $Path)

    $text = Get-Content -LiteralPath $Path -Raw -ErrorAction SilentlyContinue
    if ($null -eq $text) { return "" }
    return $text
}

function Get-RelativePath {
    param([string] $Path)
    return $Path.Replace("$RepoRoot", "").TrimStart([char[]]@('\', '/'))
}

function Assert-Wired {
    param([string] $Text, [string] $Needle, [string] $Message)

    if ($Text.Contains($Needle)) { Write-Pass $Message } else { Write-Fail "$Message (missing: $Needle)" }
}

# ---------------------------------------------------------------------------
# File sets
# ---------------------------------------------------------------------------

function Get-FilesUnder {
    param([string] $Directory, [string] $Filter)

    if (-not (Test-Path -LiteralPath $Directory)) { return @() }
    return @(Get-ChildItem -LiteralPath $Directory -Filter $Filter -File -Recurse)
}

# The design surface: everything that draws the flow.
$DesignXaml = @()
$DesignXaml += Get-FilesUnder $StepPagesDir "*.xaml"
$DesignXaml += Get-FilesUnder $ControlsDir "*.xaml"
if (Test-Path -LiteralPath $OnboardingWindowXaml) { $DesignXaml += Get-Item -LiteralPath $OnboardingWindowXaml }

# The wider onboarding surface: everything that decides anything about the flow.
$OnboardingCs = @()
$OnboardingCs += Get-FilesUnder $FlowDir "*.cs"
$OnboardingCs += Get-FilesUnder $AdaptersDir "*.cs"
$OnboardingCs += Get-FilesUnder $StepPagesDir "*.cs"
$OnboardingCs += Get-FilesUnder $ControlsDir "*.cs"
if (Test-Path -LiteralPath $OnboardingWindowCode) { $OnboardingCs += Get-Item -LiteralPath $OnboardingWindowCode }

Write-Host "HyperWhisper Windows onboarding gate"
Write-Info "repo:          $RepoRoot"
Write-Info "design files:  $($DesignXaml.Count) XAML (window + steps + controls)"
Write-Info "onboarding cs: $($OnboardingCs.Count) files scanned"

if ($DesignXaml.Count -eq 0) { Write-Fail "no onboarding XAML found under $(Get-RelativePath $StepPagesDir)" }
if ($OnboardingCs.Count -eq 0) { Write-Fail "no onboarding C# found under $(Get-RelativePath $FlowDir)" }

# ===========================================================================
Write-Section "1. Repo hygiene"
# ===========================================================================

# --- no client-side entitlement bypass -------------------------------------
# Paid moat: the HyperWhisper Cloud entitlement is enforced server side. A debug
# backdoor or a fake license key introduced during onboarding would hand the
# product away, and no unit test would catch it, so it is grepped for here.
$bypassPattern = "bypassLicense|skipLicenseCheck|fakeLicense|testLicenseKey|debugEntitlement|forceEntitled|HYPERWHISPER_DEBUG_LICENSE"
$bypassHits = @($DesignXaml + $OnboardingCs | Select-String -Pattern $bypassPattern)
if ($bypassHits.Count -gt 0) {
    Write-Fail "possible client-side entitlement bypass in onboarding (Cloud entitlement is enforced server side)"
    Show-Hits $bypassHits 6
} else {
    Write-Pass "no client-side entitlement bypass in the onboarding sources"
}

# --- no command-line trigger -----------------------------------------------
# SingleInstanceGuard.TryAcquire() runs as App.OnStartup's first statement and
# kills the second instance before e.Args is ever inspected, so an --onboarding
# switch would silently do nothing whenever the app was already running. The
# supported levers are the tray entry, HYPERWHISPER_WINDOWS_APPDATA_ROOT, and
# hand-editing settings.json with the app closed.
$switchHits = @(Get-ChildItem -LiteralPath $ProjectRoot -Filter "*.cs" -File -Recurse |
    Select-String -Pattern '"--onboarding|--onboarding"|--run-onboarding|--show-onboarding')
if ($switchHits.Count -gt 0) {
    Write-Fail "an onboarding command-line switch was added; SingleInstanceGuard consumes e.Args before it can be read"
    Show-Hits $switchHits 6
} else {
    Write-Pass "onboarding has no command-line switch (the trigger is a settings flag plus one env var)"
}

# --- the first-run flag stays out of the backup snapshot -------------------
# OnboardingPending is machine-local install state, the same class of thing as
# GettingStartedCompletedSteps and LocalApiServerPersistedPort. Restoring a
# backup must not re-run setup, or re-suppress it, on a different machine.
if (Test-Path -LiteralPath $SettingsServicePath) {
    $settingsSource = Read-Text $SettingsServicePath
    $snapshotMatch = [regex]::Match($settingsSource, "BuildBackupSettingsSnapshot\s*\(")
    if (-not $snapshotMatch.Success) {
        Write-Warn "BuildBackupSettingsSnapshot not found in SettingsService.cs; the backup exclusion is unverified"
    } else {
        $tail = $settingsSource.Substring($snapshotMatch.Index)
        $braceStart = $tail.IndexOf("{")
        $depth = 0
        $body = $tail
        for ($i = $braceStart; $i -lt $tail.Length; $i++) {
            if ($tail[$i] -eq "{") { $depth++ }
            elseif ($tail[$i] -eq "}") {
                $depth--
                if ($depth -eq 0) { $body = $tail.Substring($braceStart, $i - $braceStart + 1); break }
            }
        }
        if ($body.Contains("OnboardingPending")) {
            Write-Fail "OnboardingPending is written into BuildBackupSettingsSnapshot(); first-run state must not travel in a .hwbackup.json"
        } else {
            Write-Pass "OnboardingPending stays out of BuildBackupSettingsSnapshot()"
        }
    }
} else {
    Write-Fail "SettingsService.cs not found at $(Get-RelativePath $SettingsServicePath)"
}

# ===========================================================================
Write-Section "2. Static design conformance"
# ===========================================================================

# --- no hardcoded colour literals ------------------------------------------
$colorHits = @($DesignXaml | Select-String -Pattern '"#[0-9A-Fa-f]{3,8}"')
if ($colorHits.Count -gt 0) {
    Write-Fail "hardcoded colour literal in onboarding XAML (semantic brushes only; it has to be correct in light and dark)"
    Show-Hits $colorHits 10
} else {
    Write-Pass "no hardcoded colour literals in the onboarding XAML"
}

# --- brushes are DynamicResource -------------------------------------------
# HARD rule, .ui-consistency-sweep.md:65-68. ThemeService swaps the *colors*
# dictionary at runtime, so a StaticResource brush freezes in the launch theme
# and the flow half-changes colour when the user flips the theme mid-setup.
$staticBrushHits = @($DesignXaml | Select-String -Pattern '\{StaticResource\s+[A-Za-z0-9]*Brush\}')
if ($staticBrushHits.Count -gt 0) {
    Write-Fail "a brush is bound with StaticResource in onboarding XAML (ThemeService swaps colors at runtime; use DynamicResource)"
    Show-Hits $staticBrushHits 10
} else {
    Write-Pass "every brush in the onboarding XAML is a DynamicResource"
}

# --- corner radius scale ---------------------------------------------------
$radiusHits = @()
foreach ($hit in @($DesignXaml | Select-String -Pattern 'CornerRadius="([0-9]+)"' -AllMatches)) {
    foreach ($match in $hit.Matches) {
        if ($AllowedCornerRadii -notcontains $match.Groups[1].Value) {
            $radiusHits += "$($hit.Path):$($hit.LineNumber): $($match.Value)"
        }
    }
}
if ($radiusHits.Count -gt 0) {
    Write-Fail "CornerRadius literal off the $($AllowedCornerRadii -join '/') scale (use CardCornerRadius / ButtonCornerRadius / InputCornerRadius)"
    Show-Hits $radiusHits 10
} else {
    Write-Pass "every CornerRadius literal in the onboarding XAML is on the $($AllowedCornerRadii -join '/') scale"
}

# --- no control takes its height from its container ------------------------
# Ray's own complaint, from watching a recording of the flow: "the back button
# and the continue button are way too tall compared to a Windows application."
#
# WPF's default VerticalAlignment is Stretch. The footer band is a fixed 56 row,
# so three buttons with no alignment rendered 56 tall against the ~31 the same
# styles render on every Settings page. Nothing about that is visible in a build
# or in a binding, and it is one attribute away from coming back.
#
# The behavioural half of this lives in HyperWhisper.SmokeTests, which MEASURES
# the rendered heights against a real Settings page button. This half is the
# cheap one: every interactive control in the onboarding XAML has to say, at its
# call site or through a keyed style, what decides its height.
$stretchHits = @()
foreach ($file in $DesignXaml) {
    $text = Read-Text $file.FullName
    foreach ($match in [regex]::Matches($text, '<(Button|ComboBox|TextBox|PasswordBox)\b[^>]*>')) {
        $tag = $match.Value
        if ($tag -match 'Style\s*=' -or $tag -match 'VerticalAlignment\s*=' -or $tag -match 'Height\s*=') { continue }
        $line = ($text.Substring(0, $match.Index) -split "`n").Count
        $stretchHits += "$(Get-RelativePath $file.FullName):$($line): <$($match.Groups[1].Value) ...>"
    }
}
if ($stretchHits.Count -gt 0) {
    Write-Fail "an onboarding Button/ComboBox/TextBox/PasswordBox has no Style, VerticalAlignment or Height, so its container decides how tall it is"
    Show-Hits $stretchHits 10
} else {
    Write-Pass "every interactive control in the onboarding XAML decides its own height"
}

# --- the shared button styles carry the alignment --------------------------
# The remedy above is only real if the styles the call sites point at actually
# set it. Every Button style in OnboardingResources.xaml must declare a
# VerticalAlignment, INCLUDING the selectable row and card styles that
# deliberately stretch: an exception that is written down is a decision, and an
# exception that is inherited from a WPF default is the bug this check exists
# for.
if (Test-Path -LiteralPath $OnboardingResourcesXaml) {
    $resourcesXaml = Read-Text $OnboardingResourcesXaml

    # key -> whether it sets VerticalAlignment itself, and what it is BasedOn.
    # A style may inherit the setter, so the answer is a walk and not a match.
    $buttonStyles = @{}
    foreach ($match in [regex]::Matches($resourcesXaml, '(?s)<Style\s+x:Key="(?<key>[^"]+)"[^>]*TargetType="Button"[^>]*>(?<body>.*?)</Style>')) {
        $header = $match.Value.Substring(0, $match.Value.IndexOf(">") + 1)
        $basedOn = $null
        $basedOnMatch = [regex]::Match($header, 'BasedOn="\{StaticResource\s+(?<base>[^}]+)\}"')
        if ($basedOnMatch.Success) { $basedOn = $basedOnMatch.Groups["base"].Value.Trim() }

        $buttonStyles[$match.Groups["key"].Value] = [pscustomobject]@{
            SetsAlignment = $match.Groups["body"].Value -match 'Property="VerticalAlignment"'
            BasedOn       = $basedOn
        }
    }

    $unalignedStyles = @()
    foreach ($key in $buttonStyles.Keys) {
        $cursor = $key
        $resolved = $false
        # Bounded so a BasedOn cycle cannot hang the gate.
        for ($hop = 0; $hop -lt 8 -and $cursor -and $buttonStyles.ContainsKey($cursor); $hop++) {
            if ($buttonStyles[$cursor].SetsAlignment) { $resolved = $true; break }
            $cursor = $buttonStyles[$cursor].BasedOn
        }
        if (-not $resolved) { $unalignedStyles += $key }
    }

    if ($buttonStyles.Count -eq 0) {
        Write-Fail "no Button styles found in OnboardingResources.xaml; the alignment check is not running"
    } elseif ($unalignedStyles.Count -gt 0) {
        Write-Fail "onboarding Button style(s) that neither set nor inherit a VerticalAlignment: $(($unalignedStyles | Sort-Object) -join ', ')"
    } else {
        Write-Pass "all $($buttonStyles.Count) Button styles in OnboardingResources.xaml settle their own VerticalAlignment"
    }
} else {
    Write-Fail "OnboardingResources.xaml not found at $(Get-RelativePath $OnboardingResourcesXaml)"
}

# --- the window is the macOS stage plus the caption row --------------------
# 760 x 624 = macOS's 760 x 580 sheet plus the 44px WPF caption row, so the
# stage itself matches the original exactly.
if (Test-Path -LiteralPath $OnboardingWindowXaml) {
    $windowXaml = Read-Text $OnboardingWindowXaml
    if ($windowXaml -match 'Width="760"' -and $windowXaml -match 'Height="624"') {
        Write-Pass "OnboardingWindow is 760 x 624 (the 760 x 580 macOS stage plus the 44px caption row)"
    } else {
        Write-Fail "OnboardingWindow is not 760 x 624"
    }

    if ($windowXaml -match 'ResizeMode="NoResize"') {
        Write-Pass "OnboardingWindow is non-resizable, like the macOS sheet"
    } else {
        Write-Fail "OnboardingWindow is resizable; the steps are laid out against a fixed stage"
    }

    # 760 x 624 is the DESIGN size, not a guarantee. It has to be clamped to the
    # monitor's work area, because the window is NoResize with a custom caption: on a
    # 1366x768 laptop at 150% the designed height is a third taller than the whole
    # work area, and the footer - Continue included - lands below the bottom edge
    # with no way to drag, keyboard or maximize it back.
    if ($windowXaml -match 'MaxWidth="760"' -or $windowXaml -match 'MaxHeight="624"') {
        Write-Fail "OnboardingWindow re-pins MaxWidth/MaxHeight to the design size; that is what stops the work-area clamp shrinking it on a small screen"
    } else {
        Write-Pass "OnboardingWindow does not pin its Max size, so the work-area clamp can shrink it"
    }

    if (Test-Path -LiteralPath $OnboardingWindowCode) {
        $sizeCode = Read-Text $OnboardingWindowCode
        $clampBits = @(
            @{ Pattern = 'ClampToWorkArea'; What = "a clamp method" },
            @{ Pattern = 'FitToWorkArea';   What = "the pure sizing policy the smoke suite pins" },
            @{ Pattern = 'Screen\.FromHandle'; What = "the window's OWN monitor (SystemParameters.WorkArea is the primary display only)" },
            @{ Pattern = 'DpiChanged';      What = "a re-clamp when the scale changes mid-flow" }
        )
        $missing = @($clampBits | Where-Object { $sizeCode -notmatch $_.Pattern } | ForEach-Object { $_.What })
        if ($missing.Count -gt 0) {
            Write-Fail "OnboardingWindow.xaml.cs is missing: $($missing -join '; ')"
        } else {
            Write-Pass "OnboardingWindow clamps itself to its own monitor's work area, and re-clamps on a DPI change"
        }
    }
} else {
    Write-Fail "OnboardingWindow.xaml not found at $(Get-RelativePath $OnboardingWindowXaml)"
}

# --- all 8 steps present, rendered, and reachable --------------------------
$stepEnumPath = Join-Path $FlowDir "OnboardingStep.cs"
if (Test-Path -LiteralPath $stepEnumPath) {
    $enumSource = Read-Text $stepEnumPath
    $enumMatch = [regex]::Match($enumSource, "enum\s+OnboardingStep\s*\{(?<body>[^}]*)\}")
    if (-not $enumMatch.Success) {
        Write-Fail "no 'enum OnboardingStep' declaration found in $(Get-RelativePath $stepEnumPath)"
    } else {
        $cases = @([regex]::Matches($enumMatch.Groups["body"].Value, "(?m)^\s*([A-Za-z]+)\s*=\s*[0-9]+") |
            ForEach-Object { $_.Groups[1].Value })
        if ($cases.Count -ne 8) {
            Write-Fail "OnboardingStep declares $($cases.Count) cases, expected 8 (Advance() is step+1 and 8 is the progress segment count)"
        } elseif (@(Compare-Object $cases $Steps -SyncWindow 0).Count -ne 0) {
            Write-Fail "OnboardingStep case order is $($cases -join ', '); expected $($Steps -join ', ')"
        } else {
            Write-Pass "OnboardingStep declares all 8 steps in the macOS order"
        }
    }
} else {
    Write-Fail "OnboardingStep.cs not found at $(Get-RelativePath $stepEnumPath)"
}

$missingPages = @()
foreach ($step in $Steps) {
    $page = Join-Path $StepPagesDir "$($step)StepPage.xaml"
    if (-not (Test-Path -LiteralPath $page)) { $missingPages += "$($step)StepPage.xaml" }
}
if ($missingPages.Count -gt 0) {
    Write-Fail "missing step page(s): $($missingPages -join ', ')"
} else {
    Write-Pass "all 8 step pages exist under $(Get-RelativePath $StepPagesDir)"
}

if (Test-Path -LiteralPath $OnboardingWindowCode) {
    $windowCode = Read-Text $OnboardingWindowCode
    $unrendered = @($Steps | Where-Object { -not $windowCode.Contains("$($_)StepPage()") })
    if ($unrendered.Count -gt 0) {
        Write-Fail "step(s) never navigated to by OnboardingWindow: $($unrendered -join ', ')"
    } else {
        Write-Pass "OnboardingWindow's step switch reaches all 8 pages"
    }
}

# --- every step scrolls ----------------------------------------------------
# macOS wraps each step in GeometryReader { ScrollView { ... } } so a short step
# stays centred and a long one scrolls. A fixed 760 x 624 non-resizable window
# has the same overflow risk with less forgiveness: a step that is not staged
# loses its lower half at 125% scaling with no wheel to reach it.
$unstaged = @()
foreach ($step in $Steps) {
    $page = Join-Path $StepPagesDir "$($step)StepPage.xaml"
    if (-not (Test-Path -LiteralPath $page)) { continue }
    if (-not (Read-Text $page).Contains("OnboardingStage")) { $unstaged += "$($step)StepPage.xaml" }
}
if ($unstaged.Count -gt 0) {
    Write-Fail "step page(s) not wrapped in an OnboardingStage, so their content can be stranded: $($unstaged -join ', ')"
} else {
    Write-Pass "every step page is wrapped in the scrolling OnboardingStage"
}

# ===========================================================================
Write-Section "3. Localization"
# ===========================================================================

function Get-ResxOnboardingKeys {
    param([string] $Path)

    $keys = New-Object System.Collections.Generic.List[string]
    foreach ($match in [regex]::Matches((Read-Text $Path), '<data\s+name="(onboarding\.[^"]+)"')) {
        $keys.Add($match.Groups[1].Value) | Out-Null
    }
    return @($keys | Sort-Object -Unique)
}

if (-not (Test-Path -LiteralPath $BaseResx)) {
    Write-Fail "Strings.resx not found at $(Get-RelativePath $BaseResx)"
    $definedKeys = @()
} else {
    $definedKeys = Get-ResxOnboardingKeys $BaseResx
}

# --- every referenced key exists -------------------------------------------
# Two reference forms: Loc.S(quoted key) in C#, and a {loc:Loc key} markup
# extension in XAML. LocExtension resolves once at PARSE time, so a missing key
# renders as its own identifier on screen rather than throwing.
#
# The extension filter is a Where-Object, not -Include: -Include is silently
# ignored alongside -LiteralPath, which sweeps the .resx files (where every key
# appears as a quoted name= attribute) and this script itself into the "used"
# set, and every key then looks referenced.
$usedKeys = New-Object System.Collections.Generic.List[string]
$keySites = @{}
$sourceFiles = @(Get-ChildItem -LiteralPath $ProjectRoot -File -Recurse |
    Where-Object { $_.Extension -eq ".cs" -or $_.Extension -eq ".xaml" } |
    Where-Object { $_.FullName -notmatch "\\(bin|obj)\\" })
foreach ($file in $sourceFiles) {
    $text = Read-Text $file.FullName
    foreach ($match in [regex]::Matches($text, '(?:"|loc:Loc\s+)(onboarding\.[A-Za-z0-9._]+)')) {
        $key = $match.Groups[1].Value.TrimEnd(".")
        if (-not $keySites.ContainsKey($key)) { $keySites[$key] = Get-RelativePath $file.FullName }
        $usedKeys.Add($key) | Out-Null
    }
}
$usedKeys = @($usedKeys | Sort-Object -Unique)

$missingKeys = @($usedKeys | Where-Object { $definedKeys -notcontains $_ })
if ($missingKeys.Count -gt 0) {
    Write-Fail "$($missingKeys.Count) onboarding key(s) referenced in code but missing from Strings.resx"
    Show-Hits @($missingKeys | ForEach-Object { "$_  ($($keySites[$_]))" }) 12
} else {
    Write-Pass "every onboarding.* key referenced in C#/XAML exists in Strings.resx ($($usedKeys.Count) key(s))"
}

# Dead keys used to be reported as INFO, because four of them shipped: a11y.selected,
# a11y.notSelected, mic.permissionHint and permissions.shortcut.unassigned, each with
# a more specific sibling that won. They are gone from all 40 files now, so the count
# is zero and this can be a real check. It is worth having as one: an onboarding.*
# key is 40 lines across 40 locale files, and nothing else in the repo would ever
# notice that none of them is read.
$unreferenced = @($definedKeys | Where-Object { $usedKeys -notcontains $_ })
if ($unreferenced.Count -gt 0) {
    Write-Fail "$($unreferenced.Count) onboarding.* key(s) are defined in all 40 .resx and referenced nowhere"
    Show-Hits @($unreferenced | Sort-Object) 12
} else {
    Write-Pass "all $($definedKeys.Count) onboarding.* keys in Strings.resx are referenced from C#/XAML"
}

# --- all 40 .resx carry an identical onboarding.* key set ------------------
# The repo's practice is English placeholders in every locale file, so the KEY
# SET has to be identical even while the translations lag. A locale missing a
# key falls through to whatever the resource manager finds, which is how a
# German user ends up reading one English line in the middle of a German step.
$resxFiles = @(Get-ChildItem -LiteralPath $ResourcesDir -Filter "*.resx" -File | Sort-Object Name)
$mismatched = @()
foreach ($resx in $resxFiles) {
    if ($resx.FullName -eq $BaseResx) { continue }
    $keys = Get-ResxOnboardingKeys $resx.FullName
    $diff = @(Compare-Object $definedKeys $keys)
    if ($diff.Count -ne 0) {
        $missing = @($diff | Where-Object { $_.SideIndicator -eq "<=" } | ForEach-Object { $_.InputObject })
        $extra = @($diff | Where-Object { $_.SideIndicator -eq "=>" } | ForEach-Object { $_.InputObject })
        $mismatched += "$($resx.Name): $($keys.Count) keys, $($missing.Count) missing, $($extra.Count) extra"
    }
}
if ($resxFiles.Count -lt 40) {
    Write-Warn "only $($resxFiles.Count) .resx files found under $(Get-RelativePath $ResourcesDir); expected 40"
}
if ($mismatched.Count -gt 0) {
    Write-Fail "$($mismatched.Count) locale file(s) do not carry the base onboarding.* key set"
    Show-Hits $mismatched 12
} else {
    Write-Pass "all $($resxFiles.Count) .resx files carry an identical $($definedKeys.Count)-key onboarding.* set"
}

# --- no em dash ------------------------------------------------------------
# Carried from the macOS gate. The copy uses the middle dot and the ellipsis.
$emDashSources = @($DesignXaml + $OnboardingCs | Select-String -Pattern $EmDash -SimpleMatch)
if ($emDashSources.Count -gt 0) {
    Write-Fail "em dash found in onboarding sources ($($emDashSources.Count) line(s))"
    Show-Hits $emDashSources 10
} else {
    Write-Pass "no em dash in the onboarding sources"
}

$emDashValues = @()
foreach ($resx in $resxFiles) {
    $text = Read-Text $resx.FullName
    foreach ($match in [regex]::Matches($text, '<data\s+name="(onboarding\.[^"]+)"[^>]*>\s*<value>(?<value>[^<]*)</value>')) {
        if ($match.Groups["value"].Value.Contains($EmDash)) {
            $emDashValues += "$($resx.Name): $($match.Groups[1].Value)"
        }
    }
}
if ($emDashValues.Count -gt 0) {
    Write-Fail "em dash found in $($emDashValues.Count) onboarding.* value(s)"
    Show-Hits $emDashValues 10
} else {
    Write-Pass "no em dash in any onboarding.* value in any .resx"
}

# --- no unlocalized user-visible literal -----------------------------------
# Allowed: Text="{loc:Loc ...}", Text="{Binding ...}", and a bare Segoe MDL2
# glyph entity. Flagged: anything with two consecutive letters left once the
# entities are stripped, i.e. real copy typed straight into the XAML.
$unlocalized = @()
foreach ($hit in @($DesignXaml | Select-String -Pattern '(?:Text|Content|ToolTip|Header)="(?<literal>[^"{][^"]*)"' -AllMatches)) {
    foreach ($match in $hit.Matches) {
        $probe = [regex]::Replace($match.Groups["literal"].Value, "&#x[0-9A-Fa-f]+;", "")
        if ($probe -match "[A-Za-z]{2}") {
            $unlocalized += "$($hit.Path):$($hit.LineNumber): $($match.Value)"
        }
    }
}
if ($unlocalized.Count -gt 0) {
    Write-Fail "unlocalized literal in onboarding XAML ($($unlocalized.Count) occurrence(s))"
    Show-Hits $unlocalized 12
} else {
    Write-Pass "no unlocalized user-visible literal in the onboarding XAML"
}

# ===========================================================================
Write-Section "4. Trigger and lifecycle"
# ===========================================================================

if (Test-Path -LiteralPath $LaunchPolicyPath) {
    $policy = Read-Text $LaunchPolicyPath
    Assert-Wired $policy "HYPERWHISPER_WINDOWS_SKIP_ONBOARDING" "the opt-out environment variable is named in OnboardingLaunchPolicy"
    if ($policy -match "IsAppDataRootOverridden") {
        Write-Fail "OnboardingLaunchPolicy consults the app-data override; a scratch profile is how a fresh OnboardingPending is produced"
    } else {
        Write-Pass "OnboardingLaunchPolicy does not consult the app-data override"
    }
} else {
    Write-Fail "OnboardingLaunchPolicy.cs not found at $(Get-RelativePath $LaunchPolicyPath)"
}

if (Test-Path -LiteralPath $AppPath) {
    $appSource = Read-Text $AppPath
    Assert-Wired $appSource "ShouldShowOnboarding = OnboardingLaunchPolicy.ShouldShowOnboarding()" `
        "App.OnStartup decides the first-run flow through OnboardingLaunchPolicy"

    # The override guard belongs to the StartupService block ABOVE this one,
    # which registers a machine-wide Run key. Onboarding writes nothing outside
    # AppDataRoot, so a scratch profile must still see it.
    $decisionIndex = $appSource.IndexOf("ShouldShowOnboarding = OnboardingLaunchPolicy")
    if ($decisionIndex -ge 0) {
        $lineStart = $appSource.LastIndexOf("`n", $decisionIndex)
        $window = $appSource.Substring([Math]::Max(0, $lineStart - 400), [Math]::Min(600, $appSource.Length - [Math]::Max(0, $lineStart - 400)))
        if ($window -match "if\s*\([^)]*IsAppDataRootOverridden[^)]*\)[^;]*ShouldShowOnboarding") {
            Write-Fail "the onboarding decision is gated on AppPaths.IsAppDataRootOverridden"
        } else {
            Write-Pass "the onboarding decision is not gated on AppPaths.IsAppDataRootOverridden"
        }
    }
} else {
    Write-Fail "App.xaml.cs not found at $(Get-RelativePath $AppPath)"
}

if (Test-Path -LiteralPath $MainWindowPath) {
    $mainSource = Read-Text $MainWindowPath

    Assert-Wired $mainSource "if (App.ShouldShowOnboarding)" "MainWindow shows the flow from its Loaded handler"
    Assert-Wired $mainSource "new OnboardingWindow(live.Flow) { Owner = this }" "the onboarding window is owned by the main window"
    Assert-Wired $mainSource "OnboardingLiveDependencies.CreateLive(" "the flow is built at the single live composition point"
    Assert-Wired $mainSource "live?.DisposeResources();" "the seams' OS resources are released when the window closes"
    Assert-Wired $mainSource "OnboardingSession.SetActive(true);" `
        "the onboarding session (recording guard plus text delivery gate) is opened with the flow"
    Assert-Wired $mainSource "OnboardingSession.SetActive(false);" `
        "the onboarding session is closed again when the flow ends"
    Assert-Wired $mainSource 'Loc.S("onboarding.menu.runAgain")' "the tray carries a Run setup again entry"
    Assert-Wired $mainSource "SettingsService.Instance.LaunchMinimized && !App.ShouldShowOnboarding" `
        "the LaunchMinimized hide is gated on the first-run flow"
    Assert-Wired $mainSource "_recordingMenu.Enabled = !IsOnboardingOpen" `
        "the tray cannot start a competing recording while the flow is open"

    # Ordering, not cosmetics. OnNavigatedToAsync is what populates AudioDevices
    # and registers the hotkeys; the Permissions and Microphone steps read both,
    # and KeyboardShortcutService needs this window to own an HWND or the
    # shortcut row reports Unknown forever. And the hide has to come last, or a
    # first-run user with LaunchMinimized on gets an ownerless modal.
    $navIndex = $mainSource.IndexOf("await _viewModel.OnNavigatedToAsync();")
    $showIndex = $mainSource.IndexOf("if (App.ShouldShowOnboarding)")
    $hideIndex = $mainSource.IndexOf("SettingsService.Instance.LaunchMinimized && !App.ShouldShowOnboarding")
    if ($navIndex -ge 0 -and $showIndex -gt $navIndex -and $hideIndex -gt $showIndex) {
        Write-Pass "the flow is shown after OnNavigatedToAsync and before the LaunchMinimized hide"
    } else {
        Write-Fail "startup ordering is wrong: expected OnNavigatedToAsync, then the onboarding show, then the LaunchMinimized hide"
        Write-Detail "OnNavigatedToAsync=$navIndex show=$showIndex hide=$hideIndex"
    }
} else {
    Write-Fail "MainWindow.xaml.cs not found at $(Get-RelativePath $MainWindowPath)"
}

# The global toggle shortcut is a process-wide low-level keyboard hook, so WPF
# modality cannot stop it. The guard has to sit on the recording ENTRY POINTS in
# the view model, not on the tray items, or the hotkey opens a second recorder
# behind the modal and bills a Cloud transcription for it.
if (Test-Path -LiteralPath $MainViewModelPath) {
    $viewModelSource = Read-Text $MainViewModelPath
    $guards = ([regex]::Matches($viewModelSource, [regex]::Escape("if (OnboardingSession.IsActive)"))).Count
    if ($guards -ge 2) {
        Write-Pass "both recording entry points refuse to start while the onboarding window is open ($guards guards)"
    } else {
        Write-Fail "MainViewModel has $guards onboarding guard(s); StartRecordingAsync and StartStreamingRecordingAsync both need one"
    }

    # CopyToClipboard REFUSES under TextDeliveryGate and says so in its return
    # value. A call site that discards it and forces CopiedToClipboard shows the
    # user a "Copied" overlay for text that reached no sink at all.
    $discarded = @([regex]::Matches($viewModelSource, '(?m)^\s*_pasteService\?\.CopyToClipboard\('))
    if ($discarded.Count -eq 0) {
        Write-Pass "every clipboard copy in MainViewModel reads its result rather than assuming success"
    } else {
        Write-Fail "$($discarded.Count) CopyToClipboard call(s) in MainViewModel discard the return value"
        Show-Hits $discarded 6
    }
} else {
    Write-Fail "MainViewModel.cs not found at $(Get-RelativePath $MainViewModelPath)"
}

# MicrophoneKeepWarmService.Configure(enabled, null) takes its !HasValue branch and
# STOPS. Resuming with null therefore tears the app's warm capture stream down and
# leaves it down, and nothing re-Configures it for a returning user.
$GatewayPath = Join-Path $AdaptersDir "OnboardingLiveAudioGateway.cs"
if (Test-Path -LiteralPath $GatewayPath) {
    $gatewaySource = Read-Text $GatewayPath
    if ($gatewaySource.Contains("ResumeAfterRecording(null)")) {
        Write-Fail "the audio gateway resumes keep-warm with a null device, which stops it instead of resuming it"
    } else {
        Write-Pass "keep-warm is always resumed on a real device number, never on null"
    }
} else {
    Write-Fail "OnboardingLiveAudioGateway.cs not found at $(Get-RelativePath $GatewayPath)"
}

# Apply() changes the active Mode with SetSelectedMode, which raises ModeSelected
# and updates MainViewModel's cached SelectedMode. A Restore() that only wrote
# SettingsService.SelectedModeId raised nothing, so the running app kept dictating
# with the onboarding-staged Mode for the rest of the session. The restore path has
# to be as loud as the apply path.
$CommitterPath = Join-Path $AdaptersDir "OnboardingLiveDependencies.cs"
if (Test-Path -LiteralPath $CommitterPath) {
    $committerSource = Read-Text $CommitterPath
    $restoreIndex = $committerSource.IndexOf("public bool Restore(IOnboardingRestorePoint point)")
    $markIndex = $committerSource.IndexOf("public void MarkOnboardingCompleted()")
    if ($restoreIndex -ge 0 -and $markIndex -gt $restoreIndex) {
        $restoreBody = $committerSource.Substring($restoreIndex, $markIndex - $restoreIndex)
        if ($restoreBody.Contains("_modes.SetSelectedMode(")) {
            Write-Pass "Restore puts the mode selection back through SetSelectedMode, so ModeSelected still fires"
        } else {
            Write-Fail "Restore assigns SelectedModeId directly; the running app would keep the staged Mode"
        }
    } else {
        Write-Fail "could not locate LiveOnboardingSourceCommitter.Restore in $(Get-RelativePath $CommitterPath)"
    }
} else {
    Write-Fail "OnboardingLiveDependencies.cs not found at $(Get-RelativePath $CommitterPath)"
}

if (Test-Path -LiteralPath $OnboardingWindowCode) {
    $windowCode = Read-Text $OnboardingWindowCode

    Assert-Wired $windowCode "_flow.DeferSetup();" "Set Up Later and the caption X roll back every staged write and close first run"
    Assert-Wired $windowCode "Closing += OnWindowClosing;" "Alt+F4 and the taskbar reach the flow rather than closing over it"
    Assert-Wired $windowCode "_flow.AbandonSetup();" `
        "Alt+F4, tray Quit and an OS shutdown roll back but leave OnboardingPending set, so first run is re-offered"
    Assert-Wired $windowCode "_flow.Complete();" "the last step commits the staged configuration"
    Assert-Wired $windowCode "e.Key == Key.Escape" "Escape cannot throw away a half-finished setup (macOS interactiveDismissDisabled)"
    Assert-Wired $windowCode "_flow.Cleanup();" "the flow detaches from its seams when the window closes"
} else {
    Write-Fail "OnboardingWindow.xaml.cs not found at $(Get-RelativePath $OnboardingWindowCode)"
}

if (Test-Path -LiteralPath $SmokeProgramPath) {
    $smokeSource = Read-Text $SmokeProgramPath
    Assert-Wired $smokeSource "OnboardingLaunchPolicy.SkipEnvironmentVariable" `
        "the smoke harness opts out of the first-run flow, so a future harness that boots the real App cannot hang on a modal"
} else {
    Write-Fail "the smoke harness was not found at $(Get-RelativePath $SmokeProgramPath)"
}

# ---------------------------------------------------------------------------
# Review round 2. Four findings whose defect needs a real database, a WPF
# Application, a Local API client or a capture device to reproduce, so the
# regression guard is the wiring rather than the behaviour. Everything else
# from that round has a smoke case; section 5 runs them.
# ---------------------------------------------------------------------------

# The Try It step is the only transcription entry point in the app that did not
# load the local engine first. On the DEFAULT first-run path (Parakeet V2, which
# leaves ModelType null so MainViewModel's eager load never fires) that made the
# demo fail with "Local transcription model not loaded".
if (Test-Path -LiteralPath $GatewayPath) {
    $transcribeIndex = $gatewaySource.IndexOf("TranscriptionRuntime.Orchestrator.TranscribeAsync(")
    $readyIndex = $gatewaySource.IndexOf("await EnsureLocalEngineReadyAsync(")
    if ($readyIndex -ge 0 -and $transcribeIndex -gt $readyIndex) {
        Write-Pass "the Try It step loads the local engine before it asks the orchestrator to use it"
    } else {
        Write-Fail "the Try It transcription does not ensure the local model is loaded first"
        Write-Detail "EnsureLocalEngineReadyAsync=$readyIndex TranscribeAsync=$transcribeIndex"
    }

    # AudioDeviceService raises DevicesChanged off the MMDevice COM notification
    # thread. The flow writes bound view-model state from that handler, and is
    # deliberately Dispatcher-free so the smoke suite can drive it, so the
    # ADAPTER is what marshals. app/windows/AGENTS.md's rule.
    $hardwareIndex = $gatewaySource.IndexOf("private void OnHardwareDevicesChanged")
    if ($hardwareIndex -ge 0) {
        $hardwareBody = $gatewaySource.Substring($hardwareIndex, [Math]::Min(1400, $gatewaySource.Length - $hardwareIndex))
        if ($hardwareBody.Contains("OnboardingUiDispatch.Post(")) {
            Write-Pass "the device-change handler marshals to the UI thread before it touches flow state"
        } else {
            Write-Fail "OnHardwareDevicesChanged runs on the COM notification thread with no dispatcher hop"
        }
    }
}

# ModelDownloadService raises DownloadChanged from inside Task.Run with no
# marshalling, ~100 times over a Parakeet download. An unsynchronised Dictionary
# written there and read by the binding layer can tear, throw, or spin forever.
if (Test-Path -LiteralPath $CommitterPath) {
    # The lookbehind matters: ConcurrentDictionary<string, double> _progress
    # CONTAINS the plain form as a substring, so a naive match fails the fix it
    # is meant to protect.
    if ($committerSource -match "(?<!Concurrent)Dictionary<string,\s*double>\s+_progress") {
        Write-Fail "the download-progress store is a plain Dictionary written from the download thread"
    } else {
        Write-Pass "the download-progress store is not an unsynchronised Dictionary"
    }

    $downloadIndex = $committerSource.IndexOf("private void OnDownloadChanged")
    if ($downloadIndex -ge 0) {
        $downloadBody = $committerSource.Substring($downloadIndex, [Math]::Min(1800, $committerSource.Length - $downloadIndex))
        if ($downloadBody.Contains("OnboardingUiDispatch.Post(")) {
            Write-Pass "the download-progress handler marshals to the UI thread"
        } else {
            Write-Fail "OnDownloadChanged mutates progress state on the download thread with no dispatcher hop"
        }
    }
}

# Every USER-INITIATED writer of state the flow stages goes through one funnel,
# and the funnel asks OnboardingSession. Per-call-site checks are what left the
# changeMode hotkey and two tray submenus behind in round 1.
if (Test-Path -LiteralPath $MainViewModelPath) {
    Assert-Wired $viewModelSource "public bool TrySelectMode(Mode? mode)" `
        "the active Mode has ONE user-initiated funnel, which the changeMode hotkey and the tray both use"
    Assert-Wired $viewModelSource "public bool TrySelectAudioDevice(" `
        "and so does the input device"
    Assert-Wired $viewModelSource "return TrySelectMode(Modes[nextIndex]);" `
        "CycleMode (the changeMode global shortcut) goes through the funnel"

    $trayWrites = @([regex]::Matches($viewModelSource, 'OnboardingSession\.BlocksStateChange\('))
    if ($trayWrites.Count -ge 2) {
        Write-Pass "both staged-state funnels consult the onboarding session ($($trayWrites.Count) guards)"
    } else {
        Write-Fail "MainViewModel has $($trayWrites.Count) staged-state guard(s); the Mode and device funnels both need one"
    }
}

if (Test-Path -LiteralPath $MainWindowPath) {
    Assert-Wired $mainSource "_viewModel.TrySelectMode(m)" "the tray Mode submenu goes through the funnel"
    Assert-Wired $mainSource "_viewModel.TrySelectAudioDevice(dev)" "the tray Microphone submenu goes through the funnel"
    Assert-Wired $mainSource "if (_viewModel.HasDictationInFlight)" `
        "onboarding refuses to open OVER a running dictation, whose transcript the delivery gate would then swallow"
}

# The Local API keeps serving against the exact Mode row and shared orchestrator
# the flow stages. One middleware, per METHOD, so a new endpoint inherits it.
$LocalApiServerPath = Join-Path $ProjectRoot "Services\LocalApi\LocalApiServer.cs"
if (Test-Path -LiteralPath $LocalApiServerPath) {
    $serverSource = Read-Text $LocalApiServerPath
    Assert-Wired $serverSource "!IsReadOnlyMethod(ctx.Request.Method) && OnboardingSession.IsActive" `
        "the Local API refuses every mutating request while the first-run window is open"
} else {
    Write-Fail "LocalApiServer.cs not found at $(Get-RelativePath $LocalApiServerPath)"
}

# The four close routes run the SAME rollback, so they owe the same report. Only
# an OS session end may skip it, because only that one can be blocked by a modal.
if (Test-Path -LiteralPath $OnboardingWindowCode) {
    $closingIndex = $windowCode.IndexOf("private void OnWindowClosing")
    if ($closingIndex -ge 0) {
        $closingBody = $windowCode.Substring($closingIndex, [Math]::Min(1800, $windowCode.Length - $closingIndex))
        if ($closingBody.Contains("ReportUnrestoredState();")) {
            Write-Pass "Alt+F4 and the taskbar report a credential or Mode the rollback could not put back"
        } else {
            Write-Fail "OnWindowClosing rolls back but never reports what the rollback could not restore"
        }
    }

    Assert-Wired $windowCode "App.IsSessionEnding" `
        "the no-dialog exception is scoped to an OS session end rather than applied to all four close routes"
    Assert-Wired $windowCode "_flow.ModeRestoreFailed" `
        "a Mode the database refused to restore is reported alongside a lost API key"
}

# The single-instance guard is per PROFILE. A scratch
# HYPERWHISPER_WINDOWS_APPDATA_ROOT instance shares no state with the user's own
# copy, and refusing it is what made the end-to-end first-run demo impossible.
$GuardPath = Join-Path $ProjectRoot "Services\SingleInstanceGuard.cs"
if (Test-Path -LiteralPath $GuardPath) {
    $guardSource = Read-Text $GuardPath
    Assert-Wired $guardSource "AppPaths.IsAppDataRootOverridden" `
        "the single-instance mutex is scoped to the app-data root when it is overridden"
    Assert-Wired $guardSource "AppPaths.AppDataRootHash" `
        "and it reuses CredentialResource's fingerprint rather than inventing a second hashing scheme"
} else {
    Write-Fail "SingleInstanceGuard.cs not found at $(Get-RelativePath $GuardPath)"
}

# ===========================================================================
Write-Section "5. Build and smoke suite"
# ===========================================================================

if ($StaticOnly) {
    $script:Blocked = $true
    Write-Warn "build and smoke suite skipped (-StaticOnly): this run cannot certify the app"
} else {
    Write-Info "building HyperWhisper (Release)"
    dotnet build $ProjectFile -c Release -v minimal --nologo
    if ($LASTEXITCODE -ne 0) {
        Write-Fail "dotnet build FAILED (exit $LASTEXITCODE)"
    } else {
        Write-Pass "the Windows head builds in Release"

        if ($NoTests) {
            $script:Blocked = $true
            Write-Warn "smoke suite skipped (-NoTests): this run cannot certify the app"
        } else {
            Write-Info "running HyperWhisper.SmokeTests (Release)"
            dotnet run --project $SmokeProject -c Release --nologo
            if ($LASTEXITCODE -ne 0) {
                Write-Fail "HyperWhisper.SmokeTests FAILED (exit $LASTEXITCODE)"
            } else {
                Write-Pass "HyperWhisper.SmokeTests is green"
            }
        }
    }
}

# ===========================================================================
Write-Section "Verdict"
# ===========================================================================

if ($script:HardFails -gt 0) {
    Write-Host "FAIL: $($script:HardFails) hard check(s) failed, $($script:Warns) warning(s), $($script:Passes) passed."
    foreach ($line in $script:FailLines) { Write-Detail "- $line" }
    exit 1
}

if ($script:Blocked) {
    Write-Host "INCOMPLETE: the static checks passed but the build or the suite was skipped, so this is NOT a pass."
    exit 2
}

Write-Host "PASS: build green, smoke suite green, $($script:Passes) checks passed, $($script:Warns) warning(s)."
exit 0
