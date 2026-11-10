using AssetScope.App.Controls;
using AssetScope.App.Services;
using AssetScope.Core.Api;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AssetScope.App.Pages;

internal enum AddAccountChoice
{
    None,
    ConnectNewInstitution,
    AddManualBalance,
    ManageConnections,
}

/// <summary>
/// Keeps the user's local connection state separate from Plaid's institution
/// picker. Plaid may remember a phone number or prior institutions, but this
/// dialog is the authoritative list of Items already stored by this app.
/// </summary>
internal sealed class AddAccountChoiceDialog : ContentDialog
{
    public AddAccountChoice Selection { get; private set; }

    public AddAccountChoiceDialog(IReadOnlyList<PlaidItemDto> items, string? loadError = null)
    {
        Title = Display.S("AddAccount");
        CloseButtonText = Display.S("Cancel");
        DefaultButton = ContentDialogButton.None;
        AutomationProperties.SetAutomationId(this, "AddAccountChoiceDialog");

        var content = new StackPanel { Spacing = 14, MinWidth = 420 };
        content.Children.Add(new TextBlock
        {
            Text = Display.S("AddAccountChoiceBody"),
            TextWrapping = TextWrapping.Wrap,
            Foreground = ResourceBrush("TextFillColorSecondaryBrush"),
        });

        if (!string.IsNullOrWhiteSpace(loadError))
        {
            var warning = new InfoBar
            {
                IsOpen = true,
                IsClosable = false,
                Severity = InfoBarSeverity.Warning,
                Message = Display.S("ConnectedInstitutionsUnavailable"),
            };
            AutomationProperties.SetAutomationId(warning, "ConnectedInstitutionsLoadWarning");
            content.Children.Add(warning);
        }
        else if (items.Count > 0)
        {
            content.Children.Add(new TextBlock
            {
                Text = Display.S("AlreadyConnectedHeader"),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(0, 2, 0, -4),
            });

            var list = new ListView
            {
                SelectionMode = ListViewSelectionMode.None,
                IsItemClickEnabled = false,
                MaxHeight = 210,
            };
            AutomationProperties.SetAutomationId(list, "AlreadyConnectedInstitutionsList");
            foreach (var item in items)
            {
                var name = item.InstitutionName ?? Display.S("UnknownInstitution");
                var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(4, 7, 4, 7) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var icon = new InstitutionLogo(item.InstitutionName, "bank", 34);
                row.Children.Add(icon);

                var labels = new StackPanel { Spacing = 2 };
                labels.Children.Add(new TextBlock
                {
                    Text = name,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
                labels.Children.Add(new TextBlock
                {
                    Text = $"{Display.S($"ItemStatus_{item.Status}")} · {string.Format(Display.S("ItemAccounts"), item.AccountCount)}",
                    FontSize = 12,
                    Foreground = item.Status == "attention"
                        ? ResourceBrush("NegativeChangeBrush")
                        : ResourceBrush("TextFillColorSecondaryBrush"),
                });
                Grid.SetColumn(labels, 1);
                row.Children.Add(labels);

                var listItem = new ListViewItem { Content = row, IsTabStop = false };
                AutomationProperties.SetAutomationId(listItem, $"ConnectedInstitution_{SanitizeAutomationId(item.ItemId)}");
                AutomationProperties.SetName(listItem, $"{name}, {Display.S($"ItemStatus_{item.Status}")}, {string.Format(Display.S("ItemAccounts"), item.AccountCount)}");
                list.Items.Add(listItem);
            }
            content.Children.Add(list);
            content.Children.Add(new TextBlock
            {
                Text = Display.S("PlaidReturningUserExplanation"),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Foreground = ResourceBrush("TextFillColorSecondaryBrush"),
            });
        }

        content.Children.Add(CreateActionButton(
            "ConnectAnotherInstitutionButton",
            Display.S("ConnectAnotherInstitution"),
            Display.S("ConnectAnotherInstitutionHint"),
            "\uE8A7",
            accent: true,
            AddAccountChoice.ConnectNewInstitution));

        content.Children.Add(CreateActionButton(
            "AddManualBalanceButton",
            Display.S("SetupManualAction"),
            Display.S("AddManualBalanceHint"),
            "\uE70F",
            accent: false,
            AddAccountChoice.AddManualBalance));

        if (items.Count > 0)
        {
            content.Children.Add(CreateActionButton(
                "ManageConnectedInstitutionsButton",
                Display.S("ManageConnections"),
                Display.S("ManageConnectionsHint"),
                "\uE713",
                accent: false,
                AddAccountChoice.ManageConnections));
        }

        Content = content;
    }

    private Button CreateActionButton(
        string automationId,
        string title,
        string hint,
        string glyph,
        bool accent,
        AddAccountChoice choice)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new FontIcon { Glyph = glyph, FontSize = 18, VerticalAlignment = VerticalAlignment.Center });

        var labels = new StackPanel { Spacing = 2 };
        labels.Children.Add(new TextBlock { Text = title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        labels.Children.Add(new TextBlock
        {
            Text = hint,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = accent
                ? ResourceBrush("TextOnAccentFillColorSecondaryBrush")
                : ResourceBrush("TextFillColorSecondaryBrush"),
        });
        Grid.SetColumn(labels, 1);
        grid.Children.Add(labels);

        var button = new Button
        {
            Content = grid,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            MinHeight = 58,
            Padding = new Thickness(14, 9, 14, 9),
        };
        if (accent) button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        AutomationProperties.SetAutomationId(button, automationId);
        AutomationProperties.SetName(button, $"{title}. {hint}");
        button.Click += (_, _) =>
        {
            Selection = choice;
            Hide();
        };
        return button;
    }

    private static Brush ResourceBrush(string key) =>
        (Brush)Application.Current.Resources[key];

    private static string SanitizeAutomationId(string value) =>
        string.Concat(value.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
}
