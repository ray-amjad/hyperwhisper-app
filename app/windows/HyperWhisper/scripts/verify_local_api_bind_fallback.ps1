param()

$ErrorActionPreference = "Stop"

function Assert-True {
    param(
        [Parameter(Mandatory = $true)][bool]$Condition,
        [Parameter(Mandatory = $true)][string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Assert-Match {
    param(
        [Parameter(Mandatory = $true)][string]$Content,
        [Parameter(Mandatory = $true)][string]$Pattern,
        [Parameter(Mandatory = $true)][string]$Label
    )

    if (-not [regex]::IsMatch($Content, $Pattern, [System.Text.RegularExpressions.RegexOptions]::Singleline)) {
        throw "Missing expected Local API bind fallback wiring: $Label"
    }
}

$ProjectRoot = Split-Path -Parent $PSScriptRoot
$ServerSource = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot "Services\LocalApi\LocalApiServer.cs")
$FallbackSource = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot "Services\LocalApi\LocalApiBindFallback.cs")
$FallbackSourceForAddType = $FallbackSource -replace "namespace HyperWhisper\.Services\.LocalApi;", "namespace HyperWhisper.Services.LocalApi {"
$FallbackSourceForAddType = $FallbackSourceForAddType + "`n}"

Add-Type -TypeDefinition $FallbackSourceForAddType -WarningAction SilentlyContinue

$fallbackType = [AppDomain]::CurrentDomain.GetAssemblies() |
    ForEach-Object { $_.GetType("HyperWhisper.Services.LocalApi.LocalApiBindFallback", $false) } |
    Where-Object { $_ -ne $null } |
    Select-Object -First 1

Assert-True ($null -ne $fallbackType) "Could not load LocalApiBindFallback for classifier tests."

$method = $fallbackType.GetMethod("ShouldRetryWithEphemeral", [System.Reflection.BindingFlags]::Public -bor [System.Reflection.BindingFlags]::Static)
Assert-True ($null -ne $method) "Could not find LocalApiBindFallback.ShouldRetryWithEphemeral."

function Should-Retry([Exception]$Exception, [int]$PreferredPort) {
    return [bool]$method.Invoke($null, @($Exception, $PreferredPort))
}

Assert-True (Should-Retry ([System.Net.Sockets.SocketException]::new([int][System.Net.Sockets.SocketError]::AccessDenied)) 48123) `
    "Bare WSAEACCES/AccessDenied SocketException on a persisted port must retry ephemeral."

Assert-True (Should-Retry ([System.IO.IOException]::new(
    "Failed to bind",
    [System.Net.Sockets.SocketException]::new([int][System.Net.Sockets.SocketError]::AddressAlreadyInUse))) 48123) `
    "IOException wrapping WSAEADDRINUSE SocketException on a persisted port must retry ephemeral."

Assert-True (Should-Retry ([System.InvalidOperationException]::new("An attempt was made to access a socket in a way forbidden by its access permissions")) 48123) `
    "Kestrel access-permissions bind messages on a persisted port must retry ephemeral."

Assert-True (Should-Retry ([System.InvalidOperationException]::new("address already in use")) 48123) `
    "Kestrel address-in-use bind messages on a persisted port must retry ephemeral."

Assert-True (-not (Should-Retry ([System.Net.Sockets.SocketException]::new([int][System.Net.Sockets.SocketError]::AccessDenied)) 0)) `
    "Ephemeral bind failures must not recursively retry."

Assert-True (-not (Should-Retry ([System.InvalidOperationException]::new("route registration failed")) 48123)) `
    "Non-bind startup failures must still use the fatal error path."

Assert-Match `
    -Content $ServerSource `
    -Pattern "catch \(Exception bindEx\) when \(LocalApiBindFallback\.ShouldRetryWithEphemeral\(bindEx, preferredPort\)\).*?SettingsService\.Instance\.LocalApiServerPersistedPort = 0;.*?await DisposeAppQuietlyAsync\(\);.*?Start\(\);" `
    -Label "persisted-port bind failure clears the stored port and retries on an ephemeral port"

Assert-Match `
    -Content $ServerSource `
    -Pattern "SettingsService\.Instance\.LocalApiServerPersistedPort = boundPort;.*?LocalApiDiscoveryFile\.Write\(boundPort, BearerToken, appVersion\).*?ListeningPort = boundPort;.*?IsRunning = true;" `
    -Label "successful retry persists and publishes the newly discovered port"

Write-Host "Local API bind fallback verifier passed."
