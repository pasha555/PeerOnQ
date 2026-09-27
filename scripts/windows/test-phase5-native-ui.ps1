<# Verifies Phase 5 native UI wiring, accessibility baselines, and open-source disclosures. #>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))

function Read-RepositoryFile([string]$RelativePath) {
    $path = Join-Path $repoRoot $RelativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required Phase 5 UI file is missing: $RelativePath"
    }
    return Get-Content -Raw -LiteralPath $path
}

function Assert-Contains([string]$Text, [string]$Expected, [string]$Failure) {
    if ($Text.IndexOf($Expected, [StringComparison]::Ordinal) -lt 0) { throw $Failure }
}

function Assert-NotContains([string]$Text, [string]$Unexpected, [string]$Failure) {
    if ($Text.IndexOf($Unexpected, [StringComparison]::Ordinal) -ge 0) { throw $Failure }
}

function Assert-EventHandlers([string]$XamlPath, [string]$CodePath) {
    $xaml = Read-RepositoryFile $XamlPath
    $code = Read-RepositoryFile $CodePath
    $handlers = [regex]::Matches(
        $xaml,
        '(?:Click|Toggled|SelectionChanged|TextChanged|QuerySubmitted|KeyDown|KeyUp|PointerEntered|PointerExited|PointerPressed|PointerReleased|PointerCaptureLost|PointerMoved|PointerWheelChanged|Loaded|Unloaded)="([A-Za-z0-9_]+)"') |
        ForEach-Object { $_.Groups[1].Value } |
        Sort-Object -Unique
    foreach ($handler in $handlers) {
        if ($code -notmatch "\b$([regex]::Escape($handler))\s*\(") {
            throw "$XamlPath references missing handler $handler in $CodePath."
        }
    }
}

$mainXaml = Read-RepositoryFile 'src\PeerOnQ.App\MainWindow.xaml'
$appXaml = Read-RepositoryFile 'src\PeerOnQ.App\App.xaml'
$viewerXaml = Read-RepositoryFile 'src\PeerOnQ.App\ViewerWindow.xaml'
$indicatorXaml = Read-RepositoryFile 'src\PeerOnQ.App\SharingIndicatorWindow.xaml'
$permissionHost = Read-RepositoryFile 'src\PeerOnQ.App\PermissionDialogHost.cs'
$router = Read-RepositoryFile 'artifacts\peeronq\src\app\router\index.tsx'
$landingPage = Read-RepositoryFile 'artifacts\peeronq\src\pages\LandingPage.tsx'

foreach ($panel in @(
    'DashboardPanel', 'DevicesPanel', 'SessionsPanel', 'FileTransferPanel',
    'AddressBookPanel', 'SecurityPanel', 'SettingsPanel')) {
    Assert-Contains $mainXaml "x:Name=`"$panel`"" "Native page surface is missing: $panel"
}

Assert-Contains $mainXaml 'PaneDisplayMode="Auto"' 'NavigationView must adapt between expanded and compact layouts.'
Assert-Contains $appXaml '<ResourceDictionary x:Key="HighContrast">' 'Native high-contrast resources are missing.'
Assert-Contains $appXaml 'x:Key="PeerOnQTrustSubtleBrush"' 'Native consent surfaces have no theme-aware trust treatment.'
Assert-Contains $mainXaml 'AutomationProperties.LiveSetting="Polite"' 'Dynamic native connection states are not exposed as polite live regions.'
Assert-Contains $mainXaml 'x:Name="HeaderConnectedDot"' 'Connected signaling state has no dedicated visual indicator.'
Assert-Contains $mainXaml 'Fill="{ThemeResource PeerOnQAccentBrush}"' 'Connected signaling state does not use the theme-aware green accent.'
Assert-Contains $mainXaml 'PeerOnQ is MIT-licensed open-source software.' 'Native About disclosure is missing.'
Assert-Contains $mainXaml 'Open local data folder' 'The user-data preservation/removal path is not exposed in Settings.'
Assert-NotContains $mainXaml 'x:Name="SignalingServerBox"' 'Settings must not expose the internally managed signaling endpoint.'
Assert-NotContains $mainXaml 'Text="Connection" Style="{StaticResource PeerOnQSectionTitleStyle}"' 'Settings still exposes the Connection card.'
Assert-Contains $mainXaml 'x:Name="CheckUpdateButton" Content="Check for updates" AccessKey="U"' 'Settings has no verified server update check action.'
Assert-Contains $mainXaml 'IsEnabled="False" Click="OnCheckForUpdates"' 'Update check must remain disabled until trusted configuration initializes.'
Assert-Contains $mainXaml 'Performance (up to 60 fps)' 'The explicit performance quality profile is missing.'
Assert-Contains $mainXaml 'Office (responsive, clear text)' 'The Office quality profile is missing.'
Assert-Contains $mainXaml 'Low bandwidth' 'The Low Bandwidth quality profile is missing.'
Assert-Contains $mainXaml 'x:Name="QualityPicker" Header="Display quality" SelectedIndex="0"' 'Production must default to the adaptive Automatic quality profile.'
Assert-Contains $mainXaml 'x:Name="ResolutionPicker" Header="Target resolution" SelectedIndex="0"' 'Production must default to automatic resolution selection.'
Assert-Contains $mainXaml 'Content="1440p" Tag="P1440"' 'The explicit 1440p target is missing.'
Assert-Contains $mainXaml 'Content="4K / UHD" Tag="P2160"' 'The explicit non-upscaling 4K target is missing.'
Assert-Contains $mainXaml 'Content="Run Network Doctor"' 'The deterministic Network Doctor action is missing.'
foreach ($removedOption in @('Add file transfer', 'Allow text clipboard', 'Use configured unattended access')) {
    Assert-NotContains $mainXaml $removedOption "The simplified connection card still exposes removed option: $removedOption"
}
Assert-NotContains $mainXaml 'x:Name="ClipboardToggle"' 'The file-transfer surface still exposes the removed clipboard toggle.'
Assert-NotContains $mainXaml 'Plain-text clipboard sharing is separate' 'The file-transfer surface still exposes the removed Remote device card.'
Assert-Contains $mainXaml 'x:Name="UnattendedConnectionToggle"' 'Dashboard has no explicit unattended connection switch.'
Assert-Contains $mainXaml 'Header="Remote unattended password or recovery code"' 'Dashboard has no labeled unattended credential field.'
Assert-Contains $mainXaml 'PasswordRevealMode="Peek"' 'Password fields do not provide the platform reveal affordance.'
Assert-Contains $mainXaml 'Header="Maximum unattended connection mode"' 'Security does not expose the unattended permission scope.'
Assert-Contains $mainXaml 'Allow approved trusted devices without a password' 'Trusted-device unattended behavior is not described precisely.'
Assert-Contains $viewerXaml 'AutomationProperties.Name="Display scaling: Fit to window"' 'Viewer scale control has no screen-reader name.'
foreach ($scaleMode in @('Fit to window', 'Fill window (crop edges)', 'Stretch to window', 'Actual size (100%)')) {
    Assert-Contains $viewerXaml $scaleMode "Viewer scale mode is missing: $scaleMode"
}
Assert-Contains $viewerXaml 'SizeChanged="OnScrollerSizeChanged"' 'Viewer scaling is not bound to viewport size changes.'
Assert-Contains $viewerXaml 'x:Name="RemoteImageViewport"' 'Viewer image is not isolated from unconstrained ScrollViewer measurement.'
$viewerCode = Read-RepositoryFile 'src\PeerOnQ.App\ViewerWindow.xaml.cs'
Assert-Contains $viewerCode 'ApplyWindowScaleDimensions(e.NewSize.Width, e.NewSize.Height)' 'Viewer resize does not update the remote image viewport.'
Assert-Contains $viewerCode 'RemoteImageViewport.Width = width' 'Viewer scale modes do not pin the image container to the visible viewport.'
Assert-Contains $viewerCode 'if (_displayScaleMode == DisplayScaleMode.ActualSize) return;' 'Actual-size mode must remain independent of viewport resizing.'
Assert-Contains $viewerXaml 'AutomationProperties.Name="Remote keyboard and pointer control surface"' 'Remote input surface has no screen-reader name.'
Assert-NotContains $viewerXaml 'Post-Quantum Protected' 'The viewer toolbar still exposes the implementation-specific post-quantum label.'
Assert-NotContains $viewerXaml 'Resume control' 'The viewer still exposes a manual resume-control action.'
Assert-NotContains $viewerCode 'ControlInputButton' 'The viewer still contains a manual input-control action.'
Assert-Contains $viewerCode 'EnableAuthorizedInput();' 'Approved Full Control does not automatically restore after viewer activation.'
Assert-Contains $viewerCode 'public void AttachRemoteInput(RemoteInputSession? remoteInput)' 'Viewer cannot bind remote input after secure collaboration becomes ready.'
Assert-Contains $viewerXaml 'PointerEntered="OnRemoteImagePointerEntered"' 'The remote image does not restore approved control on pointer entry.'
Assert-Contains $viewerXaml 'AllowDrop="True" DragOver="OnRemoteFilesDragOver" Drop="OnRemoteFilesDropped"' 'Full Control has no drop target for direct file transfer.'
Assert-Contains $viewerCode 'OfferClipboardFilesAsync' 'Full Control does not offer copied files through the existing file-transfer channel.'
Assert-Contains $viewerCode 'StandardDataFormats.StorageItems' 'File drop and paste must ignore non-file clipboard content.'
Assert-NotContains $viewerXaml 'AppBarToggleButton x:Name="ControlInputButton"' 'Full Control still exposes a redundant active-state toggle.'
Assert-NotContains $viewerXaml 'Label="Control active"' 'Full Control still presents status text as a toolbar action.'
Assert-NotContains $viewerCode 'locallyEnabled ? "Pause control"' 'Full Control still exposes a visible pause-control action.'
Assert-Contains $viewerXaml '<CommandBar.SecondaryCommands>' 'Viewer has no overflow command menu.'
Assert-Contains $viewerXaml 'x:Name="FullScreenRevealBar"' 'Fullscreen has no pointer-revealed exit control.'
Assert-Contains $viewerCode 'OnFullScreenRevealTargetEntered' 'Fullscreen top-edge reveal is not wired.'
Assert-Contains $viewerXaml 'x:Name="FileTransferButton"' 'Viewer has no file-transfer command beside Fullscreen.'
Assert-Contains $viewerXaml 'x:Name="TransferProgressContainer" Visibility="Collapsed"' 'Viewer progress is not a compact toolbar element beside Fullscreen.'
Assert-Contains $viewerXaml '<Border Width="184"' 'Viewer progress is not constrained to the requested short toolbar width.'
Assert-Contains $viewerXaml 'x:Name="TransferProgressBar" Minimum="0" Maximum="100"' 'Viewer has no fixed determinate 0-100 file-transfer progress.'
Assert-Contains $viewerCode 'PickMultipleFilesAsync()' 'Viewer file-transfer command does not open the real multi-file picker.'
Assert-Contains $viewerCode '_fileTransfers.TransferChanged += OnFileTransferChanged;' 'Viewer progress is not bound to authoritative transfer snapshots.'
Assert-Contains $viewerCode 'snapshot.Status == TransferStatus.Completed' 'Viewer progress does not force completed transfers to exactly 100 percent.'
Assert-Contains $viewerCode 'Unsafe.ReadUnaligned<uint>' 'Viewer frame expansion is missing the bounded packed-pixel path.'
Assert-Contains $viewerXaml 'Label="Copy connection details"' 'The optional viewer diagnostic action is not described in plain language.'
Assert-Contains $indicatorXaml 'Content="Revoke control"' 'Sharing indicator has no visible revoke-control action.'
Assert-Contains $indicatorXaml 'Content="End session"' 'Sharing indicator has no visible end-session action.'
Assert-Contains $indicatorXaml 'CardBackgroundFillColorDefaultBrush' 'Sharing indicator does not use the neutral compact surface.'
Assert-NotContains $indicatorXaml 'SystemFillColorCriticalBrush' 'Sharing indicator still uses the oversized critical-red treatment.'
Assert-Contains $indicatorXaml 'x:Name="CompactStopButton"' 'Sharing indicator has no direct compact end-session control.'
Assert-Contains $indicatorXaml 'Click="OnExpand"' 'Sharing indicator cannot expand its details on demand.'
Assert-Contains $indicatorXaml 'Click="OnCollapse"' 'Sharing indicator cannot collapse its details on demand.'
Assert-Contains $indicatorXaml 'AutomationProperties.Name="End sharing session"' 'Compact end-session control is not accessible.'
$indicatorCode = Read-RepositoryFile 'src\PeerOnQ.App\SharingIndicatorWindow.xaml.cs'
Assert-Contains $indicatorCode 'DisplayArea.GetFromWindowId' 'Sharing indicator is not positioned relative to the active display.'
Assert-Contains $indicatorCode 'MoveToTopCenter' 'Sharing indicator does not move to top center.'
Assert-Contains $permissionHost 'Title = "Incoming connection"' 'Permission dialog has no accessible title.'
Assert-Contains $permissionHost 'MinWidth = 400' 'Permission dialog is no longer compact at its minimum width.'
Assert-Contains $permissionHost 'MaxWidth = 440' 'Permission dialog can grow beyond its compact desktop width.'
Assert-Contains $permissionHost 'MaxHeight = 480' 'Permission dialog can grow beyond its compact desktop height.'
Assert-Contains $permissionHost 'Text = "Choose access"' 'Permission dialog has no concise access hierarchy.'
Assert-NotContains $permissionHost 'CreateHero()' 'Permission dialog still renders the oversized hero card.'
Assert-NotContains $permissionHost 'CreateProtectionNotice' 'Permission dialog still renders the verbose protection card.'
Assert-Contains $permissionHost 'DefaultButton = ContentDialogButton.Secondary' 'Permission dialog must default to decline.'
Assert-Contains $permissionHost 'accessChoices = new RadioButtons { SelectedIndex = -1 };' 'Attended access must require an explicit host scope choice.'
Assert-Contains $permissionHost 'IsPrimaryButtonEnabled = accessChoices is null' 'Attended access can be accepted before the host chooses a scope.'
Assert-Contains $permissionHost '1 => "Allow Full Control"' 'The permission dialog does not name the exact Full Control grant on its action.'
Assert-NotContains $permissionHost 'accessChoices.Items.Add(new RadioButton' 'Access choices must use RadioButtons-owned containers so SelectedIndex stays authoritative.'
Assert-Contains $permissionHost 'dispatcher.CreateTimer()' 'Permission countdown must stay on the UI dispatcher.'
Assert-NotContains $permissionHost 'Task.Run(' 'Permission countdown must not marshal WinUI objects from a background task.'
$mainCode = Read-RepositoryFile 'src\PeerOnQ.App\MainWindow.xaml.cs'
$appProject = Read-RepositoryFile 'src\PeerOnQ.App\PeerOnQ.App.csproj'
$appServices = Read-RepositoryFile 'src\PeerOnQ.App\AppServices.cs'
$endpointConfiguration = Read-RepositoryFile 'src\PeerOnQ.Infrastructure\Configuration\CloudEndpointConfiguration.cs'
Assert-Contains $mainXaml 'Content="Open Account Portal"' 'Settings must expose optional Account Portal navigation.'
Assert-Contains $mainXaml 'LAN connections remain accountless.' 'Account Portal must not be presented as a LAN sign-in requirement.'
Assert-Contains $mainXaml 'Account sign-in is separate from device enrollment.' 'Account and device identities must remain distinct.'
Assert-Contains $appProject '<AssemblyMetadata Include="PeerOnQAccountPortalUrl"' 'Portal must reuse compiled deployment metadata.'
Assert-Contains $endpointConfiguration 'https://portal.peeronq.com' 'The default Account Portal host is missing.'
$portalAction = [regex]::Match($mainCode, '(?s)private async void OnOpenAccountPortal\(.*?(?=\r?\n    private )').Value
Assert-Contains $portalAction 'CloudEndpointConfiguration.ResolveAccountPortalUri(' 'Browser navigation must validate the compiled portal URL.'
Assert-Contains $portalAction 'Windows.System.Launcher.LaunchUriAsync(portalUri)' 'Account Portal must open the system browser with only the validated URI.'
if ($portalAction -notmatch '#else\s+const bool AllowDevelopmentLoopback = false;') { throw 'Release builds must require HTTPS.' }
foreach ($forbidden in @('_services', 'Token', 'Credential', 'WebView', 'HttpClient', 'UriBuilder')) {
    Assert-NotContains $portalAction $forbidden "Portal navigation must not depend on identity or mutate its URI: $forbidden"
}
foreach ($nativeSource in @($mainCode, $appServices)) {
    Assert-NotContains $nativeSource '/portal/v1/auth' 'Native session startup must not introduce customer authentication.'
    Assert-NotContains $nativeSource '__Host-peeronq_customer' 'Customer cookies must stay in the browser.'
}
if ($appServices -notmatch 'var cloudEndpoints = lanDevelopmentClient\s+\? null') { throw 'LAN builds must retain optional cloud enrollment.' }
Assert-Contains $appServices 'CloudEndpoints is null || Identity.PublicIdServerAssigned' 'Accountless routing must remain available without cloud enrollment.'
Assert-Contains $mainCode 'typeof(App).Assembly.GetName().Version?.ToString(3)' 'Native visible versions must derive from the canonical assembly version.'
Assert-Contains $appProject '<Version>$(PeerOnQWindowsClientVersion)</Version>' 'Native assembly version must remain canonical.'
$installerProject = Read-RepositoryFile 'installer\PeerOnQ.Installer.wixproj'
Assert-Contains $installerProject '>$(PeerOnQWindowsClientVersion)</ProductVersion>' 'Installer default must use the canonical client version.'
Assert-Contains $installerProject 'Name="ValidateCanonicalClientVersion"' 'Installer must reject an explicit version mismatch.'
if ($mainXaml -match 'Text="v?\d+\.\d+\.\d+(?:\.\d+)?"') { throw 'Native XAML must not hardcode a client version.' }
foreach ($previewPath in @('artifacts\peeronq\src\pages\DashboardPage.tsx', 'artifacts\peeronq\src\pages\DevicesPage.tsx', 'artifacts\peeronq\src\components\Sidebar.tsx')) {
    $previewSource = Read-RepositoryFile $previewPath
    if ($previewSource -match '(?:>|appVersion:\s*")v?\d+\.\d+\.\d+(?:<|")') { throw "Preview must not hardcode a client version: $previewPath" }
}
foreach ($label in @('View Only', 'Full Control', 'File Transfer', 'Unattended Access', 'Remote Device ID', 'Verified Updates')) {
    Assert-Contains $mainXaml $label "Canonical native terminology is missing: $label"
}
Assert-NotContains $mainCode 'OnClipboardToggled' 'The removed clipboard UI handler is still present.'
Assert-NotContains $mainCode 'OnSaveSignalingServer' 'The removed Connection card still has a mutable endpoint handler.'
Assert-Contains $mainCode 'CheckUpdateButton.IsEnabled = _services.Updates is not null;' 'Trusted client update configuration does not enable the Settings action.'
Assert-Contains $mainCode 'if (services.IsLanDevelopmentClient)' 'The 4K startup override must be limited to LAN development clients.'
Assert-Contains $mainCode 'nameof(QualityProfile.Quality)' 'LAN development must select the adaptive high-quality profile.'
Assert-Contains $mainCode 'nameof(CaptureResolution.P2160)' 'LAN development must explicitly request 4K without upscaling the source.'
Assert-Contains $mainCode 'ResolutionPicker.SelectedItem is not ComboBoxItem' 'The resolution selector is not applied to session setup.'
Assert-Contains $mainCode 'HeaderConnectionText.Text = isConnected ? "Connected" : "Disconnected";' 'Header signaling status is not limited to Connected or Disconnected.'
Assert-Contains $mainCode "Windows build does not provide the ML-KEM/ML-DSA security required by PeerOnQ." 'Post-quantum capability mismatch has no actionable user-facing recovery message.'
Assert-Contains $mainCode 'PeerOnQ will not downgrade session encryption.' 'Capability recovery guidance weakens the mandatory encryption gate.'
Assert-Contains $mainCode 'PeerOnQId.FormatMaskedDisplay(entry.PeerMaskedId)' 'Session History still exposes the protocol identifier prefix.'
Assert-NotContains $mainCode ': entry.PeerMaskedId;' 'Session History still falls back to the protocol identifier prefix.'
Assert-Contains $mainCode 'fileTransfers: _services?.Coordinator.CollaborationFor(info.SessionId)?.FileTransfers' 'Viewer is not connected to the authorized file-transfer service.'
Assert-Contains $mainCode 'AttachRemoteInput(context.RemoteInput);' 'Viewer does not receive the remote-input channel when collaboration arrives late.'
Assert-Contains $mainCode 'AppWindow.Hide();' 'Main window remains visible behind an active remote viewer.'
Assert-Contains $mainCode 'onClosed: OnViewerClosed' 'Closing the remote viewer cannot restore the main window deterministically.'
Assert-Contains $mainXaml 'x:Name="SavedDevicePicker"' 'Dashboard quick connect has no saved-device selector.'
Assert-Contains $mainXaml 'SelectionChanged="OnSavedDeviceSelectionChanged"' 'Saved-device selection is not wired to the connection card.'
Assert-Contains $mainCode 'idBox.TextChanged += OnRemoteIdTextChanged;' 'Add Device does not apply automatic PeerOnQ ID grouping.'
Assert-Contains $mainCode 'RemoteIdBox.Text = device.DeviceId.Display;' 'Saved-device selection does not populate the remote ID.'
Assert-Contains $mainCode 'SessionAccessKind.Unattended, password' 'Dashboard unattended selection is not wired to the real request scope.'
Assert-Contains $mainCode 'GetStatusAsync()' 'Security does not refresh the authoritative unattended state.'
Assert-Contains $mainCode 'ConfirmUnattendedModeChangeAsync' 'Password access does not offer a safe recovery when the remote mode is narrower.'
Assert-Contains $mainCode 'Password access was declined. Check the password' 'Password rejection has no actionable recovery message.'

Assert-EventHandlers 'src\PeerOnQ.App\MainWindow.xaml' 'src\PeerOnQ.App\MainWindow.xaml.cs'
Assert-EventHandlers 'src\PeerOnQ.App\ViewerWindow.xaml' 'src\PeerOnQ.App\ViewerWindow.xaml.cs'
Assert-EventHandlers 'src\PeerOnQ.App\SharingIndicatorWindow.xaml' 'src\PeerOnQ.App\SharingIndicatorWindow.xaml.cs'

if ($router -match '/app/billing') {
    throw 'The active public/account UI still presents a commercial billing or subscription surface.'
}
foreach ($disclosure in @(
    'open-source software released under the MIT License',
    'does not require a',
    'license key',
    'product activation',
    'paid subscription',
    'online entitlement check')) {
    Assert-Contains $landingPage $disclosure "Public landing-page disclosure is missing: $disclosure"
}

Write-Host 'Phase 5 native UI/accessibility/open-source validation passed.'
