using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using AssetScope.App.Services;
using AssetScope.Core;
using AssetScope.Core.Api;
using AssetScope.Core.Diagnostics;

namespace AssetScope.App.Pages;

/// <summary>
/// First-run guide. Plaid and SnapTrade are independent optional paths;
/// a manual balance or skip also lets the user into the app.
/// </summary>
public sealed partial class SetupView : UserControl
{
    private readonly AppSession _session = AppSession.Current;
    private readonly OnboardingFlow _flow = new();

    private readonly TextBlock _title = new()
    {
        Style = (Style)Application.Current.Resources["PageTitleStyle"],
        TextWrapping = TextWrapping.Wrap,
    };
    private readonly TextBlock _stepText = new() { FontSize = 13, Opacity = 0.7 };
    private readonly StackPanel _body = new() { Spacing = 14 };
    private readonly InfoBar _error = new()
    {
        IsOpen = false,
        Severity = InfoBarSeverity.Error,
        IsClosable = true,
    };
    private readonly Button _primary = new()
    {
        Style = (Style)Application.Current.Resources["AccentButtonStyle"],
        MinWidth = 160,
    };
    private readonly Button _back = new();
    private readonly Button _cancel = new();

    private readonly TextBox _clientIdBox = new() { MaxLength = 64 };
    private readonly PasswordBox _secretBox = new() { MaxLength = 128 };
    private readonly TextBox _snapClientIdBox = new() { MaxLength = 128 };
    private readonly PasswordBox _snapConsumerKeyBox = new() { MaxLength = 256 };
    private readonly TextBlock _captureStatus = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _connectStatus = new() { TextWrapping = TextWrapping.Wrap, FontSize = 14 };
    private readonly ProgressRing _connectRing = new() { Width = 22, Height = 22, IsActive = false };
    private readonly Button _openAgainButton = new() { Visibility = Visibility.Collapsed };

    private string? _connectedInstitutionSummary;
    private int _failedInstitutionCount;
    private bool _manualAccountAdded;
    private bool _brokerageConnected;
    private bool _startingAutomatic;
    private bool _linkRunning;
    private bool _submitting;
    private bool _capturing;
    private CancellationTokenSource? _pollCts;

    private static readonly System.Text.RegularExpressions.Regex ClientIdShape =
        new("^[0-9a-f]{24}$", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex SecretShape =
        new("^[0-9a-f]{30}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>When false (first-run gate), setup must produce at least one account.</summary>
    public bool AllowCancel { get; set; }

    /// <summary>Used by an explicit “connect bank” command to skip the method chooser.</summary>
    public bool StartAutomatic { get; set; }

    /// <summary>Raised when an account exists, or the user cancels an optional setup.</summary>
    public event EventHandler? Finished;

    public SetupView()
    {
        AutomationProperties.SetAutomationId(this, "SetupView");
        AutomationProperties.SetAutomationId(_title, "SetupTitle");
        AutomationProperties.SetAutomationId(_error, "SetupError");
        AutomationProperties.SetAutomationId(_primary, "SetupPrimaryButton");
        AutomationProperties.SetAutomationId(_back, "SetupBackButton");
        AutomationProperties.SetAutomationId(_cancel, "SetupCancelButton");
        AutomationProperties.SetAutomationId(_clientIdBox, "PlaidClientIdInput");
        AutomationProperties.SetAutomationId(_secretBox, "PlaidSecretInput");
        AutomationProperties.SetAutomationId(_connectStatus, "BankConnectionStatus");
        AutomationProperties.SetAutomationId(_openAgainButton, "OpenBankConnectionAgainButton");

        _clientIdBox.Header = Display.S("PlaidClientIdBox/Header");
        _clientIdBox.Text = _session.Settings.PlaidClientId ?? "";
        _secretBox.Header = Display.S("PlaidSecretBox/Header");
        _snapClientIdBox.Header = Display.S("SnapTradeClientIdBox/Header");
        _snapClientIdBox.Text = _session.Settings.SnapTradeClientId ?? "";
        _snapConsumerKeyBox.Header = Display.S("SnapTradeConsumerKeyBox/Header");
        AutomationProperties.SetAutomationId(_snapClientIdBox, "SetupSnapTradeClientIdInput");
        AutomationProperties.SetAutomationId(_snapConsumerKeyBox, "SetupSnapTradeConsumerKeyInput");
        _error.Margin = new Thickness(0, 0, 0, 4);

        _openAgainButton.Content = Display.S("WizardOpenAgain");
        _openAgainButton.Click += (_, _) =>
        {
            if (_flow.Step == OnboardingStep.BrokerageConnect) _ = OpenSnapTradePortalAsync();
            else _ = RunLinkAsync();
        };

        _primary.Click += OnPrimaryClick;
        _back.Content = Display.S("Back");
        _back.Click += OnBackClick;
        _cancel.Content = Display.S("Cancel");
        _cancel.Click += (_, _) => FinishOrCancel();

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Margin = new Thickness(0, 8, 0, 0),
        };
        footer.Children.Add(_primary);
        footer.Children.Add(_back);
        footer.Children.Add(_cancel);

        var card = new StackPanel
        {
            Spacing = 14,
            MaxWidth = 620,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        card.Children.Add(new FontIcon
        {
            Glyph = "",
            FontSize = 40,
            HorizontalAlignment = HorizontalAlignment.Left,
            Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"],
        });
        card.Children.Add(_title);
        card.Children.Add(_stepText);
        card.Children.Add(_error);
        card.Children.Add(_body);
        card.Children.Add(footer);

        Content = new ScrollViewer
        {
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            Padding = new Thickness(40, 32, 40, 40),
            Content = card,
        };

        Loaded += async (_, _) =>
        {
            AppLog.Breadcrumb("Setup", $"View loaded; startAutomatic={StartAutomatic}.");
            if (StartAutomatic) await BeginAutomaticSyncAsync();
            else RenderStep();
        };
        Unloaded += (_, _) =>
        {
            StopClipboardCapture();
            _pollCts?.Cancel();
        };
    }

    private void RenderStep()
    {
        AppLog.Breadcrumb("Setup", $"Rendering step {_flow.Step}.");
        _body.Children.Clear();
        _error.IsOpen = false;
        _stepText.Visibility = Visibility.Visible;
        _primary.Visibility = Visibility.Visible;
        _back.Visibility = Visibility.Collapsed;
        _cancel.Visibility = AllowCancel && _flow.Step != OnboardingStep.Done
            ? Visibility.Visible
            : Visibility.Collapsed;

        switch (_flow.Step)
        {
            case OnboardingStep.ChooseMethod:
                RenderMethodChoice();
                break;

            case OnboardingStep.PlaidCredentials:
                RenderPlaidCredentials();
                break;

            case OnboardingStep.BankConnect:
                RenderBankConnect();
                break;

            case OnboardingStep.SnapTradeCredentials:
                RenderSnapTradeCredentials();
                break;

            case OnboardingStep.BrokerageConnect:
                RenderBrokerageConnect();
                break;

            case OnboardingStep.Done:
                RenderDone();
                break;
        }
    }

    private void RenderMethodChoice()
    {
        _title.Text = Display.S("SetupTitle");
        _stepText.Visibility = Visibility.Collapsed;
        _primary.Visibility = Visibility.Collapsed;
        _body.Children.Add(Wrap(Display.S("SetupIntro")));

        _body.Children.Add(ProviderChoice(
            "ChooseAutomaticSyncButton",
            "ms-appx:///Assets/Institutions/plaid.svg",
            Display.S("SetupPlaidAction"),
            Display.S("SetupPlaidHint"),
            (_, _) => _ = BeginAutomaticSyncAsync()));
        _body.Children.Add(ProviderChoice(
            "ChooseSnapTradeButton",
            "ms-appx:///Assets/Institutions/snaptrade.svg",
            Display.S("SetupSnapTradeAction"),
            Display.S("SetupSnapTradeHint"),
            (_, _) => _ = BeginSnapTradeAsync()));

        var manual = new Button
        {
            Content = Display.S("SetupManualAction"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            MinHeight = 44,
        };
        AutomationProperties.SetAutomationId(manual, "ChooseManualBalanceButton");
        manual.Click += async (_, _) => await AddManualAccountAsync();
        _body.Children.Add(manual);
        _body.Children.Add(Hint(Display.S("SetupManualHint")));

        var skip = new HyperlinkButton
        {
            Content = Display.S("SetupSkip"),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 4, 0, 0),
        };
        AutomationProperties.SetAutomationId(skip, "SkipSetupButton");
        skip.Click += (_, _) => SkipSetup();
        _body.Children.Add(skip);
        _body.Children.Add(Hint(Display.S("SetupSkipHint")));
    }

    private Button ProviderChoice(
        string automationId,
        string logo,
        string title,
        string hint,
        RoutedEventHandler click)
    {
        var icon = new Border
        {
            Width = 36,
            Height = 36,
            CornerRadius = new CornerRadius(8),
            Background = (Brush)Application.Current.Resources["ReferenceNeutralTintBrush"],
            Child = new Image
            {
                Width = 26,
                Height = 26,
                Stretch = Stretch.Uniform,
                Source = new SvgImageSource(new Uri(logo)),
            },
        };
        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(new TextBlock { Text = title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        text.Children.Add(new TextBlock
        {
            Text = hint,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(icon);
        Grid.SetColumn(text, 1);
        row.Children.Add(text);

        var button = new Button
        {
            Content = row,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(14, 12, 14, 12),
        };
        AutomationProperties.SetAutomationId(button, automationId);
        AutomationProperties.SetName(button, title);
        button.Click += click;
        return button;
    }

    private void RenderPlaidCredentials()
    {
        _title.Text = Display.S("SetupAutomaticTitle");
        _stepText.Text = Display.S("SetupAutomaticStep");
        _primary.Content = Display.S("WizardNext");
        _primary.IsEnabled = true;
        _back.Visibility = Visibility.Visible;

        Detach(_clientIdBox);
        Detach(_secretBox);
        Detach(_captureStatus);
        _body.Children.Add(Wrap(Display.S("WizardCredentialsIntro")));
        var openDashboard = new Button { Content = Display.S("WizardOpenDashboard") };
        AutomationProperties.SetAutomationId(openDashboard, "OpenPlaidDashboardButton");
        openDashboard.Click += async (_, _) =>
        {
            StartClipboardCapture();
            await Windows.System.Launcher.LaunchUriAsync(new Uri("https://dashboard.plaid.com/developers/keys"));
        };
        _body.Children.Add(openDashboard);
        _captureStatus.Text = Display.S("WizardCaptureIdle");
        _body.Children.Add(_captureStatus);
        _body.Children.Add(_clientIdBox);
        _body.Children.Add(_secretBox);
        _body.Children.Add(Hint(Display.S("PlaidSecretHint/Text")));
    }

    private void RenderBankConnect()
    {
        _title.Text = Display.S("SetupAutomaticTitle");
        _stepText.Text = Display.S("WizardStepConnect");
        _primary.Visibility = Visibility.Collapsed;
        Detach(_connectRing);
        Detach(_connectStatus);
        Detach(_openAgainButton);
        var statusRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        statusRow.Children.Add(_connectRing);
        statusRow.Children.Add(_connectStatus);
        _body.Children.Add(statusRow);
        _body.Children.Add(_openAgainButton);
    }

    private void RenderSnapTradeCredentials()
    {
        _title.Text = Display.S("SetupSnapTradeTitle");
        _stepText.Text = Display.S("SetupSnapTradeStep");
        _primary.Content = Display.S("SaveSnapTrade/Content");
        _primary.IsEnabled = true;
        _back.Visibility = Visibility.Visible;

        _body.Children.Add(Wrap(Display.S("SnapTradeOnboardingBody/Text")));
        var links = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var signup = new HyperlinkButton { Content = Display.S("SnapTradeOpenSignup/Content") };
        signup.Click += async (_, _) =>
            await Windows.System.Launcher.LaunchUriAsync(new Uri("https://dashboard.snaptrade.com/signup"));
        var apiKey = new HyperlinkButton { Content = Display.S("SnapTradeOpenApiKey/Content") };
        apiKey.Click += async (_, _) =>
            await Windows.System.Launcher.LaunchUriAsync(new Uri("https://dashboard.snaptrade.com/api-key"));
        links.Children.Add(signup);
        links.Children.Add(apiKey);
        _body.Children.Add(links);
        _body.Children.Add(Hint(Display.S("SnapTradeCredentialsExplainer/Text")));
        Detach(_snapClientIdBox);
        Detach(_snapConsumerKeyBox);
        _body.Children.Add(_snapClientIdBox);
        _body.Children.Add(_snapConsumerKeyBox);
    }

    private void RenderBrokerageConnect()
    {
        _title.Text = Display.S("SetupSnapTradeTitle");
        _stepText.Text = Display.S("WizardStepConnect");
        _primary.Content = Display.S("SetupBrokerageContinue");
        _primary.IsEnabled = true;
        _back.Visibility = Visibility.Visible;
        Detach(_connectRing);
        Detach(_connectStatus);
        Detach(_openAgainButton);
        var statusRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        statusRow.Children.Add(_connectRing);
        statusRow.Children.Add(_connectStatus);
        _body.Children.Add(statusRow);
        _body.Children.Add(_openAgainButton);
    }

    private void RenderDone()
    {
        _title.Text = Display.S("SetupDoneTitle");
        _stepText.Visibility = Visibility.Collapsed;
        _primary.Content = Display.S("WizardGoToApp");
        _primary.IsEnabled = true;
        _body.Children.Add(new FontIcon
        {
            Glyph = "",
            FontSize = 32,
            HorizontalAlignment = HorizontalAlignment.Left,
            Foreground = (Brush)Application.Current.Resources["PositiveChangeBrush"],
        });
        var doneBody = _manualAccountAdded
            ? Display.S("SetupManualDoneBody")
            : _brokerageConnected
                ? Display.S("SetupSnapTradeDoneBody")
                : string.Format(Display.S("WizardDoneBody"), _connectedInstitutionSummary ?? "");
        _body.Children.Add(Wrap(doneBody));

        if (!_manualAccountAdded && !_brokerageConnected && _failedInstitutionCount > 0)
        {
            _body.Children.Add(new InfoBar
            {
                IsOpen = true,
                IsClosable = false,
                Severity = InfoBarSeverity.Warning,
                Message = string.Format(Display.S("LinkPartialFailure"), _failedInstitutionCount),
            });
        }

        var anotherManual = new Button { Content = Display.S("SetupAddAnotherManual") };
        AutomationProperties.SetAutomationId(anotherManual, "AddAnotherManualBalanceButton");
        anotherManual.Click += async (_, _) => await AddManualAccountAsync();
        _body.Children.Add(anotherManual);

        if (!_manualAccountAdded && !_brokerageConnected)
        {
            var anotherBank = new Button { Content = Display.S("WizardConnectAnother") };
            AutomationProperties.SetAutomationId(anotherBank, "ConnectAnotherBankButton");
            anotherBank.Click += (_, _) =>
            {
                _flow.ChooseAnotherAutomaticConnection();
                RenderStep();
                _ = RunLinkAsync();
            };
            _body.Children.Add(anotherBank);
        }

        if (_brokerageConnected)
        {
            var anotherBrokerage = new Button { Content = Display.S("SetupConnectAnotherBrokerage") };
            AutomationProperties.SetAutomationId(anotherBrokerage, "ConnectAnotherBrokerageButton");
            anotherBrokerage.Click += (_, _) =>
            {
                _flow.ChooseAnotherBrokerage();
                RenderStep();
                _ = OpenSnapTradePortalAsync();
            };
            _body.Children.Add(anotherBrokerage);
        }
    }

    private async Task AddManualAccountAsync()
    {
        if (_session.Api is null)
        {
            ShowError(_session.StartupError ?? Display.S("StartupFailed"));
            return;
        }

        var dialog = new AddManualAccountDialog { XamlRoot = XamlRoot };
        if (!await dialog.ShowAndCreateAsync(_session)) return;

        _manualAccountAdded = true;
        _brokerageConnected = false;
        DismissOnboarding();
        _flow.Complete();
        _session.NotifyDataRefreshed();
        RenderStep();
    }

    private async Task BeginAutomaticSyncAsync()
    {
        if (_startingAutomatic) return;
        if (_session.Api is null)
        {
            _flow.BackToMethods();
            RenderStep();
            ShowError(_session.StartupError ?? Display.S("StartupFailed"));
            return;
        }

        _startingAutomatic = true;
        try
        {
            var status = await _session.Api.GetPlaidConfigStatusAsync();
            _flow.ChoosePlaid(status.Configured);
            RenderStep();
            if (_flow.Step == OnboardingStep.BankConnect) await RunLinkAsync();
        }
        catch (ApiException ex)
        {
            _flow.BackToMethods();
            RenderStep();
            ShowError(ex.Message);
        }
        finally
        {
            _startingAutomatic = false;
        }
    }

    private static TextBlock Wrap(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        FontSize = 14,
    };

    private static TextBlock Hint(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        FontSize = 12,
        Margin = new Thickness(2, -6, 2, 4),
        Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
    };

    private async void OnPrimaryClick(object sender, RoutedEventArgs e)
    {
        AppLog.Breadcrumb("Setup", $"Primary action invoked on step {_flow.Step}.");
        switch (_flow.Step)
        {
            case OnboardingStep.PlaidCredentials:
                await SubmitCredentialsAsync();
                break;
            case OnboardingStep.SnapTradeCredentials:
                await SubmitSnapTradeCredentialsAsync();
                break;
            case OnboardingStep.BrokerageConnect:
                await CompleteSnapTradeSetupAsync();
                break;
            case OnboardingStep.Done:
                Finished?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        AppLog.Breadcrumb("Setup", "Back invoked.");
        StopClipboardCapture();
        _flow.BackToMethods();
        RenderStep();
    }

    private void FinishOrCancel()
    {
        AppLog.Breadcrumb("Setup", "Finish or cancel invoked.");
        StopClipboardCapture();
        _pollCts?.Cancel();
        Finished?.Invoke(this, EventArgs.Empty);
    }

    // Clipboard capture is a convenience only. Both fields remain ordinary,
    // accessible inputs and can always be pasted or typed directly.
    private void StartClipboardCapture()
    {
        if (_capturing) return;
        _capturing = true;
        Windows.ApplicationModel.DataTransfer.Clipboard.ContentChanged += OnClipboardChanged;
        _captureStatus.Text = Display.S("WizardCaptureWaiting");
    }

    private void StopClipboardCapture()
    {
        if (!_capturing) return;
        _capturing = false;
        Windows.ApplicationModel.DataTransfer.Clipboard.ContentChanged -= OnClipboardChanged;
    }

    private async void OnClipboardChanged(object? sender, object e)
    {
        if (!_capturing || _flow.Step != OnboardingStep.PlaidCredentials) return;
        string text;
        try
        {
            var content = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            if (!content.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text)) return;
            text = (await content.GetTextAsync()).Trim();
        }
        catch
        {
            return;
        }
        if (text.Length is < 20 or > 40) return;

        DispatcherQueue.TryEnqueue(() =>
        {
            var lower = text.ToLowerInvariant();
            if (ClientIdShape.IsMatch(lower))
            {
                _clientIdBox.Text = lower;
                _captureStatus.Text = Display.S("WizardCapturedClientId");
            }
            else if (SecretShape.IsMatch(lower))
            {
                _secretBox.Password = lower;
                _captureStatus.Text = Display.S("WizardCapturedSecret");
            }
            else
            {
                return;
            }

            if (_clientIdBox.Text.Length > 0 && _secretBox.Password.Length > 0 && !_submitting)
            {
                _captureStatus.Text = Display.S("WizardCapturedBoth");
                _ = SubmitCredentialsAsync();
            }
        });
    }

    private async Task SubmitCredentialsAsync()
    {
        if (_submitting) return;
        var environment = _session.Settings.PlaidEnvironment == "sandbox" ? "sandbox" : "production";
        var clientId = _clientIdBox.Text.Trim();
        var secret = _secretBox.Password;
        if (clientId.Length == 0 || secret.Length == 0)
        {
            ShowError(Display.S("PlaidMissingFields"));
            return;
        }

        _submitting = true;
        AppLog.Breadcrumb("Setup", $"Submitting Plaid credentials; environment={environment}.");
        _primary.IsEnabled = false;
        try
        {
            if (_session.State != SessionState.Ready)
            {
                ShowError(_session.StartupError ?? Display.S("StartupFailed"));
                return;
            }
            await _session.SavePlaidConfigAsync(environment, clientId, secret);
            AppLog.Info("Setup", "Plaid credentials accepted.");
            _secretBox.Password = "";
            StopClipboardCapture();
            _flow.PlaidCredentialsSaved();
            RenderStep();
            await RunLinkAsync();
        }
        catch (ApiException ex)
        {
            AppLog.Error("Setup", "Plaid credential submission failed.", ex);
            ShowError(ex.Message);
        }
        finally
        {
            _submitting = false;
            if (_flow.Step == OnboardingStep.PlaidCredentials) _primary.IsEnabled = true;
        }
    }

    private async Task RunLinkAsync()
    {
        if (_linkRunning || _session.Api is null) return;
        _linkRunning = true;
        AppLog.Breadcrumb("Setup", "Creating Plaid Hosted Link session.");
        _openAgainButton.Visibility = Visibility.Collapsed;
        _connectRing.IsActive = true;
        _pollCts = new CancellationTokenSource();
        try
        {
            var linkSession = await _session.Api.CreateLinkSessionAsync();
            AppLog.Breadcrumb("Setup", $"Hosted Link session created; sessionId={linkSession.Id}.");
            _connectStatus.Text = Display.S("LinkWaiting");
            var launched = await Windows.System.Launcher.LaunchUriAsync(new Uri(linkSession.Url));
            AppLog.Breadcrumb("Setup", $"Hosted Link launch returned {launched}.");

            for (var i = 0; i < 900 && !_pollCts.IsCancellationRequested; i++)
            {
                await Task.Delay(1000, _pollCts.Token);
                linkSession = await _session.Api.GetLinkSessionAsync(linkSession.Id, _pollCts.Token);
                if (linkSession.State != "waitingForUser") break;
            }

            switch (linkSession.State)
            {
                case "completed":
                    var summary = PlaidLinkPresentation.InstitutionSummary(linkSession);
                    AppLog.Info("Setup", $"Plaid Link completed with {linkSession.ConnectedItems.Count} item(s). ");
                    _connectedInstitutionSummary = summary;
                    _failedInstitutionCount = linkSession.FailedItemCount;
                    _manualAccountAdded = false;
                    _brokerageConnected = false;
                    DismissOnboarding();
                    _session.NotifyDataRefreshed();
                    _flow.Complete();
                    RenderStep();
                    break;
                case "expired":
                    AppLog.Warn("Setup", "Hosted Link session expired.");
                    _connectStatus.Text = Display.S("LinkExpired");
                    _openAgainButton.Visibility = Visibility.Visible;
                    break;
                case "cancelled":
                    AppLog.Warn("Setup", "Hosted Link session was cancelled.");
                    _connectStatus.Text = Display.S("LinkCancelled");
                    _openAgainButton.Visibility = Visibility.Visible;
                    break;
                default:
                    AppLog.Error("Setup", $"Hosted Link session ended in state {linkSession.State}: {linkSession.Error ?? "no detail"}.");
                    _connectStatus.Text = string.Format(Display.S("LinkFailed"), linkSession.Error ?? "");
                    _openAgainButton.Visibility = Visibility.Visible;
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            AppLog.Breadcrumb("Setup", "Hosted Link polling cancelled.");
        }
        catch (ApiException ex)
        {
            AppLog.Error("Setup", "Hosted Link flow failed.", ex);
            _connectStatus.Text = ex.Message;
            _openAgainButton.Visibility = Visibility.Visible;
        }
        finally
        {
            AppLog.Breadcrumb("Setup", "Hosted Link flow leaving active state.");
            _connectRing.IsActive = false;
            _linkRunning = false;
        }
    }

    private async Task BeginSnapTradeAsync()
    {
        if (_startingAutomatic) return;
        if (_session.Api is null)
        {
            _flow.BackToMethods();
            RenderStep();
            ShowError(_session.StartupError ?? Display.S("StartupFailed"));
            return;
        }

        _startingAutomatic = true;
        try
        {
            var status = await _session.Api.GetSnapTradeConfigStatusAsync();
            _flow.ChooseSnapTrade(status.Configured);
            RenderStep();
            if (_flow.Step == OnboardingStep.BrokerageConnect) await OpenSnapTradePortalAsync();
        }
        catch (ApiException ex)
        {
            _flow.BackToMethods();
            RenderStep();
            ShowError(ex.Message);
        }
        finally
        {
            _startingAutomatic = false;
        }
    }

    private async Task SubmitSnapTradeCredentialsAsync()
    {
        if (_submitting) return;
        var clientId = _snapClientIdBox.Text.Trim();
        var consumerKey = _snapConsumerKeyBox.Password;
        if (clientId.Length == 0 || consumerKey.Length == 0)
        {
            ShowError(Display.S("SnapTradeMissingFields"));
            return;
        }

        _submitting = true;
        _primary.IsEnabled = false;
        try
        {
            if (_session.State != SessionState.Ready)
            {
                ShowError(_session.StartupError ?? Display.S("StartupFailed"));
                return;
            }
            await _session.SaveSnapTradeConfigAsync(clientId, consumerKey);
            _snapConsumerKeyBox.Password = "";
            _flow.SnapTradeCredentialsSaved();
            RenderStep();
            await OpenSnapTradePortalAsync();
        }
        catch (ApiException ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            _submitting = false;
            if (_flow.Step == OnboardingStep.SnapTradeCredentials) _primary.IsEnabled = true;
        }
    }

    private async Task OpenSnapTradePortalAsync()
    {
        if (_linkRunning || _session.Api is null) return;
        _linkRunning = true;
        _openAgainButton.Visibility = Visibility.Collapsed;
        _connectRing.IsActive = true;
        _connectStatus.Text = Display.S("SetupBrokerageWaiting");
        try
        {
            var portal = await _session.Api.CreateSnapTradePortalAsync();
            var launched = await Windows.System.Launcher.LaunchUriAsync(new Uri(portal.Url));
            if (!launched) throw new InvalidOperationException(Display.S("SnapTradePortalLaunchFailed"));
            _openAgainButton.Visibility = Visibility.Visible;
        }
        catch (Exception ex) when (ex is ApiException or InvalidOperationException)
        {
            _connectStatus.Text = ex.Message;
            _openAgainButton.Visibility = Visibility.Visible;
        }
        finally
        {
            _connectRing.IsActive = false;
            _linkRunning = false;
        }
    }

    private async Task CompleteSnapTradeSetupAsync()
    {
        if (_submitting || _session.Api is null) return;
        _submitting = true;
        _primary.IsEnabled = false;
        _connectRing.IsActive = true;
        try
        {
            var run = await _session.Api.StartInvestmentSyncAsync("portal-return");
            for (var i = 0; i < 600 && run.Status == "running"; i++)
            {
                await Task.Delay(1000);
                run = (await _session.Api.GetInvestmentSyncStatusAsync()).Run ?? run;
            }
            if (run.Status == "failed")
            {
                ShowError(string.Format(Display.S("SnapTradeSyncFailed"),
                    run.ErrorCode ?? Display.S("UnknownError")));
                return;
            }
            _session.Bootstrap = await _session.Api.GetBootstrapAsync();
            _session.LastSyncedAt = _session.Bootstrap.Sync.InvestmentLastSuccessAt
                ?? _session.Bootstrap.Sync.LastSuccessAt;
            _manualAccountAdded = false;
            _brokerageConnected = true;
            DismissOnboarding();
            _session.NotifyDataRefreshed();
            _flow.Complete();
            RenderStep();
        }
        catch (ApiException ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            _submitting = false;
            _connectRing.IsActive = false;
            if (_flow.Step == OnboardingStep.BrokerageConnect) _primary.IsEnabled = true;
        }
    }

    private void SkipSetup()
    {
        DismissOnboarding();
        FinishOrCancel();
    }

    private void DismissOnboarding()
    {
        _session.Settings.OnboardingDismissed = true;
        _session.Settings.Save();
    }

    private static void Detach(UIElement element)
    {
        if (element is FrameworkElement { Parent: Panel panel })
            panel.Children.Remove(element);
    }

    private void ShowError(string message)
    {
        _error.Message = message;
        _error.IsOpen = true;
    }
}
