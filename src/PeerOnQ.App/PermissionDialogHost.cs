using PeerOnQ.Application.Abstractions;
using PeerOnQ.Domain.Sessions;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

namespace PeerOnQ.App;

/// <summary>
/// Shows the incoming-session dialog on the UI thread and returns the user's answer.
///
/// Rules this class enforces:
///   * the dialog always names the requester and shows a masked PeerOnQ ID
///   * every requested permission and the local time are always visible
///   * no answer within the timeout counts as a decline, never as an accept
/// </summary>
public sealed class PermissionDialogHost(DispatcherQueue dispatcher, Func<XamlRoot?> xamlRoot) : IPermissionPrompt
{
    private ResourceDictionary? _activeThemeResources;

    public ILogger<PermissionDialogHost>? Logger { get; set; }

    public async Task<PermissionDecision> AskAsync(
        PermissionRequest request,
        CancellationToken cancellationToken = default) =>
        (await AskForScopeAsync(request, cancellationToken)).Decision;

    public Task<PermissionPromptResult> AskForScopeAsync(
        PermissionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!Phase1SessionScope.IsAllowed(
                request.RequestedMode,
                request.RequestedPermissions,
                request.AccessKind))
        {
            Logger?.LogWarning("Permission dialog rejected an invalid requested scope");
            return Task.FromResult(new PermissionPromptResult(PermissionDecision.Decline));
        }

        var completion = new TaskCompletionSource<PermissionPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        var queued = dispatcher.TryEnqueue(async void () =>
        {
            try
            {
                var decision = await ShowAsync(request, cancellationToken);
                completion.TrySetResult(decision);
            }
            catch (Exception ex)
            {
                // Anything unexpected must fall back to the safe answer.
                Logger?.LogWarning(
                    ex,
                    "Permission dialog failed closed ({RootErrorType})",
                    ex.GetBaseException().GetType().FullName);
                completion.TrySetResult(new PermissionPromptResult(PermissionDecision.Decline));
            }
        });

        if (!queued)
        {
            Logger?.LogWarning("Permission dialog could not be queued on the UI thread");
            completion.TrySetResult(new PermissionPromptResult(PermissionDecision.Decline));
        }

        return completion.Task;
    }

    private async Task<PermissionPromptResult> ShowAsync(PermissionRequest request, CancellationToken cancellationToken)
    {
        var root = xamlRoot();
        if (root is null)
        {
            Logger?.LogWarning("Permission dialog has no active XAML root");
            return new PermissionPromptResult(PermissionDecision.Decline);
        }
        _activeThemeResources = ResolveThemeResources(root);

        var countdown = new TextBlock
        {
            Text = FormatRemaining(request.Timeout),
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = Resource<Brush>("PeerOnQTrustBrush"),
        };

        var body = new StackPanel
        {
            Spacing = 10,
            MinWidth = 400,
            MaxWidth = 440,
        };
        body.Children.Add(CreateRequesterCard(request));
        if (request.AccessKind == SessionAccessKind.SupportInvitation)
        {
            body.Children.Add(CreateSupportInvitationNotice(request.SupportNote));
        }

        RadioButtons? accessChoices = null;
        Border? viewOnlyChoice = null;
        Border? fullControlChoice = null;
        if (CanChooseRemoteScope(request))
        {
            body.Children.Add(new TextBlock
            {
                Text = "Choose access",
                FontSize = 14,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(0, 2, 0, -4),
            });
            // Require an explicit host choice. The incoming request is a Full Control envelope,
            // but silently preselecting View Only made the resulting session look like broken
            // input to both people. RadioButtons creates the accessible radio containers for
            // these content items and keeps SelectedIndex authoritative.
            accessChoices = new RadioButtons { SelectedIndex = -1 };
            AutomationProperties.SetName(accessChoices, "Access level for this connection");
            viewOnlyChoice = CreateAccessChoice(
                "\uE890",
                "View only",
                "Screen only",
                "PeerOnQTrustBrush");
            fullControlChoice = CreateAccessChoice(
                "\uE765",
                "Full control",
                "Screen  \u2022  Mouse & keyboard  \u2022  Files",
                "PeerOnQAccentBrush");
            accessChoices.Items.Add(viewOnlyChoice);
            accessChoices.Items.Add(fullControlChoice);
            body.Children.Add(accessChoices);
        }
        else
        {
            body.Children.Add(CreateFixedScopeCard(request));
        }

        body.Children.Add(CreateFooter(request.RequestedAt, countdown));

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Incoming connection",
            Content = new ScrollViewer
            {
                Content = body,
                MaxHeight = 480,
                HorizontalScrollMode = ScrollMode.Disabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollMode = ScrollMode.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            },
            PrimaryButtonText = accessChoices is null ? "Accept" : "Select access",
            IsPrimaryButtonEnabled = accessChoices is null,
            SecondaryButtonText = "Decline",
            CloseButtonText = "Block",
            DefaultButton = ContentDialogButton.Secondary,
            PrimaryButtonStyle = Resource<Style>("PeerOnQDialogPrimaryButtonStyle"),
        };
        if (accessChoices is not null)
        {
            accessChoices.SelectionChanged += (_, _) =>
            {
                dialog.IsPrimaryButtonEnabled = accessChoices.SelectedIndex is 0 or 1;
                dialog.PrimaryButtonText = accessChoices.SelectedIndex switch
                {
                    0 => "Allow View only",
                    1 => "Allow Full control",
                    _ => "Select access",
                };
                SetChoiceSelected(
                    viewOnlyChoice!,
                    accessChoices.SelectedIndex == 0,
                    "PeerOnQTrustSubtleBrush",
                    "PeerOnQTrustBorderBrush");
                SetChoiceSelected(
                    fullControlChoice!,
                    accessChoices.SelectedIndex == 1,
                    "PeerOnQSuccessSubtleBrush",
                    "PeerOnQSuccessBorderBrush");
            };
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.Timeout);

        var deadline = DateTimeOffset.UtcNow + request.Timeout;
        var ticker = dispatcher.CreateTimer();
        ticker.Interval = TimeSpan.FromMilliseconds(250);
        ticker.IsRepeating = true;
        ticker.Tick += (_, _) =>
        {
            var remaining = deadline - DateTimeOffset.UtcNow;
            countdown.Text = FormatRemaining(remaining);
        };

        using var registration = timeout.Token.Register(() => dispatcher.TryEnqueue(dialog.Hide));

        ContentDialogResult result;
        ticker.Start();
        try
        {
            result = await dialog.ShowAsync();
        }
        finally
        {
            ticker.Stop();
        }

        var timedOut = timeout.IsCancellationRequested;
        Logger?.LogInformation(
            "Permission dialog resolved with {DialogResult}; timed out: {TimedOut}",
            result,
            timedOut);

        if (timedOut)
        {
            return new PermissionPromptResult(PermissionDecision.Timeout);
        }

        return result switch
        {
            ContentDialogResult.Primary when accessChoices is not null && accessChoices.SelectedIndex == 1 =>
                new PermissionPromptResult(
                    PermissionDecision.Accept,
                    SessionMode.FullControl,
                    Phase1SessionScope.FullControlPermissions),
            ContentDialogResult.Primary when accessChoices is null =>
                PermissionPromptResult.ForRequestedScope(PermissionDecision.Accept, request),
            ContentDialogResult.Primary when accessChoices.SelectedIndex == 0 => new PermissionPromptResult(
                PermissionDecision.Accept,
                SessionMode.ViewOnly,
                SessionPermission.ViewScreen),
            ContentDialogResult.Primary => new PermissionPromptResult(PermissionDecision.Decline),
            ContentDialogResult.Secondary => new PermissionPromptResult(PermissionDecision.Decline),
            _ => new PermissionPromptResult(PermissionDecision.Block),
        };
    }

    private static bool CanChooseRemoteScope(PermissionRequest request) =>
        request.RemoteScopeSelectionRequired
        && request.AccessKind == SessionAccessKind.Attended
        && request.RequestedMode == SessionMode.FullControl
        && request.RequestedPermissions == Phase1SessionScope.FullControlPermissions;

    private Border CreateRequesterCard(PermissionRequest request)
    {
        var copy = new StackPanel { Spacing = 2 };
        copy.Children.Add(new TextBlock
        {
            Text = "REQUEST FROM",
            FontSize = 11,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = Resource<Brush>("PeerOnQMutedTextBrush"),
        });
        copy.Children.Add(new TextBlock
        {
            Text = request.RequesterDisplayName,
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        copy.Children.Add(new TextBlock
        {
            Text = request.MaskedRequesterId,
            FontFamily = new FontFamily("Consolas"),
            Foreground = Resource<Brush>("PeerOnQMutedTextBrush"),
        });

        return new Border
        {
            Background = Resource<Brush>("PeerOnQSurfaceBrush"),
            BorderBrush = Resource<Brush>("PeerOnQBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12),
            Child = CreateTwoColumnLayout(
                CreateIconTile("\uE77B", "PeerOnQTrustSubtleBrush", "PeerOnQTrustBrush", 36),
                copy),
        };
    }

    private Border CreateSupportInvitationNotice(string? supportNote)
    {
        var description = string.IsNullOrWhiteSpace(supportNote)
            ? "Verified support invitation. Your explicit approval is still required."
            : $"Verified support invitation. Note: {supportNote}";
        return CreateCallout(
            "\uE8D7",
            "Support invitation",
            description,
            "PeerOnQTrustSubtleBrush",
            "PeerOnQTrustBorderBrush",
            "PeerOnQTrustBrush");
    }

    private Border CreateAccessChoice(
        string glyph,
        string title,
        string scope,
        string accentKey)
    {
        var grid = new Grid { ColumnSpacing = 10, MinWidth = 340 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = CreateIconTile(glyph, "PeerOnQSubtleBrush", accentKey, 34);
        grid.Children.Add(icon);

        var copy = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        copy.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        copy.Children.Add(new TextBlock
        {
            Text = scope,
            FontSize = 12,
            Foreground = Resource<Brush>("PeerOnQMutedTextBrush"),
            TextWrapping = TextWrapping.Wrap,
        });
        Grid.SetColumn(copy, 1);
        grid.Children.Add(copy);

        var choice = new Border
        {
            Background = Resource<Brush>("PeerOnQSurfaceBrush"),
            BorderBrush = Resource<Brush>("PeerOnQBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10),
            MinHeight = 60,
            Child = grid,
        };
        AutomationProperties.SetName(choice, $"{title}. {scope}");
        return choice;
    }

    private void SetChoiceSelected(
        Border choice,
        bool selected,
        string selectedBackgroundKey,
        string selectedBorderKey)
    {
        choice.Background = Resource<Brush>(selected ? selectedBackgroundKey : "PeerOnQSurfaceBrush");
        choice.BorderBrush = Resource<Brush>(selected ? selectedBorderKey : "PeerOnQBorderBrush");
    }

    private Border CreateFixedScopeCard(PermissionRequest request) =>
        CreateCallout(
            "\uE713",
            Describe(request.RequestedMode, request.RequestedPermissions),
            $"Requested permissions: {request.RequestedPermissions}",
            "PeerOnQSubtleBrush",
            "PeerOnQBorderBrush",
            "PeerOnQAccentBrush");

    private Grid CreateFooter(DateTimeOffset requestedAt, TextBlock countdown)
    {
        var footer = new Grid { ColumnSpacing = 12 };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.Children.Add(new TextBlock
        {
            Text = $"Requested at {requestedAt.ToLocalTime():t}",
            Foreground = Resource<Brush>("PeerOnQMutedTextBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        });

        var countdownPill = new Border
        {
            Background = Resource<Brush>("PeerOnQTrustSubtleBrush"),
            BorderBrush = Resource<Brush>("PeerOnQTrustBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 4, 8, 4),
            Child = countdown,
        };
        Grid.SetColumn(countdownPill, 1);
        footer.Children.Add(countdownPill);
        return footer;
    }

    private Border CreateCallout(
        string glyph,
        string title,
        string description,
        string backgroundKey,
        string borderKey,
        string accentKey)
    {
        var copy = new StackPanel { Spacing = 2 };
        copy.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        copy.Children.Add(new TextBlock
        {
            Text = description,
            Foreground = Resource<Brush>("PeerOnQMutedTextBrush"),
            TextWrapping = TextWrapping.Wrap,
        });

        return new Border
        {
            Background = Resource<Brush>(backgroundKey),
            BorderBrush = Resource<Brush>(borderKey),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12),
            Child = CreateTwoColumnLayout(CreateIconTile(glyph, "PeerOnQSurfaceBrush", accentKey, 36), copy),
        };
    }

    private static Grid CreateTwoColumnLayout(FrameworkElement leading, FrameworkElement content)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(leading);
        Grid.SetColumn(content, 1);
        grid.Children.Add(content);
        return grid;
    }

    private Border CreateIconTile(string glyph, string backgroundKey, string foregroundKey, double size = 42) =>
        new()
        {
            Width = size,
            Height = size,
            Background = Resource<Brush>(backgroundKey),
            CornerRadius = new CornerRadius(10),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe Fluent Icons"),
                FontSize = size >= 42 ? 20 : 17,
                Foreground = Resource<Brush>(foregroundKey),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

    private static ResourceDictionary ResolveThemeResources(XamlRoot root)
    {
        var resources = Microsoft.UI.Xaml.Application.Current.Resources;
        var highContrast = new AccessibilitySettings().HighContrast;
        var actualTheme = (root.Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Light;
        var themeKey = highContrast
            ? "HighContrast"
            : actualTheme == ElementTheme.Dark ? "Dark" : "Light";
        return resources.ThemeDictionaries[themeKey] as ResourceDictionary
            ?? throw new InvalidOperationException($"Missing PeerOnQ theme dictionary: {themeKey}");
    }

    private T Resource<T>(string key) where T : class
    {
        var resources = Microsoft.UI.Xaml.Application.Current.Resources;
        if (resources.ContainsKey(key) && resources[key] is T directResource)
        {
            return directResource;
        }
        if (_activeThemeResources is not null
            && _activeThemeResources.ContainsKey(key)
            && _activeThemeResources[key] is T themedResource)
        {
            return themedResource;
        }
        throw new InvalidOperationException($"Missing PeerOnQ UI resource: {key}");
    }

    private static string Describe(SessionMode mode, SessionPermission permissions) => mode switch
    {
        SessionMode.ViewOnly => "View only (no mouse or keyboard control)",
        SessionMode.FullControl when permissions.HasFlag(SessionPermission.FileTransfer) =>
            "Full control (screen, input, and file transfer)",
        SessionMode.FullControl => "Full control (screen and input)",
        SessionMode.FileTransferOnly => "File transfer only (no screen or input)",
        SessionMode.Custom => "Custom",
        _ => mode.ToString(),
    };

    private static string FormatRemaining(TimeSpan remaining) =>
        $"Auto-decline in {Math.Max(0, (int)remaining.TotalSeconds)}s";
}
