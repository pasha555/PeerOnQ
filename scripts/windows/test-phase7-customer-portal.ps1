<#
  Exercises the Phase 7 customer boundary against the local HTTPS stack.
  Creates uniquely named development-only accounts and leaves their audit data
  in the local database so the run remains inspectable.
#>

[CmdletBinding()]
param(
    [string]$BaseUrl = 'https://localhost:8443',
    [string]$VirtualHost = 'portal.dev.localhost',
    [string]$CloudContainer = 'peeronq-phase6-development-cloud-api-1',
    [switch]$SkipDataProtectionRestart,
    [switch]$SkipPolicyMatrix
)

$ErrorActionPreference = 'Stop'
$script:requestCount = 0
$baseUri = [Uri]$BaseUrl
if ($baseUri.Scheme -ne 'https' -or -not $baseUri.IsLoopback -or $CloudContainer -ne 'peeronq-phase6-development-cloud-api-1') {
    throw 'This acceptance harness modifies only the local HTTPS development stack. Production requires a separate approved mailbox workflow.'
}
$cookieUri = [Uri]"https://${VirtualHost}:$($baseUri.Port)"
$clientVersion = ([xml](Get-Content -LiteralPath (Join-Path $PSScriptRoot '../../Directory.Build.props') -Raw)).Project.PropertyGroup.PeerOnQWindowsClientVersion | Where-Object { $_ } | Select-Object -First 1
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

    $script:requestCount++
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
        throw "$Method $($Path.Split('?')[0]) returned $status, expected $ExpectedStatus. Response body withheld to protect credentials."
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

function Restart-CustomerApi {
    & docker restart $CloudContainer | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Unable to restart local customer API.' }
    Wait-CustomerApi
}
function Wait-CustomerApi {
    for ($attempt = 0; $attempt -lt 45; $attempt++) {
        Start-Sleep -Seconds 1
        $health = (& docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' $CloudContainer).Trim()
        if ($health -eq 'healthy') { return }
    }
    throw 'Local customer API did not become healthy.'
}
function Set-CustomerPolicy([string]$Mode, [bool]$Mfa) {
    $repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
    $deployment = Join-Path $repo 'src/PeerOnQ.Infrastructure.Deployment'
    $priorMode = $env:PEERONQ_CUSTOMER_REGISTRATION_MODE
    $priorMfa = $env:PEERONQ_CUSTOMER_MFA_ENABLED
    $outLog = Join-Path $env:TEMP "peeronq-customer-policy-$suffix.out"
    $errLog = Join-Path $env:TEMP "peeronq-customer-policy-$suffix.err"
    try {
        $env:PEERONQ_CUSTOMER_REGISTRATION_MODE = $Mode
        $env:PEERONQ_CUSTOMER_MFA_ENABLED = $Mfa.ToString().ToLowerInvariant()
        $composeArguments = @('compose', '--env-file', ('"' + (Join-Path $deployment '.env') + '"'), '-f', ('"' + (Join-Path $deployment 'docker-compose.development.yml') + '"'), 'up', '-d', '--no-deps', '--force-recreate', 'cloud-api')
        $process = Start-Process docker.exe -ArgumentList $composeArguments -WindowStyle Hidden -RedirectStandardOutput $outLog -RedirectStandardError $errLog -Wait -PassThru
        if ($process.ExitCode -ne 0) { throw 'Local customer policy fixture recreation failed.' }
        Wait-CustomerApi
        Write-Host "Local acceptance policy: $Mode; customer MFA: $Mfa"
        $reload = Start-Process docker.exe -ArgumentList @('exec', 'peeronq-phase6-development-proxy-1', 'nginx', '-s', 'reload') -WindowStyle Hidden -RedirectStandardOutput $outLog -RedirectStandardError $errLog -Wait -PassThru
        if ($reload.ExitCode -ne 0) { throw 'Local proxy upstream reload failed.' }
    }
    finally { $env:PEERONQ_CUSTOMER_REGISTRATION_MODE = $priorMode; $env:PEERONQ_CUSTOMER_MFA_ENABLED = $priorMfa }
}
function Set-TestTokenAge([string]$Email, [bool]$Expired = $false) {
    if (-not $Email.EndsWith("-$suffix@example.test")) { throw 'Only accounts created by this local acceptance run may be modified.' }
    $expiry = if ($Expired) { ', "ExpiresAtUtc" = NOW() - INTERVAL ''1 minute''' } else { '' }
    $sql = 'UPDATE "CustomerAccountTokens" SET "CreatedAtUtc" = NOW() - INTERVAL ''2 minutes''' + $expiry + ' WHERE "AccountId" = (SELECT "Id" FROM "CustomerAccounts" WHERE "Email" = ''' + $Email + ''') AND "UsedAtUtc" IS NULL;'
    # SQL contains only a generated test-account address; no password or raw token.
    $sqlPath = Join-Path $env:TEMP "peeronq-customer-age-$suffix.sql"
    [IO.File]::WriteAllText($sqlPath, $sql, [Text.UTF8Encoding]::new($false))
    & docker cp $sqlPath 'peeronq-phase6-development-postgres-1:/tmp/peeronq-customer-age.sql' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Unable to stage local token-age fixture.' }
    & docker exec peeronq-phase6-development-postgres-1 sh -c 'psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -v ON_ERROR_STOP=1 -f /tmp/peeronq-customer-age.sql' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Unable to apply local token-age fixture.' }
}

Write-Host "Phase 7 customer acceptance: $suffix"
$capabilities = Invoke-PortalRequest GET '/portal/v1/auth/capabilities' $null $null
if ($capabilities.registrationMode -ne 'Open' -or -not $capabilities.requireEmailVerification -or -not $capabilities.passwordResetAvailable) { throw 'The local harness requires Open registration with verification and FileSink mail.' }
$initialMfa = [bool]$capabilities.mfaAvailable
if ($capabilities.passwordRules.minLength -ne 12 -or $capabilities.passwordRules.maxLength -ne 128) { throw 'Password capability policy mismatch.' }


foreach ($account in $accounts) {
    Invoke-PortalRequest POST '/portal/v1/auth/register' @{
        email = $account.Email
        displayName = $account.Name
        password = $password
        invitationToken = $null
    } $null 202 -NoCsrf | Out-Null
    $verification = Get-MailToken $account.Email 'Verify*'
    if ($account -eq $accounts[0]) {
        Invoke-PortalRequest POST '/portal/v1/auth/login' @{ email = $account.Email; password = $password } $null 401 -NoCsrf | Out-Null
        Set-TestTokenAge $account.Email
        Invoke-PortalRequest POST '/portal/v1/auth/verify-email/resend' @{ email = $account.Email } $null 202 -NoCsrf | Out-Null
        Invoke-PortalRequest POST '/portal/v1/auth/verify-email' @{ token = $verification } $null 400 -NoCsrf | Out-Null
        $verification = Get-MailToken $account.Email 'Verify*'
        Set-TestTokenAge $account.Email $true
        Invoke-PortalRequest POST '/portal/v1/auth/verify-email' @{ token = $verification } $null 400 -NoCsrf | Out-Null
        Invoke-PortalRequest POST '/portal/v1/auth/verify-email/resend' @{ email = $account.Email } $null 202 -NoCsrf | Out-Null
        $verification = Get-MailToken $account.Email 'Verify*'
    }
    Invoke-PortalRequest POST '/portal/v1/auth/verify-email' @{ token = $verification } $null 204 -NoCsrf | Out-Null
}

Write-Host 'PASS: registration, unverified login rejection, resend invalidation and expired verification.'
Restart-CustomerApi # Fresh limiter window after verification negative cases.
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

if (-not $SkipDataProtectionRestart -and $initialMfa) {
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

if (-not $initialMfa) {
    foreach ($endpoint in @('/account/mfa/setup', '/account/mfa/confirm', '/account/mfa')) {
        $method = if ($endpoint -eq '/account/mfa') { 'DELETE' } else { 'POST' }
        $result = Invoke-PortalRequest $method ("/portal/v1" + $endpoint) @{ setupToken = 'invalid'; code = '123456'; password = $password } $sessions[0] 403
        if ($result.code -ne 'customer_mfa_disabled') { throw 'Customer MFA did not fail closed while disabled.' }
    }
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

Write-Host 'PASS: customer MFA guard, tenant/RBAC/CSRF, invitations, teams, policy and audit.'
Restart-CustomerApi # Isolate refresh/reset cases from the earlier authentication rate window.

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

Write-Host 'PASS: refresh family replay and password reset replay.'
# Authenticated password changes retain only the current session and enforce CSRF/current password.
Restart-CustomerApi
$otherSession = [Microsoft.PowerShell.Commands.WebRequestSession]::new()
Invoke-PortalRequest POST '/portal/v1/auth/login' @{ email = $accounts[1].Email; password = $newPassword } $otherSession 200 -NoCsrf | Out-Null
$changedPassword = 'Changed!' + [Guid]::NewGuid().ToString('N') + 'Aa9'
Invoke-PortalRequest POST '/portal/v1/account/password/change' @{ currentPassword = $newPassword; newPassword = $changedPassword } $resetSession 403 -NoCsrf | Out-Null
Invoke-PortalRequest POST '/portal/v1/account/password/change' @{ currentPassword = 'Wrong-Password-9'; newPassword = $changedPassword } $resetSession 400 | Out-Null
Invoke-PortalRequest POST '/portal/v1/account/password/change' @{ currentPassword = $newPassword; newPassword = 'weak' } $resetSession 400 | Out-Null
Invoke-PortalRequest POST '/portal/v1/account/password/change' @{ currentPassword = $newPassword; newPassword = $changedPassword } $resetSession 204 | Out-Null
Invoke-PortalRequest GET '/portal/v1/account/profile' $null $resetSession 200 | Out-Null
Invoke-PortalRequest GET '/portal/v1/account/profile' $null $otherSession 401 | Out-Null
Invoke-PortalRequest POST '/portal/v1/auth/refresh' @{} $otherSession 401 | Out-Null
Invoke-PortalRequest POST '/portal/v1/auth/login' @{ email = $accounts[1].Email; password = $newPassword } $null 401 -NoCsrf | Out-Null
Invoke-PortalRequest GET '/portal/v1/account/sessions' $null $resetSession 200 | Out-Null
Invoke-PortalRequest GET "/portal/v1/organizations/$organizationB/devices" $null $resetSession 200 | Out-Null
Invoke-PortalRequest GET "/portal/v1/organizations/$organizationB/sessions" $null $resetSession 200 | Out-Null
Invoke-PortalRequest PUT '/portal/v1/account/profile' @{ displayName = 'Acceptance updated profile' } $resetSession 204 | Out-Null
Invoke-PortalRequest POST '/portal/v1/auth/logout' $null $resetSession 204 | Out-Null
Invoke-PortalRequest GET '/portal/v1/account/profile' $null $resetSession 401 | Out-Null
Invoke-PortalRequest POST '/portal/v1/auth/login' @{ email = $accounts[1].Email; password = $changedPassword } $resetSession 200 -NoCsrf | Out-Null

Write-Host 'PASS: password change/current session/other session revocation, profile, devices, history and logout.'
if (-not $SkipPolicyMatrix) {
    try {
        Set-CustomerPolicy Closed $false
        $closed = Invoke-PortalRequest GET '/portal/v1/auth/capabilities' $null $null
        if ($closed.registrationAvailable -or $closed.registrationMode -ne 'Closed') { throw 'Closed capability mismatch.' }
        Invoke-PortalRequest POST '/portal/v1/auth/register' @{ email = "closed-$suffix@example.test"; displayName = 'Closed test'; password = $password } $null 403 -NoCsrf | Out-Null
        Set-CustomerPolicy InvitationOnly $false
        Invoke-PortalRequest POST '/portal/v1/auth/register' @{ email = "invite-$suffix@example.test"; displayName = 'Invitation test'; password = $password } $null 403 -NoCsrf | Out-Null
        Invoke-PortalRequest POST "/portal/v1/organizations/$organizationB/invitations" @{ email = "invite-$suffix@example.test"; role = 'Member' } $resetSession 202 | Out-Null
        $inviteToken = Get-MailToken "invite-$suffix@example.test" 'PeerOnQ organization invitation'
        Invoke-PortalRequest POST '/portal/v1/auth/register' @{ email = "invite-$suffix@example.test"; displayName = 'Invitation test'; password = $password; invitationToken = $inviteToken } $null 202 -NoCsrf | Out-Null
        $inviteVerification = Get-MailToken "invite-$suffix@example.test" 'Verify*'
        Invoke-PortalRequest POST '/portal/v1/auth/verify-email' @{ token = $inviteVerification } $null 204 -NoCsrf | Out-Null
        Invoke-PortalRequest POST '/portal/v1/auth/verify-email' @{ token = $inviteVerification } $null 400 -NoCsrf | Out-Null

        Set-CustomerPolicy Open $true
        $mfaSetup = Invoke-PortalRequest POST '/portal/v1/account/mfa/setup' $null $resetSession
        Restart-CustomerApi
        $confirmation = Invoke-PortalRequest POST '/portal/v1/account/mfa/confirm' @{ setupToken = $mfaSetup.setupToken; code = (New-TotpCode $mfaSetup.secret) } $resetSession
        if (@($confirmation.recoveryCodes).Count -ne 10) { throw 'MFA recovery material was not issued.' }
        $policy = Invoke-PortalRequest GET "/portal/v1/organizations/$organizationB/policy" $null $resetSession
        $policy.mfaRequired = $true
        Invoke-PortalRequest PUT "/portal/v1/organizations/$organizationB/policy" $policy $resetSession 204 | Out-Null
        Invoke-PortalRequest POST '/portal/v1/auth/login' @{ email = $accounts[1].Email; password = $changedPassword } $null 401 -NoCsrf | Out-Null

        Set-CustomerPolicy Open $false
        $noMfaSession = [Microsoft.PowerShell.Commands.WebRequestSession]::new()
        Invoke-PortalRequest POST '/portal/v1/auth/login' @{ email = $accounts[1].Email; password = $changedPassword } $noMfaSession 200 -NoCsrf | Out-Null
        $storedProfile = Invoke-PortalRequest GET '/portal/v1/account/profile' $null $noMfaSession
        if (-not $storedProfile.mfaEnabled) { throw 'Disabling customer MFA destroyed stored enrollment.' }
        $effectivePolicy = Invoke-PortalRequest GET "/portal/v1/organizations/$organizationB/policy" $null $noMfaSession
        if ($effectivePolicy.mfaRequired) { throw 'An unavailable MFA requirement remained effective.' }
        Invoke-PortalRequest PUT "/portal/v1/organizations/$organizationB/policy" $policy $noMfaSession 400 | Out-Null
        Invoke-PortalRequest PUT "/portal/v1/organizations/$organizationB/policy" $effectivePolicy $noMfaSession 204 | Out-Null
        $decision = Invoke-PortalRequest POST "/portal/v1/organizations/$organizationB/policy/evaluate" @{ mode = 'ViewOnly'; unattended = $false; relayRegion = 'local-dev'; clientVersion = $clientVersion; hybridSecurityActive = $false } $noMfaSession
        if ($decision.denials -contains 'mfa_required') { throw 'Disabled customer MFA still blocks policy evaluation.' }
        Set-CustomerPolicy Open $true
        $preserved = Invoke-PortalRequest GET "/portal/v1/organizations/$organizationB/policy" $null $noMfaSession
        if (-not $preserved.mfaRequired) { throw 'Stored MFA policy was lost during a disabled-policy edit.' }
        $recoverySession = [Microsoft.PowerShell.Commands.WebRequestSession]::new()
        Invoke-PortalRequest POST '/portal/v1/auth/login' @{ email = $accounts[1].Email; password = $changedPassword; mfaCode = @($confirmation.recoveryCodes)[0] } $recoverySession 200 -NoCsrf | Out-Null
        Invoke-PortalRequest POST '/portal/v1/auth/login' @{ email = $accounts[1].Email; password = $changedPassword; mfaCode = @($confirmation.recoveryCodes)[0] } $null 401 -NoCsrf | Out-Null
    }
    finally { Set-CustomerPolicy Open $initialMfa }
}

# Actual limiter and lockout responses; server policies are never weakened for acceptance.
Restart-CustomerApi
for ($i = 0; $i -lt 8; $i++) { Invoke-PortalRequest POST '/portal/v1/auth/login' @{ email = $accounts[2].Email; password = 'Example-Incorrect-9' } $null 401 -NoCsrf | Out-Null }
Invoke-PortalRequest POST '/portal/v1/auth/login' @{ email = $accounts[2].Email; password = $password } $null 401 -NoCsrf | Out-Null
Invoke-PortalRequest POST '/portal/v1/auth/login' @{ email = "unknown-$suffix@example.test"; password = $password } $null 401 -NoCsrf | Out-Null
Invoke-PortalRequest POST '/portal/v1/auth/login' @{ email = "unknown-$suffix@example.test"; password = $password } $null 429 -NoCsrf | Out-Null
for ($i = 0; $i -lt 5; $i++) { Invoke-PortalRequest POST '/portal/v1/auth/verify-email/resend' @{ email = "unknown-$suffix@example.test" } $null 202 -NoCsrf | Out-Null }
Invoke-PortalRequest POST '/portal/v1/auth/verify-email/resend' @{ email = "unknown-$suffix@example.test" } $null 429 -NoCsrf | Out-Null
Restart-CustomerApi
for ($i = 0; $i -lt 10; $i++) { Invoke-PortalRequest POST '/portal/v1/auth/password-reset/request' @{ email = "unknown-$suffix@example.test" } $null 202 -NoCsrf | Out-Null }
Invoke-PortalRequest POST '/portal/v1/auth/password-reset/request' @{ email = "unknown-$suffix@example.test" } $null 429 -NoCsrf | Out-Null
for ($i = 0; $i -lt 5; $i++) { Invoke-PortalRequest POST '/portal/v1/auth/register' @{ email = "invalid-$suffix@example.test"; displayName = 'Invalid password'; password = 'weak' } $null 400 -NoCsrf | Out-Null }
Invoke-PortalRequest POST '/portal/v1/auth/register' @{ email = "invalid-$suffix@example.test"; displayName = 'Invalid password'; password = 'weak' } $null 429 -NoCsrf | Out-Null
Restart-CustomerApi
Write-Host 'PASS: customer registration/verification/resend/expiry/replay, cookie auth, CSRF, password change/revocation, reset, profile/organizations/devices/sessions/logout, tenant/RBAC/audit and actual rate limits/lockout.' -ForegroundColor Green
if (-not $SkipPolicyMatrix) { Write-Host 'PASS: Closed/Open/InvitationOnly, real invitation signup, MFA off with stored enrollment, MFA restart/recovery and stored/effective organization policy.' -ForegroundColor Green }
if ($SkipDataProtectionRestart -or $SkipPolicyMatrix) { Write-Host 'SKIP: requested acceptance subsections were not executed.' -ForegroundColor Yellow }
Write-Host 'LIVE EMAIL TEST: BLOCKED (this harness uses the Development FileSink, not a production SMTP mailbox).' -ForegroundColor Yellow

Write-Host "Validated HTTP requests: $script:requestCount"
