using System.Text.Json;
using AssetScope.Core;
using AssetScope.Core.Api;
using AssetScope.Core.Security;
using AssetScope.Core.Sidecar;
using AssetScope.Core.Diagnostics;
using AssetScope.Core.Localization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace AssetScope.Tests;

public class MoneyTests
{
    [Theory]
    [InlineData("28410.45", true, "$28,410.45")]
    [InlineData("28410.45", false, "$28,410")]
    [InlineData("-1341.00", true, "-$1,341.00")]
    [InlineData("0.00", true, "$0.00")]
    public void Format_renders_us_dollars(string amount, bool decimals, string expected) =>
        Assert.Equal(expected, Money.Format(amount, decimals));

    [Fact]
    public void Privacy_mode_masks_every_shape()
    {
        Assert.Equal(Money.PrivacyMask, Money.Format("123.45", true, privacy: true));
        Assert.Equal(Money.PrivacyMask, Money.FormatSigned(5m, true, privacy: true));
        Assert.Equal(Money.PrivacyMask, Money.FormatCompact(5m, privacy: true));
        Assert.Equal(string.Empty, Money.FormatRatio("0.0202", privacy: true));
    }

    [Theory]
    [InlineData(1234d, "$1.2K")]
    [InlineData(263219d, "$263.2K")]
    [InlineData(1_500_000d, "$1.5M")]
    [InlineData(-263219d, "-$263.2K")]
    [InlineData(999d, "$999")]
    public void Compact_axis_labels(double value, string expected) =>
        Assert.Equal(expected, Money.FormatCompact((decimal)value));

    [Fact]
    public void Signed_format_carries_direction()
    {
        Assert.Equal("+$4,895.00", Money.FormatSigned(4895m));
        Assert.Equal("-$222.48", Money.FormatSigned(-222.48m));
    }

    [Fact]
    public void Ratio_formats_percent_and_tolerates_null()
    {
        Assert.Equal("2.02%", Money.FormatRatio("0.0202"));
        Assert.Equal(string.Empty, Money.FormatRatio(null));
        Assert.Equal(string.Empty, Money.FormatRatio("not-a-number"));
    }

    [Fact]
    public void Parse_is_invariant_and_decimal() =>
        Assert.Equal(28410.45m, Money.Parse("28410.45"));
}

public class InstitutionBrandTests
{
    [Theory]
    [InlineData("Bank of America", InstitutionBrand.BankOfAmerica)]
    [InlineData("BofA", InstitutionBrand.BankOfAmerica)]
    [InlineData("JPMorgan Chase", InstitutionBrand.Chase)]
    [InlineData("Robinhood", InstitutionBrand.Robinhood)]
    [InlineData("Robinhood Securities", InstitutionBrand.Robinhood)]
    [InlineData("X Money", InstitutionBrand.XMoney)]
    [InlineData("X", InstitutionBrand.XMoney)]
    [InlineData("Xfinity Mobile", InstitutionBrand.None)]
    [InlineData("First National Bank", InstitutionBrand.None)]
    [InlineData(null, InstitutionBrand.None)]
    public void Institution_names_map_to_stable_brand_assets(string? name, InstitutionBrand expected) =>
        Assert.Equal(expected, InstitutionBrands.Match(name));
}

public class SecurityBrandTests
{
    [Theory]
    [InlineData("VOO", "Vanguard S&P 500 ETF", SecurityBrand.Vanguard)]
    [InlineData("VTI", "Vanguard Total Stock Market ETF", SecurityBrand.Vanguard)]
    [InlineData("QQQ", "Invesco QQQ Trust", SecurityBrand.Invesco)]
    [InlineData("FXAIX", "Fidelity 500 Index Fund", SecurityBrand.Fidelity)]
    [InlineData("AAPL", "Apple Inc.", SecurityBrand.Apple)]
    [InlineData("MSFT", "Microsoft Corporation", SecurityBrand.Microsoft)]
    [InlineData("NVDA", "NVIDIA Corporation", SecurityBrand.Nvidia)]
    [InlineData("USD", "Cash", SecurityBrand.Cash)]
    [InlineData("AAPL.US", "Apple Inc.", SecurityBrand.Apple)]
    [InlineData("ACME", "Acme Corporation", SecurityBrand.None)]
    public void Symbols_and_names_map_to_local_security_artwork(
        string symbol,
        string name,
        SecurityBrand expected) =>
        Assert.Equal(expected, SecurityBrands.Match(symbol, name));
}

public class InstitutionOverviewTests
{
    [Fact]
    public void Build_sums_already_signed_assets_and_liabilities_once()
    {
        var groups = InstitutionOverview.Build(
        [
            new("checking", "Bank of America", "Checking", "bank", 5_760m, 84m),
            new("savings", "Bank of America", "Savings", "bank", 11_615m, null),
            new("credit", "Bank of America", "Credit card", "credit", -1_698m, -34m),
        ]);

        var group = Assert.Single(groups);
        Assert.Equal(15_677m, group.Total);
        Assert.Equal(50m, group.Change);
        Assert.Equal(50m / 15_627m, group.ChangeRatio);
        Assert.Collection(group.Rows,
            cash =>
            {
                Assert.Equal("cash", cash.Bucket);
                Assert.Equal(17_375m, cash.Total);
            },
            liability =>
            {
                Assert.Equal("liability", liability.Bucket);
                Assert.Equal(-1_698m, liability.Total);
            });
    }

    [Fact]
    public void Build_groups_case_insensitively_orders_by_magnitude_and_limits_cards()
    {
        var groups = InstitutionOverview.Build(
        [
            new("r1", "Robinhood", "Brokerage", "investment", 46_000m, 400m),
            new("r2", "ROBINHOOD", "Banking", "bank", 5_700m, 12m),
            new("c1", "Chase", "Savings", "bank", 17_700m, null),
            new("b1", "Bank of America", "Checking", "bank", 15_600m, null),
            new("o1", "Other", "Cash", "cash", 100m, null),
        ], maximumGroups: 3);

        Assert.Equal(["Robinhood", "Chase", "Bank of America"], groups.Select(group => group.Name));
        Assert.Equal(2, groups[0].Accounts.Count);
        Assert.Equal(51_700m, groups[0].Total);
        Assert.Collection(groups[0].Rows,
            investment =>
            {
                Assert.Equal("investment", investment.Bucket);
                Assert.Equal(46_000m, investment.Total);
            },
            cash =>
            {
                Assert.Equal("cash", cash.Bucket);
                Assert.Equal(5_700m, cash.Total);
            });
    }

    [Fact]
    public void Build_keeps_change_unknown_when_no_account_has_a_comparison()
    {
        var group = Assert.Single(InstitutionOverview.Build(
        [
            new("a", "Bank", "Checking", "bank", 100m, null),
            new("b", "Bank", "Savings", "bank", 200m, null),
        ]));

        Assert.Null(group.Change);
        Assert.Null(group.ChangeRatio);
    }
}

public class HandshakeTests
{
    [Fact]
    public void Parses_the_sidecar_ready_line()
    {
        const string line = """
            {"event":"ready","protocolVersion":"1.0","port":43127,"token":"abc","pid":9,"dataDir":"C:\\x\\.assetscope","mode":"real"}
            """;
        var handshake = JsonSerializer.Deserialize<SidecarHandshake>(line)!;
        Assert.Equal("ready", handshake.Event);
        Assert.Equal(43127, handshake.Port);
        Assert.Equal(SidecarLauncher.ExpectedProtocolVersion, handshake.ProtocolVersion);
    }
}

public class EnvelopeTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Error_envelopes_deserialize_with_code_and_retryable()
    {
        const string json = """
            {"data":null,"meta":{"requestId":"r1","generatedAt":"2026-08-12T00:00:00Z","protocolVersion":"1.0"},
             "error":{"code":"ITEM_LOGIN_REQUIRED","message":"Reconnect.","retryable":false,"details":null}}
            """;
        var envelope = JsonSerializer.Deserialize<ApiEnvelope<BootstrapDto>>(json, Web)!;
        Assert.Null(envelope.Data);
        Assert.Equal("ITEM_LOGIN_REQUIRED", envelope.Error!.Code);
        Assert.False(envelope.Error.Retryable);
    }

    [Fact]
    public void Money_amounts_stay_strings_end_to_end()
    {
        const string json = """{"amount":"28410.45","currencyCode":"USD"}""";
        var money = JsonSerializer.Deserialize<MoneyDto>(json, Web)!;
        Assert.Equal("28410.45", money.Amount);
        Assert.Equal(28410.45m, Money.Parse(money.Amount));
    }

    [Fact]
    public void Reward_cards_carry_the_issuer_announcement_and_activation()
    {
        const string json = """
            {"accountId":"flex-9002","displayName":"Ultimate Rewards","institutionName":"Chase","mask":"9002",
             "productId":"chase-freedom-flex","productName":"Chase Freedom Flex","productSource":"chosen",
             "choiceCategory":null,"rotatingCategories":["groceries","dining"],"rotatingSource":"calendar",
             "rotatingStatus":"unconfirmed","rotatingRates":[{"category":"groceries","rate":"5"},{"category":"dining","rate":"7"}],
             "rotatingActivateBy":"2026-12-14","rotatingUntracked":["American Red Cross Donations"],
             "rotatingAnnouncement":{"url":"https://media.chase.com/news/chase-freedom-2026-q4-categories","state":"read"},
             "rotatingNext":{"quarter":"2027-Q1","categories":["groceries","streaming"],"untracked":[],"activateBy":"2027-03-14","open":false},
             "activation":{"url":"https://www.chasebonus.com/","quarter":"2026-Q4","activateBy":"2026-12-14"},
             "capUsage":{"cap":"1500.00","used":"200.00","remaining":"1300.00"},
             "unit":"points","baseRate":"1","bonuses":[],"notes":[],"spend":"136.00","earned":"6.80","transactionCount":4}
            """;
        var card = JsonSerializer.Deserialize<RewardCardDto>(json, Web)!;
        Assert.Equal(["American Red Cross Donations"], card.RotatingUntracked);
        Assert.Equal("read", card.RotatingAnnouncement!.State);
        Assert.Equal(new[] { "groceries", "streaming" }, card.RotatingNext!.Categories);
        Assert.False(card.RotatingNext.Open);
        Assert.Equal("https://www.chasebonus.com/", card.Activation!.Url);
        Assert.Equal("2026-12-14", card.Activation.ActivateBy);
        Assert.Equal("unconfirmed", card.RotatingStatus);
        Assert.Equal("7", card.RotatingRates.Single(r => r.Category == "dining").Rate);
        Assert.Equal("1300.00", card.CapUsage!.Remaining);
    }
}

public class AppSettingsTests
{
    [Fact]
    public void Defaults_are_bank_first_production_mode()
    {
        var settings = new AppSettings();
        Assert.Equal("real", settings.Mode);
        Assert.Equal("production", settings.PlaidEnvironment);
        Assert.Equal("light", settings.Theme);
        Assert.Equal("system", settings.Language);
        Assert.Null(settings.DisplayName);
        Assert.False(settings.StartInPrivacyMode);
        Assert.True(settings.StartOnHome);
        Assert.Equal("home", settings.LastPage);
    }

    [Fact]
    public void Roundtrips_through_json()
    {
        var settings = new AppSettings
        {
            Mode = "real",
            PlaidClientId = "abc123",
            WindowWidth = 1500,
            Density = "compact",
            StartOnHome = false,
            LastPage = "investments",
        };
        var clone = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        Assert.Equal("abc123", clone.PlaidClientId);
        Assert.Equal(1500, clone.WindowWidth);
        Assert.Equal("compact", clone.Density);
        Assert.False(clone.StartOnHome);
        Assert.Equal("investments", clone.LastPage);
        Assert.False(clone.FirstRun); // FirstRun is derived at load, never persisted
    }
}

public class StartupPageTests
{
    [Theory]
    [InlineData(true, "investments", "home")]
    [InlineData(false, "investments", "investments")]
    [InlineData(false, "living", "living")]
    [InlineData(false, "transactions", "transactions")]
    [InlineData(false, "settings", "settings")]
    [InlineData(false, "removed-page", "home")]
    [InlineData(false, null, "home")]
    public void Resolves_a_safe_start_destination(bool startOnHome, string? lastPage, string expected) =>
        Assert.Equal(expected, StartupPage.Resolve(startOnHome, lastPage));

    [Fact]
    public void Resume_flag_keeps_the_last_page_even_when_start_on_home_is_on() =>
        Assert.Equal("settings", StartupPage.Resolve(startOnHome: true, "settings", resumeLastPage: true));
}

public class AppLaunchTests
{
    [Theory]
    [InlineData(AppLaunchMode.Normal)]
    [InlineData(AppLaunchMode.Background, "--background")]
    [InlineData(AppLaunchMode.SyncOnce, "--sync-once")]
    [InlineData(AppLaunchMode.SyncOnce, "--background", "--SYNC-ONCE")]
    public void Parses_lifecycle_modes(AppLaunchMode expected, params string[] args) =>
        Assert.Equal(expected, AppLaunch.Parse(args));

    [Fact]
    public void Startup_command_is_quoted_and_background_only()
    {
        var command = AppLaunch.BuildStartupCommand(@"C:\Users\Me\App Data\assetscope.exe");
        Assert.Equal("\"C:\\Users\\Me\\App Data\\assetscope.exe\" --background", command);
    }

    [Fact]
    public void Relaunch_arguments_start_the_quoted_exe_after_a_short_delay()
    {
        var args = AppLaunch.BuildRelaunchArguments(@"C:\Users\Me\App Data\assetscope.exe");
        Assert.Contains("ping 127.0.0.1 -n 2", args);
        Assert.Contains("\"C:\\Users\\Me\\App Data\\assetscope.exe\"", args);
        Assert.StartsWith("/d /c ", args);
    }

    [Fact]
    public void Only_the_canonical_install_location_can_register()
    {
        const string local = @"C:\Users\Me\AppData\Local";
        Assert.True(AppLaunch.IsInstalledExecutable(
            @"C:\Users\Me\AppData\Local\Programs\AssetScope\assetscope.exe", local));
        Assert.False(AppLaunch.IsInstalledExecutable(@"D:\repo\bin\assetscope.exe", local));
    }
}

public class OnboardingFlowTests
{
    [Fact]
    public void Manual_balance_is_available_without_any_provider()
    {
        var flow = new OnboardingFlow();

        Assert.Equal(OnboardingStep.ChooseMethod, flow.Step);
        Assert.Equal(OnboardingStep.Done, flow.Complete());
    }

    [Theory]
    [InlineData(false, OnboardingStep.PlaidCredentials)]
    [InlineData(true, OnboardingStep.BankConnect)]
    public void Plaid_only_requests_credentials_when_needed(
        bool configured,
        OnboardingStep expected)
    {
        var flow = new OnboardingFlow();

        Assert.Equal(expected, flow.ChoosePlaid(configured));
    }

    [Theory]
    [InlineData(false, OnboardingStep.SnapTradeCredentials)]
    [InlineData(true, OnboardingStep.BrokerageConnect)]
    public void SnapTrade_only_requests_credentials_when_needed(
        bool configured,
        OnboardingStep expected)
    {
        var flow = new OnboardingFlow();

        Assert.Equal(expected, flow.ChooseSnapTrade(configured));
    }

    [Fact]
    public void Either_provider_can_return_to_the_choice()
    {
        var flow = new OnboardingFlow();
        flow.ChoosePlaid(configured: false);
        Assert.Equal(OnboardingStep.BankConnect, flow.PlaidCredentialsSaved());
        Assert.Equal(OnboardingStep.ChooseMethod, flow.BackToMethods());

        flow.ChooseSnapTrade(configured: false);
        Assert.Equal(OnboardingStep.BrokerageConnect, flow.SnapTradeCredentialsSaved());
        Assert.Equal(OnboardingStep.ChooseMethod, flow.BackToMethods());
    }
}

public class WindowsCredentialsTests
{
    [Fact]
    public void Save_read_delete_roundtrip()
    {
        if (!OperatingSystem.IsWindows()) return;
        var target = $"AssetScope.Tests/{Guid.NewGuid():N}";
        try
        {
            WindowsCredentials.Save(target, "s3cret-value");
            Assert.Equal("s3cret-value", WindowsCredentials.Read(target));
        }
        finally
        {
            WindowsCredentials.Delete(target);
        }
        Assert.Null(WindowsCredentials.Read(target));
    }
}

public class AppLogTests
{
    [Fact]
    public void Startup_replaces_previous_log_and_redacts_tokens()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"assetscope-log-test-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "app.log");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, "previous-session");

            AppLog.Initialize(path);
            AppLog.Info("test", "PLAID_SECRET=do-not-keep access-production-sensitive-token");

            var text = File.ReadAllText(path);
            Assert.DoesNotContain("previous-session", text);
            Assert.DoesNotContain("do-not-keep", text);
            Assert.DoesNotContain("sensitive-token", text);
            Assert.Contains("<redacted>", text);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}

public class ResourceLookupTests
{
    [Fact]
    public void Missing_plain_key_falls_through_to_text_property()
    {
        var lookedUp = new List<string>();
        string Loader(string candidate)
        {
            lookedUp.Add(candidate);
            if (candidate == "AddAccount/Text") return "Add account";
            throw new COMException("NamedResource Not Found", ResourceLookup.NamedResourceNotFound);
        }

        Assert.Equal("Add account", ResourceLookup.Resolve("AddAccount", Loader));
        Assert.Equal(["AddAccount", "AddAccount/Text"], lookedUp);
    }

    [Fact]
    public void Completely_missing_key_is_nonfatal_and_reported()
    {
        string? missing = null;
        var result = ResourceLookup.Resolve("UnknownKey",
            _ => throw new COMException("NamedResource Not Found", ResourceLookup.NamedResourceNotFound),
            key => missing = key);

        Assert.Equal("UnknownKey", result);
        Assert.Equal("UnknownKey", missing);
    }
}

public class NetWorthCompositionTests
{
    [Fact]
    public void Build_IncludesLiabilityMagnitudeAndNormalizesShares()
    {
        var slices = NetWorthComposition.Build(
            [
                new CompositionAmount("bankCash", 60_000m),
                new CompositionAmount("investment", 40_000m),
            ],
            liabilities: 25_000m);

        Assert.Collection(slices,
            bank =>
            {
                Assert.Equal("bankCash", bank.Bucket);
                Assert.Equal(0.48m, bank.Share);
            },
            investment =>
            {
                Assert.Equal("investment", investment.Bucket);
                Assert.Equal(0.32m, investment.Share);
            },
            liability =>
            {
                Assert.Equal("liability", liability.Bucket);
                Assert.Equal(25_000m, liability.Amount);
                Assert.Equal(0.20m, liability.Share);
            });
    }

    [Fact]
    public void Build_DropsNonPositiveAssetsAndHandlesNegativeLiabilityInput()
    {
        var slices = NetWorthComposition.Build(
            [
                new CompositionAmount("bankCash", 0m),
                new CompositionAmount("investment", -50m),
            ],
            liabilities: -125m);

        var liability = Assert.Single(slices);
        Assert.Equal("liability", liability.Bucket);
        Assert.Equal(125m, liability.Amount);
        Assert.Equal(1m, liability.Share);
    }
}

public class PageLayoutMetricsTests
{
    [Theory]
    [InlineData(1055, 1000, true)]
    [InlineData(1056, 1000, false)]
    [InlineData(1105, 1050, true)]
    [InlineData(1106, 1050, false)]
    public void Stack_breakpoints_include_page_shell_padding(
        double pageWidth,
        double requiredContentWidth,
        bool expected) =>
        Assert.Equal(expected,
            PageLayoutMetrics.ShouldStack(pageWidth, requiredContentWidth));

    [Fact]
    public void Primary_width_excludes_shell_padding_sidebar_and_gutter()
    {
        Assert.Equal(968, PageLayoutMetrics.PrimaryColumnWidth(1400, 360, stacked: false));
        Assert.Equal(1344, PageLayoutMetrics.PrimaryColumnWidth(1400, 0, stacked: true));
    }
}

public class RewardsPlanTests
{
    private static RewardCardRateDto Rate(string accountId, string rate, string unit = "percent") =>
        new(accountId, rate, unit, "0");

    private static RewardCategoryDto Category(string category, string spend, params RewardCardRateDto[] best) =>
        new(category, spend, 1, best.ToList(), null, null, null);

    [Fact]
    public void Ties_go_to_the_card_that_covers_most_and_its_base_rate_folds_into_everything_else()
    {
        var plan = RewardsPlan.Build(
        [
            Category("other", "3517.74", Rate("gold", "3")),
            Category("travel", "2009.97", Rate("gold", "3")),
            Category("dining", "1259.71", Rate("flex", "5", "points")),
            Category("groceries", "452.94", Rate("flex", "5", "points")),
            Category("drugstores", "18.50", Rate("gold", "3"), Rate("flex", "3", "points")),
            Category("streaming", "8.99", Rate("gold", "3"), Rate("sapphire", "3", "points")),
            Category("gas", "0.00", Rate("cash", "3"), Rate("gold", "3"), Rate("sapphire", "3", "points")),
        ]);

        Assert.Equal(["flex", "gold"], plan.Select(c => c.AccountId));
        var flex = plan[0];
        Assert.False(flex.IsEveryday);
        Assert.Equal(["dining", "groceries"], flex.Items.Select(i => i.Category));
        Assert.All(flex.Items, i => Assert.Equal(("5", "points"), (i.Rate, i.Unit)));

        var gold = plan[1];
        Assert.True(gold.IsEveryday);
        Assert.Equal(["other", "travel", "drugstores", "streaming", "gas"], gold.Categories);
        Assert.Equal(new RewardsPlanItem("other", "3", "percent"), Assert.Single(gold.Items));
    }

    [Fact]
    public void Everyday_bonuses_stay_listed_and_specialty_cards_lead_by_spend()
    {
        var plan = RewardsPlan.Build(
        [
            Category("other", "100.00", Rate("sapphire", "1", "points")),
            Category("dining", "50.00", Rate("sapphire", "3", "points")),
            Category("travel", "300.00", Rate("travel", "2", "points")),
            Category("gas", "400.00", Rate("cash", "3")),
            Category("entertainment", "20.00"),
        ]);

        Assert.Equal(["cash", "travel", "sapphire"], plan.Select(c => c.AccountId));
        Assert.Equal(
            [new RewardsPlanItem("dining", "3", "points"), new RewardsPlanItem("other", "1", "points")],
            plan[2].Items);
        Assert.DoesNotContain(plan, c => c.Categories.Contains("entertainment"));
    }

    [Fact]
    public void Nothing_to_plan_until_a_card_earns_somewhere()
    {
        Assert.Empty(RewardsPlan.Build([Category("other", "50.00")]));
    }

    [Fact]
    public void Rewards_page_leads_with_the_plan_and_never_shows_a_miss_as_a_gain()
    {
        var root = FindRepositoryRoot();
        var pages = Path.Combine(root, "winui", "AssetScope.App", "Pages");
        var xaml = File.ReadAllText(Path.Combine(pages, "RewardsPage.xaml"));
        var code = File.ReadAllText(Path.Combine(pages, "RewardsPage.xaml.cs"));

        Assert.True(xaml.IndexOf("x:Name=\"PlanCard\"", StringComparison.Ordinal)
            < xaml.IndexOf("x:Name=\"ReviewCard\"", StringComparison.Ordinal));
        Assert.Contains("RewardsPlan.Build(o.Categories)", code);
        // The visual review waits on this marker at the top of the page.
        Assert.Contains("PlanHintText, \"RewardsContentReady\"", code);
        Assert.DoesNotContain("PositiveChangeBrush", code);
    }

    [Fact]
    public void Rewards_page_keeps_the_category_table_aligned_and_the_fine_print_out_of_the_way()
    {
        var root = FindRepositoryRoot();
        var pages = Path.Combine(root, "winui", "AssetScope.App", "Pages");
        var xaml = File.ReadAllText(Path.Combine(pages, "RewardsPage.xaml"));
        var code = File.ReadAllText(Path.Combine(pages, "RewardsPage.xaml.cs"));

        // The column headings and every row are separate grids; they only line
        // up while both declare the same widths.
        var header = Regex.Match(xaml, "<Grid x:Name=\"CategoryHeader\"[\\s\\S]*?</Grid.ColumnDefinitions>").Value;
        var headerWidths = Regex.Matches(header, "Width=\"([^\"]+)\"").Select(m => m.Groups[1].Value);
        Assert.Equal(["28", "*", "1.2*", "80"], headerWidths);
        var row = Regex.Match(code, @"FrameworkElement BuildCategoryRow\([\s\S]*?\n    \}").Value;
        Assert.Contains("new GridLength(28)", row);
        Assert.Contains("new GridLength(1, GridUnitType.Star)", row);
        Assert.Contains("new GridLength(1.2, GridUnitType.Star)", row);
        Assert.Contains("new GridLength(80)", row);

        // Method notes and card terms are one tap away, not a wall of captions.
        Assert.DoesNotContain("x:Name=\"AboutCard\"", xaml);
        Assert.Matches("<Button x:Name=\"AboutButton\"[\\s\\S]*?x:Uid=\"RewardsAboutBody\"", xaml);
        Assert.Contains("BuildNotesButton(card, name)", code);

        // Private-use glyphs render as nothing in editors and diffs.
        Assert.DoesNotMatch("[\uE000-\uF8FF]", code);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "winui", "AssetScope.App")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the assetscope repository root.");
    }
}

public class LivingLabelsTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> NothingPinned = [];

    private static LivingMerchantDto Merchant(
        string key, string group, string automaticKind = "other", string? labelled = null, string? amount = null) =>
        new(key, key.ToUpperInvariant(), labelled ?? automaticKind, automaticKind, labelled, amount,
            "2026-08-04", "1400.00", group);

    private static readonly LivingMerchantDto[] Merchants =
    [
        Merchant("oak street", "labelled", labelled: "rent", amount: "1800.00"),
        Merchant("acme corp payroll", "recurring", automaticKind: "income"),
        Merchant("zelle to j smith", "other", automaticKind: "transfer"),
        Merchant("corner cafe", "other", automaticKind: "dining"),
    ];

    [Fact]
    public void Merchant_list_deserializes_with_its_nullable_fields()
    {
        const string json = """
            {"merchants":[
              {"key":"old landlord","name":"old landlord","kind":"rent","automaticKind":"other","labelledKind":"rent",
               "monthlyAmount":"1500.00","lastDate":null,"lastAmount":null,"group":"labelled"},
              {"key":"corner cafe","name":"Corner Cafe","kind":"dining","automaticKind":"dining","labelledKind":null,
               "monthlyAmount":null,"lastDate":"2026-08-04","lastAmount":"12.50","group":"other"}]}
            """;
        var merchants = JsonSerializer.Deserialize<LivingMerchantsDto>(json, Web)!.Merchants;
        Assert.Equal(2, merchants.Count);
        Assert.Equal("rent", merchants[0].LabelledKind);
        Assert.Equal("other", merchants[0].AutomaticKind);
        Assert.Equal("1500.00", merchants[0].MonthlyAmount);
        Assert.Null(merchants[0].LastDate);
        Assert.Null(merchants[0].LastAmount);
        Assert.Null(merchants[1].LabelledKind);
        Assert.Null(merchants[1].MonthlyAmount);
        Assert.Equal("other", merchants[1].Group);
    }

    [Fact]
    public void Labelled_rows_stay_visible_and_detected_ones_fold_away()
    {
        Assert.Equal(new[] { "oak street" }, LivingLabels.Labelled(Merchants, NothingPinned).Select(m => m.Key));
        Assert.Equal(new[] { "acme corp payroll" }, LivingLabels.Detected(Merchants, NothingPinned).Select(m => m.Key));

        // Picked from search or edited this visit: the row joins the visible list.
        var pinned = new HashSet<string> { "corner cafe", "acme corp payroll" };
        Assert.Equal(new[] { "oak street", "acme corp payroll", "corner cafe" },
            LivingLabels.Labelled(Merchants, pinned).Select(m => m.Key));
        Assert.Empty(LivingLabels.Detected(Merchants, pinned));
    }

    [Fact]
    public void Search_offers_only_merchants_not_shown_yet()
    {
        Assert.Empty(LivingLabels.Suggestions(Merchants, NothingPinned, "  "));
        Assert.Empty(LivingLabels.Suggestions(Merchants, NothingPinned, null));
        Assert.Empty(LivingLabels.Suggestions(Merchants, NothingPinned, "oak"));
        Assert.Equal(new[] { "acme corp payroll" },
            LivingLabels.Suggestions(Merchants, NothingPinned, "acme").Select(m => m.Key));
        Assert.Equal(new[] { "zelle to j smith" },
            LivingLabels.Suggestions(Merchants, NothingPinned, " zelle ").Select(m => m.Key));
        Assert.Empty(LivingLabels.Suggestions(Merchants, new HashSet<string> { "zelle to j smith" }, "zelle"));

        var many = Enumerable.Range(0, 20).Select(i => Merchant($"shop {i}", "other")).ToList();
        var offered = LivingLabels.Suggestions(many, NothingPinned, "SHOP");
        Assert.Equal(LivingLabels.SuggestionLimit, offered.Count);
        Assert.Equal("shop 0", offered[0].Key);
    }

    [Fact]
    public void A_kind_edit_sends_null_for_automatic()
    {
        Assert.Equal("rent", LivingLabels.KindPatch("rent")["kind"]);
        Assert.Null(LivingLabels.KindPatch(string.Empty)["kind"]);
        Assert.Equal("""{"kind":null}""", JsonSerializer.Serialize(LivingLabels.KindPatch(null), Web));
    }

    [Fact]
    public void An_amount_edit_is_sent_only_when_the_text_changed()
    {
        Assert.Null(LivingLabels.AmountPatch(" 1800.00 ", "1800.00"));
        Assert.Null(LivingLabels.AmountPatch(string.Empty, null));
        Assert.Equal("1750", LivingLabels.AmountPatch(" 1750 ", "1800.00")!["monthlyAmount"]);
        Assert.Equal("""{"monthlyAmount":null}""",
            JsonSerializer.Serialize(LivingLabels.AmountPatch("  ", "1800.00"), Web));
    }

    [Fact]
    public void A_saved_edit_becomes_what_the_next_edit_is_compared_against()
    {
        var labelled = LivingLabels.Apply(Merchants[2], LivingLabels.KindPatch("rent"));
        Assert.Equal("rent", labelled.Kind);
        Assert.Equal("rent", labelled.LabelledKind);
        Assert.Equal("transfer", labelled.AutomaticKind);

        var priced = LivingLabels.Apply(labelled, LivingLabels.AmountPatch("1800", labelled.MonthlyAmount)!);
        Assert.Equal("rent", priced.LabelledKind);
        Assert.Equal("1800", priced.MonthlyAmount);
        Assert.Null(LivingLabels.AmountPatch("1800", priced.MonthlyAmount));

        var automatic = LivingLabels.Apply(priced, LivingLabels.KindPatch(null));
        Assert.Equal("transfer", automatic.Kind);
        Assert.Null(automatic.LabelledKind);
        Assert.Equal("1800", automatic.MonthlyAmount);
    }
}

public class LocalizationCatalogTests
{
    [Fact]
    public void English_and_chinese_catalogs_cover_code_and_xaml_keys()
    {
        var root = FindRepositoryRoot();
        var app = Path.Combine(root, "winui", "AssetScope.App");
        var en = ReadNames(Path.Combine(app, "Strings", "en-US", "Resources.resw"));
        var zh = ReadNames(Path.Combine(app, "Strings", "zh-CN", "Resources.resw"));

        Assert.Equal(en.Order(), zh.Order());

        var csharp = string.Join('\n', SourceFiles(app, "*.cs")
            .Select(File.ReadAllText));
        var codeKeys = Regex.Matches(csharp, "Display\\.S\\(\\\"([^\\\"]+)\\\"\\)")
            .Select(match => match.Groups[1].Value)
            .Distinct();
        foreach (var key in codeKeys)
        {
            var resourceForm = key.Replace('/', '.');
            Assert.True(en.Contains(resourceForm)
                || en.Contains(resourceForm + ".Text")
                || en.Contains(resourceForm + ".Content"),
                $"Code localization key '{key}' has no .resw entry.");
        }

        var xaml = string.Join('\n', SourceFiles(app, "*.xaml")
            .Select(File.ReadAllText));
        var uids = Regex.Matches(xaml, "x:Uid=\\\"([^\\\"]+)\\\"")
            .Select(match => match.Groups[1].Value)
            .Distinct();
        foreach (var uid in uids)
            Assert.True(en.Contains(uid)
                || en.Any(name => name.StartsWith(uid + ".", StringComparison.Ordinal)),
                $"XAML x:Uid '{uid}' has no .resw entry.");
    }

    [Fact]
    public void Selector_bar_items_only_localize_their_supported_text_property()
    {
        var root = FindRepositoryRoot();
        var app = Path.Combine(root, "winui", "AssetScope.App");
        var en = ReadNames(Path.Combine(app, "Strings", "en-US", "Resources.resw"));
        var xaml = string.Join('\n', SourceFiles(app, "*.xaml").Select(File.ReadAllText));
        var selectorUids = Regex.Matches(
                xaml,
                "<mux:SelectorBarItem[^>]*x:Uid=\\\"([^\\\"]+)\\\"[^>]*/>")
            .Select(match => match.Groups[1].Value)
            .Distinct();

        foreach (var uid in selectorUids)
        {
            Assert.Contains(uid + ".Text", en);
            Assert.DoesNotContain(uid + ".Content", en);
        }
    }

    [Fact]
    public void Start_only_presentation_settings_restart_the_app_instead_of_asking_the_user()
    {
        var root = FindRepositoryRoot();
        var app = Path.Combine(root, "winui", "AssetScope.App");
        var settings = File.ReadAllText(Path.Combine(app, "Pages", "SettingsPage.xaml.cs"));

        // Theme, language and density are all fixed before the first control
        // renders, so each handler must relaunch rather than leave the control
        // disagreeing with the window.
        foreach (var handler in new[]
        {
            "ThemePicker_SelectionChanged",
            "LanguagePicker_SelectionChanged",
            "DensityToggle_Toggled",
        })
        {
            var body = Regex.Match(
                settings,
                $@"void {handler}\([\s\S]*?\n    \}}").Value;
            Assert.NotEmpty(body);
            Assert.Contains("RestartToApplyPresentation()", body);
        }

        // The relaunch returns the user to Settings rather than to Home.
        var restart = Regex.Match(
            settings, @"void RestartToApplyPresentation\(\)[\s\S]*?\n    \}").Value;
        Assert.Contains("ResumeLastPageOnce = true", restart);
        Assert.Contains("LastPage = \"settings\"", restart);

        // No user-facing string may ask for a manual restart.
        foreach (var locale in new[] { "en-US", "zh-CN" })
        {
            var resw = File.ReadAllText(Path.Combine(app, "Strings", locale, "Resources.resw"));
            Assert.DoesNotContain("LanguageRestartNotice", resw);
            Assert.DoesNotContain("after restarting the app", resw);
            Assert.DoesNotContain("重启应用后生效", resw);
        }
    }

    [Fact]
    public void Density_is_applied_after_startup_never_from_the_app_constructor()
    {
        var root = FindRepositoryRoot();
        var app = Path.Combine(root, "winui", "AssetScope.App");
        var appCode = File.ReadAllText(Path.Combine(app, "App.xaml.cs"));

        // Comments explain both traps at length; only executable lines are the
        // contract being asserted here.
        static string CodeOnly(string source) => string.Join('\n', source
            .Split('\n')
            .Where(line => !line.TrimStart().StartsWith("//")));

        // Application.Resources throws E_UNEXPECTED in the constructor, which
        // killed the process at 0xC000027B before any log existed.
        var constructor = Regex.Match(appCode, @"public App\(\)[\s\S]*?\n    \}").Value;
        Assert.NotEmpty(constructor);
        Assert.DoesNotContain("Resources", CodeOnly(constructor));
        Assert.Contains("ApplyDensity();", appCode);

        // Compact sizing is our dictionary; the SDK property throws outside
        // Experimental builds.
        Assert.DoesNotContain("UseCompactResources", CodeOnly(appCode));
        Assert.True(File.Exists(Path.Combine(app, "Themes", "Density.Compact.xaml")));
        Assert.True(File.Exists(Path.Combine(app, "Themes", "Density.Comfortable.xaml")));

        // A cosmetic preference must never be able to block startup.
        var applyDensity = Regex.Match(appCode, @"void ApplyDensity\(\)[\s\S]*?\n    \}").Value;
        Assert.Contains("catch (Exception", applyDensity);
    }

    [Fact]
    public void Investments_lists_scroll_inside_their_cards_rather_than_lengthening_the_page()
    {
        var root = FindRepositoryRoot();
        var app = Path.Combine(root, "winui", "AssetScope.App");
        var page = File.ReadAllText(Path.Combine(app, "Pages", "InvestmentsPage.xaml"));
        var code = File.ReadAllText(Path.Combine(app, "Pages", "InvestmentsPage.xaml.cs"));

        // Buying more securities must lengthen a list, not the page.
        foreach (var scroll in new[] { "HoldingsScroll", "RealizedScroll" })
            Assert.Contains($"x:Name=\"{scroll}\"", page);
        Assert.Contains("x:Name=\"PageScroll\"", page);

        // The panels are sized to the window: the list row takes a star height
        // so both cards end on the same baseline, and the page stops scrolling.
        Assert.Contains("ListRow.Height = fits ? new GridLength(1, GridUnitType.Star)", code);
        Assert.Contains("PageScroll.VerticalScrollMode = fits ? ScrollMode.Disabled", code);
        // A ScrollViewer measures against infinite height, so the star rows only
        // divide the viewport if the grid is pinned to it.
        Assert.Contains("PageRoot.Height = fits ? height : double.NaN;", code);

        // Fitting is conditional on there being room. Disabling the page scroll
        // on a window too short for the content would clip unreachable rows.
        Assert.Contains("height >= FittedHeightFloor", code);
        Assert.Contains("ApplyResponsiveLayout(e.NewSize.Width, e.NewSize.Height)", code);

        // Top five holdings was the holdings list sorted; the side panel now
        // carries realized profit and loss instead.
        Assert.DoesNotContain("TopHoldingsCard", page);
        Assert.DoesNotContain("TopHoldingsRows", page);
        Assert.DoesNotContain("CreateTopHoldingRow", code);
        Assert.Contains("x:Name=\"RealizedCard\"", page);
        Assert.Contains("x:Name=\"RealizedRows\"", page);

        // The read-only/not-advice disclaimer states itself once, in Settings.
        Assert.DoesNotContain("ReadOnlyNotice", page);
        var settings = File.ReadAllText(Path.Combine(app, "Pages", "SettingsPage.xaml"));
        Assert.Contains("SnapTradeReadOnlyNotice", settings);
        foreach (var locale in new[] { "en-US", "zh-CN" })
        {
            var resw = File.ReadAllText(Path.Combine(app, "Strings", locale, "Resources.resw"));
            Assert.Contains("SnapTradeReadOnlyNotice.Text", resw);
            Assert.DoesNotContain("InvestmentsReadOnlyNotice", resw);
        }
    }

    [Fact]
    public void Primary_page_shells_stretch_without_a_shared_width_cap()
    {
        var root = FindRepositoryRoot();
        var app = Path.Combine(root, "winui", "AssetScope.App");

        // Page shells are density-dependent, so both density dictionaries must
        // hold to the stretch rule — compact tightens padding, never re-caps width.
        foreach (var density in new[] { "Density.Comfortable.xaml", "Density.Compact.xaml" })
        {
            var resources = File.ReadAllText(Path.Combine(app, "Themes", density));
            foreach (var styleName in new[] { "ReferencePageStackStyle", "ReferencePageGridStyle" })
            {
                var style = Regex.Match(
                    resources,
                    $"<Style x:Key=\"{styleName}\"[\\s\\S]*?</Style>").Value;
                Assert.NotEmpty(style);
                Assert.DoesNotContain("Property=\"MaxWidth\"", style);
                Assert.Contains("Property=\"HorizontalAlignment\" Value=\"Stretch\"", style);
            }
        }

        var pages = Path.Combine(app, "Pages");
        foreach (var page in new[] { "HomePage.xaml", "TrendsPage.xaml", "SettingsPage.xaml" })
            Assert.Contains("ReferencePageStackStyle", File.ReadAllText(Path.Combine(pages, page)));
        // Accounts, Transactions, Investments, and Living size to the viewport
        // rather than to their data, so they use the grid shell and scroll
        // inside their own panels.
        foreach (var page in new[]
                 {
                     "AccountsPage.xaml", "TransactionsPage.xaml", "InvestmentsPage.xaml",
                     "LivingPage.xaml",
                 })
            Assert.Contains("ReferencePageGridStyle", File.ReadAllText(Path.Combine(pages, page)));

        var header = File.ReadAllText(Path.Combine(app, "Controls", "PageHeader.cs"));
        Assert.DoesNotContain("_left.Margin", header);
    }

    [Fact]
    public void Settings_uses_fixed_full_width_cards_for_top_level_groups()
    {
        var root = FindRepositoryRoot();
        var settings = File.ReadAllText(Path.Combine(
            root, "winui", "AssetScope.App", "Pages", "SettingsPage.xaml"));

        foreach (var group in new[]
                 {
                     "BackupsSettingsGroup",
                     "PrivacySecuritySettingsGroup",
                     "DiagnosticsSettingsGroup",
                 })
        {
            Assert.Matches(
                $"<Border[^>]*AutomationProperties.AutomationId=\"{group}\"",
                settings);
            Assert.DoesNotMatch(
                $"<mux:Expander[^>]*AutomationProperties.AutomationId=\"{group}\"",
                settings);
        }

        foreach (var connection in new[] { "BankExpander", "SnapTradeExpander" })
        {
            var declaration = Regex.Match(
                settings,
                $"<mux:Expander x:Name=\"{connection}\"[\\s\\S]*?>").Value;
            Assert.Contains("HorizontalAlignment=\"Stretch\"", declaration);
            Assert.Contains("HorizontalContentAlignment=\"Stretch\"", declaration);
        }

        static string Card(string xaml, string automationId) => Regex.Match(
            xaml,
            $"<Border[^>]*AutomationProperties.AutomationId=\"{automationId}\"[\\s\\S]*?</Border>").Value;

        var appearance = Card(settings, "AppearanceSettingsGroup");
        Assert.Contains("x:Name=\"DecimalsToggle\"", appearance);
        Assert.Contains("x:Name=\"DensityToggle\"", appearance);
        Assert.Contains("x:Name=\"AnimationsToggle\"", appearance);

        var refresh = Card(settings, "BackgroundSyncSettingsGroup");
        Assert.Contains("x:Name=\"LaunchSyncToggle\"", refresh);
        Assert.Contains("x:Name=\"DailySyncToggle\"", refresh);

        var connections = Card(settings, "DataConnectionsSettingsGroup");
        Assert.Contains("x:Name=\"DemoModeToggle\"", connections);
        Assert.Contains("AutomationProperties.AutomationId=\"DemoModeToggle\"", connections);

        var settingsCode = File.ReadAllText(Path.Combine(
            root, "winui", "AssetScope.App", "Pages", "SettingsPage.xaml.cs"));
        Assert.Contains("AddAnotherBank", settingsCode);
        Assert.Contains("BankInstitutionsConnectedStatus", settingsCode);
        Assert.Contains("AddBrokerage", settingsCode);
        Assert.Contains("OnboardingDismissed", settingsCode);
        Assert.Contains("ChooseSnapTradeButton", File.ReadAllText(Path.Combine(
            root, "winui", "AssetScope.App", "Pages", "SetupView.cs")));
        Assert.Contains("SkipSetupButton", File.ReadAllText(Path.Combine(
            root, "winui", "AssetScope.App", "Pages", "SetupView.cs")));
        Assert.Contains("onboardingDismissed", File.ReadAllText(Path.Combine(
            root, "winui", "AssetScope.Core", "AppSettings.cs")));
        Assert.Contains("Style=\"{StaticResource ConnectionHeaderButtonStyle}\"", settings);
        Assert.Contains("x:Name=\"ConnectBankButton\"", settings);
        Assert.Contains("x:Name=\"ConnectBrokerageButton\"", settings);

        var en = File.ReadAllText(Path.Combine(
            root, "winui", "AssetScope.App", "Strings", "en-US", "Resources.resw"));
        var zh = File.ReadAllText(Path.Combine(
            root, "winui", "AssetScope.App", "Strings", "zh-CN", "Resources.resw"));
        Assert.Contains("BankStatusConfigured", en);
        Assert.Contains("Connected · Plaid account", en);
        Assert.DoesNotContain("Ready · Plaid account", en);
        Assert.Contains("已连接 · Plaid 账户", zh);
        Assert.DoesNotContain("已就绪 · Plaid 账户", zh);

        var appXaml = File.ReadAllText(Path.Combine(
            root, "winui", "AssetScope.App", "App.xaml"));
        Assert.Contains("x:Key=\"ConnectionHeaderButtonStyle\"", appXaml);
        Assert.Contains("Property=\"MinWidth\" Value=\"200\"", appXaml);

        // Page padding moved into the density dictionaries; the default one
        // still owns the comfortable gutter.
        var comfortable = File.ReadAllText(Path.Combine(
            root, "winui", "AssetScope.App", "Themes", "Density.Comfortable.xaml"));
        Assert.Contains("Value=\"28,0,28,28\"", comfortable);
        Assert.DoesNotContain("Value=\"28,8,28,28\"", comfortable);

        var privacy = Card(settings, "PrivacySecuritySettingsGroup");
        Assert.Contains("x:Name=\"PrivacyToggle\"", privacy);
        Assert.Contains("x:Name=\"PrivacyDefaultToggle\"", privacy);
        Assert.Contains("x:Name=\"PrivacySecurityTooltip\"", privacy);
        Assert.Contains("x:Name=\"ClearSnapTradeCredentialsButton\"", privacy);
        Assert.Contains("Style=\"{StaticResource SettingsToggleSwitchStyle}\"", privacy);
        var toggleStyle = File.ReadAllText(Path.Combine(
            root, "winui", "AssetScope.App", "Themes", "SettingsToggleSwitch.xaml"));
        Assert.Contains("x:Key=\"SettingsToggleSwitchStyle\"", toggleStyle);
        Assert.Contains("<ControlTemplate TargetType=\"ToggleSwitch\">", toggleStyle);
        Assert.DoesNotContain("Width=\"12\" MaxWidth=\"12\"", toggleStyle);
        Assert.Contains("MinWidth\" Value=\"40\"", toggleStyle);
        Assert.DoesNotContain("ToggleSwitchPreContentMargin", toggleStyle);
        Assert.DoesNotContain("ToggleSwitchPostContentMargin", toggleStyle);
        Assert.DoesNotContain("Margin=\"0,5\"", toggleStyle);
        Assert.DoesNotContain("x:Uid=\"PrivacyHint\"", privacy);
        Assert.DoesNotContain("x:Name=\"SecurityStateText\"", privacy);
        Assert.DoesNotContain("x:Name=\"LaunchSyncToggle\"", privacy);
        Assert.DoesNotContain("x:Name=\"DecimalsToggle\"", privacy);
        Assert.DoesNotContain("x:Name=\"ClearLogsButton\"", privacy);
    }

    [Fact]
    public void Page_actions_are_visible_controls_not_header_menu_items()
    {
        var root = FindRepositoryRoot();
        var pages = Path.Combine(root, "winui", "AssetScope.App", "Pages");
        // Every page action is a toolbar button, in-card tab, or Settings card;
        // nothing hides behind the header's three-dot menu.
        foreach (var file in Directory.GetFiles(pages, "*.xaml.cs"))
        {
            var code = File.ReadAllText(file);
            Assert.DoesNotContain("Header.AddMenuItem(", code);
            Assert.DoesNotContain("Header.AddSubMenu(", code);
        }
        var accounts = File.ReadAllText(Path.Combine(pages, "AccountsPage.xaml"));
        Assert.Contains("x:Name=\"AddAccountButton\"", accounts);
        Assert.Contains("x:Name=\"SortButton\"", accounts);
        var investments = File.ReadAllText(Path.Combine(pages, "InvestmentsPage.xaml"));
        Assert.Contains("x:Name=\"HoldingsViewBar\"", investments);
        Assert.DoesNotContain("ShowActivityAndOrdersAsync",
            File.ReadAllText(Path.Combine(pages, "InvestmentsPage.xaml.cs")));
        var living = File.ReadAllText(Path.Combine(pages, "LivingPage.xaml.cs"));
        Assert.DoesNotContain("ContentDialog", living.Replace("ContentDialogButton", ""));
        var settings = File.ReadAllText(Path.Combine(pages, "SettingsPage.xaml"));
        Assert.Contains("AutomationId=\"SavingsGoalSettingsGroup\"", settings);
        // Merchant labels are edited in Settings; the Living page only reads them.
        Assert.Contains("AutomationId=\"LivingLabelsSettingsGroup\"", settings);
        Assert.Contains("<AutoSuggestBox x:Name=\"LabelSearchBox\"", settings);
        Assert.Contains("AutomationId=\"LivingDetectedLabelsExpander\"", settings);
        Assert.DoesNotContain("PutLivingMerchantDefaultAsync", living);
        Assert.DoesNotContain("PatchTransactionAsync", living);
        Assert.True(File.Exists(Path.Combine(root, "winui", "AssetScope.App", "Assets", "Institutions", "x-money.svg")));
    }

    [Fact]
    public void Label_picker_offers_every_kind_the_sidecar_accepts()
    {
        var root = FindRepositoryRoot();
        var service = File.ReadAllText(Path.Combine(root, "native-api", "src", "services", "living.ts"));
        var declared = Regex.Match(service, @"const LIVING_KINDS = new Set\(\[(?<kinds>[^\]]+)\]\)");
        Assert.True(declared.Success, "LIVING_KINDS was not found in living.ts.");
        var sidecarKinds = Regex.Matches(declared.Groups["kinds"].Value, "'([a-z_]+)'")
            .Select(match => match.Groups[1].Value);
        Assert.Equal(sidecarKinds.Order(), LivingLabels.Kinds.Order());
    }

    [Fact]
    public void Shell_routes_transactions_between_accounts_and_investments()
    {
        var root = FindRepositoryRoot();
        var windowXaml = File.ReadAllText(Path.Combine(
            root, "winui", "AssetScope.App", "MainWindow.xaml"));
        var windowCode = File.ReadAllText(Path.Combine(
            root, "winui", "AssetScope.App", "MainWindow.xaml.cs"));

        var accounts = windowXaml.IndexOf("AutomationId=\"NavAccounts\"", StringComparison.Ordinal);
        var transactions = windowXaml.IndexOf("AutomationId=\"NavTransactions\"", StringComparison.Ordinal);
        var investments = windowXaml.IndexOf("AutomationId=\"NavInvestments\"", StringComparison.Ordinal);
        Assert.True(accounts >= 0 && transactions > accounts && investments > transactions,
            "Transactions must sit between Accounts and Investments in the navigation pane.");

        // Menu index, page routing, and the Ctrl+N shortcuts all follow pane order.
        Assert.Contains("\"transactions\" => 2", windowCode);
        Assert.Contains("\"transactions\" => typeof(TransactionsPage)", windowCode);
        Assert.Contains("VirtualKey.Number7", windowCode);
        Assert.Contains("NavigateToTransactions", windowCode);
        Assert.True(StartupPage.IsKnown("transactions"));

        var page = File.ReadAllText(Path.Combine(
            root, "winui", "AssetScope.App", "Pages", "TransactionsPage.xaml"));
        Assert.Contains("IsSourceGrouped=\"True\"", page);
        Assert.Contains("x:DataType=\"local:TransactionRow\"", page);
        Assert.Contains("AutomationProperties.AutomationId=\"TransactionsList\"", page);
    }

    [Fact]
    public void Shell_places_privacy_and_settings_in_footer_and_uses_native_theme_resources()
    {
        var root = FindRepositoryRoot();
        var windowXaml = File.ReadAllText(Path.Combine(
            root, "winui", "AssetScope.App", "MainWindow.xaml"));
        var windowCode = File.ReadAllText(Path.Combine(
            root, "winui", "AssetScope.App", "MainWindow.xaml.cs"));
        var footer = Regex.Match(
            windowXaml,
            "<mux:NavigationView.FooterMenuItems>[\\s\\S]*?</mux:NavigationView.FooterMenuItems>").Value;

        Assert.Contains("AutomationProperties.AutomationId=\"NavPrivacy\"", footer);
        Assert.Contains("AutomationProperties.AutomationId=\"NavSettings\"", footer);
        Assert.DoesNotContain("AutomationProperties.AutomationId=\"NavTheme\"", footer);
        Assert.DoesNotContain("SelectsOnInvoked=\"False\"", footer);
        Assert.DoesNotContain("NavProfile", windowXaml);
        Assert.DoesNotContain("VirtualKey.P", windowCode);
        Assert.DoesNotContain("NavigationViewExpandedPaneBackground", windowXaml);
        Assert.DoesNotContain("NavigationViewDefaultPaneBackground", windowXaml);
        var brand = Regex.Match(windowXaml, "<Border x:Name=\"TitleBarBrandHost\"[\\s\\S]*?>").Value;
        Assert.Contains("Background=\"Transparent\"", brand);
        Assert.DoesNotContain("AppSidebarBrush", brand);
        Assert.DoesNotContain("LayerOnMicaBaseAltFillColorDefaultBrush", brand);
        Assert.Contains("IsTitleBarAutoPaddingEnabled=\"True\"", windowXaml);
        Assert.Contains("ButtonBackgroundColor = Microsoft.UI.Colors.Transparent", windowCode);
        Assert.Contains("DwmwaCaptionColor", windowCode);
        Assert.Contains("ApplyMaximizedCaptionInset", windowCode);
        Assert.Contains("new Thickness(0, -1, 0, 0)", windowCode);
        Assert.Contains("OffsetPaneBelowCaption", windowCode);
        Assert.Contains("TogglePaneTopPadding", windowCode);
        Assert.Contains("ShutdownFast()", windowCode);
        var session = File.ReadAllText(Path.Combine(
            root, "winui", "AssetScope.App", "Services", "AppSession.cs"));
        Assert.Contains("TryStartAsync", session);
        Assert.Contains("EnsureEngineAsync", session);
        Assert.Contains("TimeSpan.FromSeconds(45)", session);
        Assert.Contains("previousLauncher", session);
        Assert.Contains("incomingApi", session);
        Assert.DoesNotContain("ShutdownAsync().Wait", windowCode);
        Assert.Contains("PrivacyNavIcon.Glyph", windowCode);
        Assert.Contains("nameof(AppSession.PrivacyMode)", windowCode);
        Assert.Contains("if (tag == \"privacy\")", windowCode);
        Assert.Contains("sender.SelectedItem = _selectedDestination", windowCode);
        Assert.Contains("_privacySelectionPending", windowCode);
        Assert.Contains("ActualThemeChanged", windowCode);

        var settings = File.ReadAllText(Path.Combine(
            root, "winui", "AssetScope.App", "Pages", "SettingsPage.xaml"));
        foreach (var theme in new[] { "ThemeLightItem", "ThemeDarkItem", "ThemeSystemItem" })
            Assert.Contains($"AutomationProperties.AutomationId=\"{theme}\"", settings);

        var appResources = File.ReadAllText(Path.Combine(root, "winui", "AssetScope.App", "App.xaml"));
        Assert.Contains("x:Key=\"WindowCaptionBackground\"", appResources);
        Assert.Contains("x:Key=\"WindowCaptionBackgroundDisabled\"", appResources);
        Assert.Contains("x:Key=\"InfoTipButtonStyle\"", appResources);
        Assert.Contains("x:Key=\"ConnectionHeaderButtonStyle\"", appResources);
        foreach (var page in new[] { "HomePage.xaml", "TrendsPage.xaml" })
        {
            var xaml = File.ReadAllText(Path.Combine(root, "winui", "AssetScope.App", "Pages", page));
            Assert.Contains("x:Uid=\"NetWorthTooltip\"", xaml);
            Assert.Contains("<Button.Flyout>", xaml);
            Assert.Contains("x:Name=\"NetWorthInfoButton\"", xaml);
            Assert.DoesNotContain("x:Name=\"EstimateInfoButton\"", xaml);
            Assert.DoesNotContain("&#xE7BA;", xaml);
        }
    }

    [Fact]
    public void Shared_header_uses_one_three_state_refresh_button()
    {
        var root = FindRepositoryRoot();
        var header = File.ReadAllText(Path.Combine(
            root, "winui", "AssetScope.App", "Controls", "PageHeader.cs"));

        Assert.Contains("\"HeaderRefreshButton\"", header);
        Assert.Contains("RefreshVisualState.Syncing", header);
        Assert.Contains("RefreshVisualState.Succeeded", header);
        Assert.Contains("_spinStoryboard.Begin()", header);
        Assert.Contains("_successTimer.Start()", header);
        Assert.Contains("_refresh.Content = _refreshIcon", header);
        Assert.Contains("SetRefreshState(RefreshVisualState.Idle)", header);
        Assert.DoesNotContain("SettingsTitle", header);
        Assert.DoesNotContain("ProgressRing", header);
        Assert.DoesNotContain("ShowHealthyCheck", header);
    }

    [Fact]
    public void About_page_is_minimal_and_keeps_unimplemented_actions_inert()
    {
        var root = FindRepositoryRoot();
        var app = Path.Combine(root, "winui", "AssetScope.App");
        var xaml = File.ReadAllText(Path.Combine(app, "Pages", "SettingsPage.xaml"));
        var code = File.ReadAllText(Path.Combine(app, "Pages", "SettingsPage.xaml.cs"));
        var readme = File.ReadAllText(Path.Combine(root, "README.md"));
        var notices = File.ReadAllText(Path.Combine(root, "THIRD_PARTY_NOTICES.txt"));

        Assert.Contains("x:Uid=\"CheckUpdates\"", xaml);
        Assert.Contains("x:Uid=\"OpenHelp\"", xaml);
        Assert.DoesNotContain("CheckUpdates_Click", xaml);
        Assert.DoesNotContain("OpenHelp_Click", xaml);
        Assert.DoesNotContain("IsEnabled=\"False\"", Regex.Match(
            xaml, "<Border Grid.Row=\"2\"[\\s\\S]*?</Border>").Value);
        Assert.DoesNotContain("x:Uid=\"AboutBody\"", xaml);
        Assert.DoesNotContain("x:Uid=\"AboutAttribution\"", xaml);
        Assert.DoesNotContain("x:Uid=\"AboutDisclaimer\"", xaml);
        Assert.DoesNotContain("github.com/tomfunk/fungible", code);
        Assert.Contains("`native-api` is AssetScope's backend", readme);
        Assert.Contains("adapted portions of the MIT-licensed Fungible project", notices);
        Assert.Contains("THIRD_PARTY_NOTICES.txt", code);
    }

    [Fact]
    public void Investment_page_uses_cost_basis_profit_and_loss_not_balance_growth()
    {
        var root = FindRepositoryRoot();
        var page = File.ReadAllText(Path.Combine(
            root, "winui", "AssetScope.App", "Pages", "InvestmentsPage.xaml.cs"));
        var xaml = File.ReadAllText(Path.Combine(
            root, "winui", "AssetScope.App", "Pages", "InvestmentsPage.xaml"));

        Assert.Contains("SetSignedChange(TotalGainText, overview.TotalGainLoss);", page);
        Assert.DoesNotContain("PeriodAnalysis", page);
        Assert.DoesNotContain("NetContribution", xaml);
        Assert.Contains("InvestmentUnrealizedGainText", xaml);
    }

    private static HashSet<string> ReadNames(string path) =>
        XDocument.Load(path).Root!.Elements("data")
            .Select(element => (string)element.Attribute("name")!)
            .ToHashSet(StringComparer.Ordinal);

    private static IEnumerable<string> SourceFiles(string root, string pattern) =>
        Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "package.json"))
                && File.Exists(Path.Combine(directory.FullName, "native-api", "src", "main.ts"))
                && Directory.Exists(Path.Combine(directory.FullName, "winui", "AssetScope.App")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the assetscope repository root.");
    }
}

public class VisualSystemTests
{
    private static string App(params string[] parts) =>
        Path.Combine([FindRepositoryRoot(), "winui", "AssetScope.App", .. parts]);

    [Fact]
    public void Compact_density_redeclares_every_comfortable_key()
    {
        static HashSet<string> Keys(string path) => Regex
            .Matches(File.ReadAllText(path), "x:Key=\"([^\"]+)\"")
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        // A key only the comfortable dictionary declares would ignore the
        // compact setting, because a merged dictionary overrides by key.
        var comfortable = Keys(App("Themes", "Density.Comfortable.xaml"));
        var compact = Keys(App("Themes", "Density.Compact.xaml"));
        Assert.Empty(comfortable.Except(compact));
        foreach (var key in new[] { "CaptionStyle", "SectionLabelStyle", "StatTileStyle", "StatValueStyle" })
            Assert.Contains(key, comfortable);
    }

    [Fact]
    public void Blue_text_has_a_shade_for_every_theme()
    {
        var app = File.ReadAllText(App("App.xaml"));
        foreach (var theme in new[] { "Light", "Dark", "HighContrast" })
        {
            var dictionary = Regex.Match(
                app, $@"<ResourceDictionary x:Key=""{theme}"">[\s\S]*?</ResourceDictionary>").Value;
            Assert.Contains("x:Key=\"ReferenceBlueTextBrush\"", dictionary);
        }
        Assert.Contains("x:Key=\"DividerStyle\"", app);
    }

    [Fact]
    public void Every_page_header_sits_on_the_shared_shell_padding()
    {
        // Accounts once padded itself, so its header jumped when switching pages.
        var accounts = File.ReadAllText(App("Pages", "AccountsPage.xaml.cs"));
        Assert.DoesNotContain("PageLayout.Padding", accounts);
        Assert.Contains("ReferencePageGridStyle", File.ReadAllText(App("Pages", "AccountsPage.xaml")));
    }

    [Fact]
    public void Trends_states_each_total_once()
    {
        // The four totals already have their own cards beside the chart.
        var trends = File.ReadAllText(App("Pages", "TrendsPage.xaml"));
        Assert.DoesNotContain("TrendSnapshotTitle", trends);
        Assert.DoesNotContain("SideNetWorthText", trends);
        Assert.Contains("x:Name=\"KpiNetWorthText\"", trends);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "winui", "AssetScope.App")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the assetscope repository root.");
    }
}
