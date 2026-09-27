<#
  Exercises the Phase 7 customer boundary against the local HTTPS stack.
  Creates uniquely named development-only accounts and leaves their audit data
  in the local database so the run remains inspectable.
#>

[CmdletBinding()]
param(
    [string]$BaseUrl = 'https://127.0.0.1:8443',
    [string]$VirtualHost = 'portal.dev.localhost',
    [string]$CloudContainer = 'peeronq-phase6-development-cloud-api-1',
    [switch]$SkipDataProtectionRestart
)

$ErrorActionPreference = 'Stop'
$baseUri = [Uri]$BaseUrl
$cookieUri = [Uri]"https://${VirtualHost}:$($baseUri.Port)"
$suffix = [DateTimeOffset]::UtcNow.ToString('yyyyMMddHHmmss')
$password = 'Phase7!' + [Guid]::NewGuid().ToString('N') + 'Aa9'
$accounts = @(
    @{ Email = "phase7-owner-a-$suffix@example.test"; Name = 'Phase 7 Owner A' },
    @{ Email = "phase7-owner-b-$suffix@example.test"; Name = 'Phase 7 Owner B' },
    @{ Email = "phase7-member-$suffix@example.test"; Name = 'Phase 7 Member' }
)

function Get-CookieValue([Microsoft.PowerShell.Commands.WebRequestSession]$Session, [string]$Name) {
    $cookie = $Session.Cookies.GetCookies($cookieUri)[$Name]
    if ($null -eq $cookie) { return $null }
    return $cookie.Value
}

function Invoke-PortalRequest {
    param(
        [ValidateSet('GET', 'POST', 'PUT', 'DELETE')][string]$Method,
        [string]$Path,
        [object]$Body,
        [Microsoft.PowerShell.Commands.WebRequestSession]$Session,
        [int]$ExpectedStatus = 200,
        [switch]$NoCsrf
    )

    $headers = @{ Accept = 'application/json'; Host = $VirtualHost }
    if ($null -ne $Session) { $null = $Session.Headers.Remove('X-CSRF-Token') }
    if ($null -ne $Session -and -not $NoCsrf -and $Method -notin @('GET')) {
        $csrf = Get-CookieValue $Session '__Host-peeronq_customer_csrf'
        if ([string]::IsNullOrWhiteSpace($csrf)) { throw "CSRF cookie missing for $Method $Path" }
        $headers['X-CSRF-Token'] = $csrf
    }
    $arguments = @{
        Uri = "$BaseUrl$Path"
        Method = $Method
        Headers = $headers
        UseBasicParsing = $true
    }
    if ($null -ne $Session) { $arguments.WebSession = $Session }
    if ($null -ne $Body) {
        $arguments.ContentType = 'application/json'
        $arguments.Body = $Body | ConvertTo-Json -Depth 8 -Compress
    }
    $status = 0
    $content = ''
    try {
        $response = Invoke-WebRequest @arguments
        $status = [int]$response.StatusCode
        $content = $response.Content
    }
    catch {
        if ($null -eq $_.Exception.Response) { throw }
        $status = [int]$_.Exception.Response.StatusCode
        $stream = $_.Exception.Response.GetResponseStream()
        if ($null -ne $stream) {
            $reader = [IO.StreamReader]::new($stream)
            try { $content = $reader.ReadToEnd() }
            finally { $reader.Dispose() }
        }
    }
    if ($status -ne $ExpectedStatus) {
        throw "$Method $Path returned $status, expected $ExpectedStatus. $content"
    }
    if ([string]::IsNullOrWhiteSpace($content)) { return $null }
    $parsed = $content | ConvertFrom-Json
    if ($parsed -is [Array]) { return $parsed | ForEach-Object { $_ } }
    return $parsed
}

function Get-MailToken([string]$Recipient, [string]$SubjectPattern) {
    $files = @(& docker exec $CloudContainer find /var/lib/peeronq/customer-mail -type f)
    if ($LASTEXITCODE -ne 0) { throw 'Unable to inspect the development mail sink.' }
    foreach ($file in ($files | Sort-Object -Descending)) {
        $mail = (& docker exec $CloudContainer cat $file) | ConvertFrom-Json
        if ($mail.recipient -ne $Recipient -or $mail.subject -notlike $SubjectPattern) { continue }
        if ($mail.textBody -notmatch '[?&]token=([^\s]+)') { throw "Mail for $Recipient did not contain a token." }
        return [Uri]::UnescapeDataString($Matches[1])
    }
    throw "Mail for $Recipient was not found in the development sink."
}

function ConvertFrom-Base32([string]$Value) {
    $alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567'
    $bits = [Text.StringBuilder]::new()
    foreach ($character in $Value.Trim().TrimEnd('=').ToUpperInvariant().ToCharArray()) {
        $index = $alphabet.IndexOf($character)
        if ($index -lt 0) { throw 'The MFA secret contains an invalid Base32 character.' }
        $null = $bits.Append([Convert]::ToString($index, 2).PadLeft(5, '0'))
    }
    $bytes = [Collections.Generic.List[byte]]::new()
    for ($offset = 0; $offset + 8 -le $bits.Length; $offset += 8) {
        $bytes.Add([Convert]::ToByte($bits.ToString($offset, 8), 2))
    }
    return $bytes.ToArray()
}

function New-TotpCode([string]$Base32Secret) {
    $secret = ConvertFrom-Base32 $Base32Secret
    [Int64]$counter = [Math]::Floor([DateTimeOffset]::UtcNow.ToUnixTimeSeconds() / 30)
    $counterBytes = [BitConverter]::GetBytes($counter)
    if ([BitConverter]::IsLittleEndian) { [Array]::Reverse($counterBytes) }
    $hmac = [Security.Cryptography.HMACSHA1]::new($secret)
    try { $digest = $hmac.ComputeHash($counterBytes) }
    finally { $hmac.Dispose() }
    $offset = $digest[$digest.Length - 1] -band 0x0f
    $binary = (($digest[$offset] -band 0x7f) -shl 24) -bor
        (($digest[$offset + 1] -band 0xff) -shl 16) -bor
        (($digest[$offset + 2] -band 0xff) -shl 8) -bor
        ($digest[$offset + 3] -band 0xff)
    return ($binary % 1000000).ToString('D6')
}

Write-Host "Phase 7 customer acceptance: $suffix"

foreach ($account in $accounts) {
    Invoke-PortalRequest POST '/portal/v1/auth/register' @{
        email = $account.Email
        displayName = $account.Name
        password = $password
        invitationToken = $null
    } $null 202 -NoCsrf | Out-Null
    $verification = Get-MailToken $account.Email 'Verify*'
    Invoke-PortalRequest POST '/portal/v1/auth/verify-email' @{ token = $verification } $null 204 -NoCsrf | Out-Null
}

$sessions = @(
    [Microsoft.PowerShell.Commands.WebRequestSession]::new(),
    [Microsoft.PowerShell.Commands.WebRequestSession]::new(),
    [Microsoft.PowerShell.Commands.WebRequestSession]::new()
)
for ($index = 0; $index -lt $accounts.Count; $index++) {
    Invoke-PortalRequest POST '/portal/v1/auth/login' @{
        email = $accounts[$index].Email
        password = $password
        mfaCode = $null
    } $sessions[$index] 200 -NoCsrf | Out-Null
}

if (-not $SkipDataProtectionRestart) {
    $mfaSetup = Invoke-PortalRequest POST '/portal/v1/account/mfa/setup' $null $sessions[0]
    & docker restart $CloudContainer | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Unable to restart Cloud API for the Data Protection persistence check.' }
    $healthy = $false
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        Start-Sleep -Seconds 1
        $health = (& docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' $CloudContainer).Trim()
        if ($health -eq 'healthy') { $healthy = $true; break }
    }
    if (-not $healthy) { throw 'Cloud API did not become healthy after the Data Protection persistence restart.' }
    Invoke-PortalRequest GET '/portal/v1/account/profile' $null $sessions[0] 200 | Out-Null
    $mfaResult = Invoke-PortalRequest POST '/portal/v1/account/mfa/confirm' @{ setupToken = $mfaSetup.setupToken; code = 'invalid' } $sessions[0] 400
    if ($mfaResult.code -ne 'mfa_code_invalid') { throw 'The persisted MFA setup token could not be decrypted after restart.' }

    $mfaCode = New-TotpCode $mfaSetup.secret
    $mfaConfirmation = Invoke-PortalRequest POST '/portal/v1/account/mfa/confirm' @{
        setupToken = $mfaSetup.setupToken
        code = $mfaCode
    } $sessions[0] 200
    if (@($mfaConfirmation.recoveryCodes).Count -ne 10) { throw 'MFA confirmation did not issue ten recovery codes.' }

    $withoutMfa = [Microsoft.PowerShell.Commands.WebRequestSession]::new()
    Invoke-PortalRequest POST '/portal/v1/auth/login' @{
        email = $accounts[0].Email; password = $password; mfaCode = $null
    } $withoutMfa 401 -NoCsrf | Out-Null
    $recoveryCode = @($mfaConfirmation.recoveryCodes)[0]
    $withRecovery = [Microsoft.PowerShell.Commands.WebRequestSession]::new()
    Invoke-PortalRequest POST '/portal/v1/auth/login' @{
        email = $accounts[0].Email; password = $password; mfaCode = $recoveryCode
    } $withRecovery 200 -NoCsrf | Out-Null
    $replayedRecovery = [Microsoft.PowerShell.Commands.WebRequestSession]::new()
    Invoke-PortalRequest POST '/portal/v1/auth/login' @{
        email = $accounts[0].Email; password = $password; mfaCode = $recoveryCode
    } $replayedRecovery 401 -NoCsrf | Out-Null
}

$organizationsA = @(Invoke-PortalRequest GET '/portal/v1/organizations/' $null $sessions[0])
$organizationsB = @(Invoke-PortalRequest GET '/portal/v1/organizations/' $null $sessions[1])
$organizationA = [Guid]$organizationsA[0].id
$organizationB = [Guid]$organizationsB[0].id

# Cross-tenant reads and mutations must fail before resource details leak.
Invoke-PortalRequest GET "/portal/v1/organizations/$organizationB/members" $null $sessions[0] 403 | Out-Null
Invoke-PortalRequest PUT "/portal/v1/organizations/$organizationB/policy" @{
    viewOnlyAllowed = $true; fullControlAllowed = $false; fileTransferAllowed = $false
    clipboardAllowed = $false; unattendedAccessAllowed = $false; mfaRequired = $false
    trustedDeviceLifetimeDays = 30; auditRetentionDays = 90; approvedRelayRegionsCsv = ''
    minimumClientVersion = ''; hybridSecurityRequired = $false
} $sessions[0] 403 | Out-Null

# Missing CSRF is rejected even when the authenticated cookies are valid.
Invoke-PortalRequest POST "/portal/v1/organizations/$organizationA/teams" @{ name = 'Must not exist' } $sessions[0] 403 -NoCsrf | Out-Null

$invitation = Invoke-PortalRequest POST "/portal/v1/organizations/$organizationA/invitations" @{
    email = $accounts[2].Email; role = 'Member'
} $sessions[0] 202
$invitationToken = Get-MailToken $accounts[2].Email 'PeerOnQ organization invitation'
Invoke-PortalRequest POST '/portal/v1/organizations/invitations/accept' @{ token = $invitationToken } $sessions[2] 204 | Out-Null
Invoke-PortalRequest POST '/portal/v1/organizations/invitations/accept' @{ token = $invitationToken } $sessions[2] 400 | Out-Null

$members = @(Invoke-PortalRequest GET "/portal/v1/organizations/$organizationA/members" $null $sessions[0])
$member = $members | Where-Object { $_.email -eq $accounts[2].Email } | Select-Object -First 1
if ($null -eq $member -or $member.role -ne 'Member') {
    $observed = ($members | ForEach-Object { "$($_.email):$($_.role)" }) -join ', '
    throw "The accepted invitation did not create the expected member role. Observed: $observed"
}

# A Member cannot elevate its own organization role.
Invoke-PortalRequest PUT "/portal/v1/organizations/$organizationA/members/$($member.accountId)/role" @{ role = 'Administrator' } $sessions[2] 403 | Out-Null

$team = Invoke-PortalRequest POST "/portal/v1/organizations/$organizationA/teams" @{ name = 'Support Engineering' } $sessions[0] 201
Invoke-PortalRequest POST "/portal/v1/organizations/$organizationA/teams/$($team.id)/members" @{ accountId = $member.accountId } $sessions[0] 204 | Out-Null

Invoke-PortalRequest PUT "/portal/v1/organizations/$organizationA/policy" @{
    viewOnlyAllowed = $true; fullControlAllowed = $false; fileTransferAllowed = $false
    clipboardAllowed = $false; unattendedAccessAllowed = $false; mfaRequired = $false
    trustedDeviceLifetimeDays = 14; auditRetentionDays = 180; approvedRelayRegionsCsv = 'local-dev'
    minimumClientVersion = '0.7.0'; hybridSecurityRequired = $false
} $sessions[0] 204 | Out-Null
$decision = Invoke-PortalRequest POST "/portal/v1/organizations/$organizationA/policy/evaluate" @{
    mode = 'FullControl'; unattended = $false; relayRegion = 'local-dev'; clientVersion = '0.7.0'; hybridSecurityActive = $false
} $sessions[0]
if ($decision.allowed -or $decision.denials -notcontains 'connection_mode_denied') { throw 'Organization policy did not deny Full Control.' }

# Ownership transfer is explicit; the former owner cannot transfer it again.
Invoke-PortalRequest POST "/portal/v1/organizations/$organizationA/transfer-ownership" @{ newOwnerAccountId = $member.accountId } $sessions[0] 204 | Out-Null
Invoke-PortalRequest POST "/portal/v1/organizations/$organizationA/transfer-ownership" @{ newOwnerAccountId = $members[0].accountId } $sessions[0] 403 | Out-Null

$audit = @(Invoke-PortalRequest GET "/portal/v1/organizations/$organizationA/audit" $null $sessions[2])
if ($audit.Count -lt 5 -or $audit.action -notcontains 'organization.owner_transferred') { throw 'Organization audit evidence is incomplete.' }

# Refresh tokens rotate once. Replaying the old token revokes the full family.
$oldRefresh = Get-CookieValue $sessions[1] '__Host-peeronq_customer_refresh'
Invoke-PortalRequest POST '/portal/v1/auth/refresh' @{ refreshToken = $null } $sessions[1] 200 | Out-Null
$replaySession = [Microsoft.PowerShell.Commands.WebRequestSession]::new()
$csrfBytes = [byte[]]::new(32)
$csrfGenerator = [Security.Cryptography.RandomNumberGenerator]::Create()
try { $csrfGenerator.GetBytes($csrfBytes) }
finally { $csrfGenerator.Dispose() }
$csrfValue = ([BitConverter]::ToString($csrfBytes)).Replace('-', '')
$csrfCookie = [Net.Cookie]::new('__Host-peeronq_customer_csrf', $csrfValue, '/', $VirtualHost)
$csrfCookie.Secure = $true
$replaySession.Cookies.Add($cookieUri, $csrfCookie)
Invoke-PortalRequest POST '/portal/v1/auth/refresh' @{ refreshToken = $oldRefresh } $replaySession 401 | Out-Null
Invoke-PortalRequest GET '/portal/v1/account/profile' $null $sessions[1] 401 | Out-Null

# Password reset is enumeration-safe, single-use, and invalidates the old password/session family.
$newPassword = 'Phase7!' + [Guid]::NewGuid().ToString('N') + 'Zz8'
Invoke-PortalRequest POST '/portal/v1/auth/password-reset/request' @{ email = $accounts[1].Email } $null 202 -NoCsrf | Out-Null
$resetToken = Get-MailToken $accounts[1].Email 'Reset*'
Invoke-PortalRequest POST '/portal/v1/auth/password-reset/complete' @{
    token = $resetToken; newPassword = $newPassword
} $null 204 -NoCsrf | Out-Null
Invoke-PortalRequest POST '/portal/v1/auth/password-reset/complete' @{
    token = $resetToken; newPassword = $newPassword
} $null 400 -NoCsrf | Out-Null
$oldPasswordSession = [Microsoft.PowerShell.Commands.WebRequestSession]::new()
Invoke-PortalRequest POST '/portal/v1/auth/login' @{
    email = $accounts[1].Email; password = $password; mfaCode = $null
} $oldPasswordSession 401 -NoCsrf | Out-Null
$resetSession = [Microsoft.PowerShell.Commands.WebRequestSession]::new()
Invoke-PortalRequest POST '/portal/v1/auth/login' @{
    email = $accounts[1].Email; password = $newPassword; mfaCode = $null
} $resetSession 200 -NoCsrf | Out-Null

Write-Host 'PASS: registration, verification, MFA/recovery, reset, restart persistence, cookie auth, CSRF, tenant isolation, invitation replay, RBAC, teams, policy, ownership, audit and refresh replay.' -ForegroundColor Green
