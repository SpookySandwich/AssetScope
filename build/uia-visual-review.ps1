param(
    [Parameter(Mandatory = $true)][int]$TargetProcessId,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [int[][]]$Sizes = @(
        @(900, 650), @(1049, 760), @(1050, 760), @(1079, 780), @(1080, 780),
        @(1259, 850), @(1260, 850), @(1272, 850), @(1273, 850),
        @(1280, 800), @(1600, 900)
    ),
    [string[]]$Pages = @('home', 'accounts', 'transactions', 'investments', 'living', 'rewards', 'trends', 'settings'),
    [switch]$CaptureExpandedSnapTrade
)

$ErrorActionPreference = 'Stop'
# The shell intentionally uses PaneDisplayMode=LeftCompact. Width changes
# reflow page content but never force the navigation pane open on the user.
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class AssetScopeVisualReviewNative {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", SetLastError=true)] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll", SetLastError=true)] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int w, int hgt, uint f);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
    [DllImport("user32.dll", SetLastError=true)] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint f);
}
'@

function Find-ById($Root, [string]$Id, [int]$TimeoutMs = 8000) {
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while ($timer.ElapsedMilliseconds -lt $TimeoutMs) {
        $element = $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
        if ($null -ne $element) { return $element }
        Start-Sleep -Milliseconds 100
    }
    throw "Timed out waiting for '$Id'."
}

function Find-OptionalByName($Root, [string]$Name) {
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    return $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Find-OptionalById($Root, [string]$Id) {
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
    return $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Wait-ForNonEmptyName($Window, [string]$Id, [int]$TimeoutMs = 10000) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while ($timer.ElapsedMilliseconds -lt $TimeoutMs) {
        $element = Find-OptionalById $Window $Id
        if ($null -ne $element -and -not [string]::IsNullOrWhiteSpace($element.Current.Name)) {
            return
        }
        Start-Sleep -Milliseconds 100
    }
    throw "Timed out waiting for non-empty text in '$Id'."
}

function Wait-ForPageSettled($Window, [string]$CheckId, [int]$Width, [int]$TimeoutMs = 5000) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $stableSamples = 0
    while ($timer.ElapsedMilliseconds -lt $TimeoutMs) {
        $check = Find-OptionalById $Window $CheckId
        $navHome = Find-OptionalById $Window 'NavHome'
        $checkVisible = $null -ne $check -and -not $check.Current.IsOffscreen -and
            $check.Current.BoundingRectangle.Width -gt 0 -and $check.Current.BoundingRectangle.Height -gt 0

        # At the minimum width the closed pane can remove navigation items from
        # the automation tree. At larger sizes LeftCompact remains 64 DIPs.
        $navigationSettled = if ($Width -le 900) {
            $true
        } else {
            $null -ne $navHome -and -not $navHome.Current.IsOffscreen -and
                ($navHome.Current.BoundingRectangle.Width / $scale) -le 80
        }

        if ($checkVisible -and $navigationSettled) {
            $stableSamples++
            if ($stableSamples -ge 3) { return }
        } else {
            $stableSamples = 0
        }
        Start-Sleep -Milliseconds 100
    }
    $check = Find-OptionalById $Window $CheckId
    $navHome = Find-OptionalById $Window 'NavHome'
    $checkState = if ($null -eq $check) { 'missing' } else {
        "offscreen=$($check.Current.IsOffscreen), bounds=$($check.Current.BoundingRectangle)"
    }
    $navState = if ($null -eq $navHome) { 'missing' } else {
        "offscreen=$($navHome.Current.IsOffscreen), bounds=$($navHome.Current.BoundingRectangle)"
    }
    Save-Shot $handle (Join-Path $OutputDirectory "diagnostic-$CheckId-${Width}.png")
    throw "Page '$CheckId' did not settle at width $Width. check=[$checkState], homeNav=[$navState]"
}

function Select-Page($Window, [string]$Id, [int]$Width) {
    if ($Width -le 900) {
        # In Minimal mode WinUI removes closed-pane navigation items from the
        # automation tree completely. Only open the pane when the target is not
        # already reachable; blindly toggling can close a pane that stayed open
        # after selecting the current item.
        $target = Find-OptionalById $Window $Id
        if ($null -eq $target -or $target.Current.IsOffscreen) {
            $toggle = Find-ById $Window 'TogglePaneButton'
            $invoke = $toggle.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
            $invoke.Invoke()
            Start-Sleep -Milliseconds 200
        }
    }

    $element = Find-ById $Window $Id
    $pattern = $element.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
    $pattern.Select()
    Start-Sleep -Milliseconds 180
}

function Wait-ForAppReady($Window, [int]$TimeoutMs = 30000) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while ($timer.ElapsedMilliseconds -lt $TimeoutMs) {
        $homeNavItem = Find-OptionalById $Window 'NavHome'
        if ($null -ne $homeNavItem -and $homeNavItem.Current.IsEnabled -and
            -not $homeNavItem.Current.IsOffscreen) {
            return
        }
        Start-Sleep -Milliseconds 100
    }
    throw 'The application did not enable navigation after startup.'
}

function Wait-ForSyncSettled($Window, [int]$TimeoutMs = 120000) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $stableSamples = 0
    while ($timer.ElapsedMilliseconds -lt $TimeoutMs) {
        $refreshButton = Find-OptionalById $Window 'HeaderRefreshButton'
        $syncSettled = $null -ne $refreshButton -and
            -not $refreshButton.Current.IsOffscreen -and
            $refreshButton.Current.IsEnabled -and
            $refreshButton.Current.Name -notmatch '同步中|Syncing'
        if ($syncSettled) {
            $stableSamples++
            if ($stableSamples -ge 5) { return }
        } else {
            $stableSamples = 0
        }
        Start-Sleep -Milliseconds 100
    }
    throw 'The refresh command did not return to its settled state before visual capture.'
}

function Wait-ForNavigationMode($Window, [int]$Width, [double]$Scale, [int]$TimeoutMs = 5000) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $stableSamples = 0
    $lastWidth = 0
    while ($timer.ElapsedMilliseconds -lt $TimeoutMs) {
        if ($Width -le 900) {
            $toggle = Find-OptionalById $Window 'TogglePaneButton'
            if ($null -ne $toggle -and -not $toggle.Current.IsOffscreen -and
                $toggle.Current.BoundingRectangle.Width -gt 0) {
                $stableSamples++
                if ($stableSamples -ge 3) { return 0 }
            } else {
                $stableSamples = 0
            }
            Start-Sleep -Milliseconds 100
            continue
        }

        $navHome = Find-OptionalById $Window 'NavHome'
        if ($null -ne $navHome) {
            $lastWidth = [Math]::Round($navHome.Current.BoundingRectangle.Width / $Scale, 1)
            $matches = $lastWidth -gt 0 -and $lastWidth -le 80
            if ($matches) {
                $stableSamples++
                if ($stableSamples -ge 3) { return $lastWidth }
            } else {
                $stableSamples = 0
            }
        }
        Start-Sleep -Milliseconds 100
    }
    throw "Navigation did not reach the expected mode at $Width DIPs (last item width $lastWidth)."
}

function Assert-Contained($Element, $Window, [string]$Label) {
    $item = $Element.Current.BoundingRectangle
    $outer = $Window.Current.BoundingRectangle
    if ($item.Width -le 0 -or $item.Height -le 0) { throw "$Label has no visible bounds." }
    if ($item.Left -lt ($outer.Left - 1) -or $item.Right -gt ($outer.Right + 1)) {
        throw "$Label overflows horizontally: item=$item window=$outer"
    }
}

function Scroll-ToTop($Window) {
    # Page navigation and async rendering can replace peers while UIA walks the
    # tree. Take a bounded retryable snapshot instead of failing the whole
    # review on an ElementNotAvailableException from a stale sibling.
    for ($attempt = 0; $attempt -lt 3; $attempt++) {
        try {
            $elements = $Window.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                [System.Windows.Automation.Condition]::TrueCondition)
            foreach ($element in $elements) {
                try {
                    $pattern = $null
                    if (-not $element.TryGetCurrentPattern(
                        [System.Windows.Automation.ScrollPattern]::Pattern, [ref]$pattern)) { continue }
                $scroll = [System.Windows.Automation.ScrollPattern]$pattern
                if ($scroll.Current.VerticallyScrollable) { $scroll.SetScrollPercent(-1, 0) }
                } catch [System.Windows.Automation.ElementNotAvailableException] { }
            }
            return
        } catch [System.Windows.Automation.ElementNotAvailableException] {
            Start-Sleep -Milliseconds 100
        }
    }
    throw 'The UI Automation tree stayed unstable while normalizing scroll position.'
}

function Save-Shot([IntPtr]$Handle, [string]$Path) {
    $rect = [AssetScopeVisualReviewNative+RECT]::new()
    if (-not [AssetScopeVisualReviewNative]::GetWindowRect($Handle, [ref]$rect)) { throw 'GetWindowRect failed.' }
    $bitmap = [Drawing.Bitmap]::new($rect.Right - $rect.Left, $rect.Bottom - $rect.Top)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $dc = $graphics.GetHdc()
        try { $ok = [AssetScopeVisualReviewNative]::PrintWindow($Handle, $dc, 2) }
        finally { $graphics.ReleaseHdc($dc) }
        if (-not $ok) { $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bitmap.Size) }
        $bitmap.Save($Path, [Drawing.Imaging.ImageFormat]::Png)
    } finally { $graphics.Dispose(); $bitmap.Dispose() }
}

$process = Get-Process -Id $TargetProcessId
$handle = $process.MainWindowHandle
if ($handle -eq [IntPtr]::Zero) { throw 'Target process has no visible main window.' }
$window = [System.Windows.Automation.AutomationElement]::FromHandle($handle)
$scale = [AssetScopeVisualReviewNative]::GetDpiForWindow($handle) / 96.0
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$results = @()
$pageCatalog = @(
    # These IDs are assigned only after each page consumes its real API
    # response. Page roots can exist behind StartupOverlay before the
    # data-driven composition is ready to capture.
    @{ Nav='NavHome'; Check='HomeDashboardReady'; File='home' },
    @{ Nav='NavAccounts'; Check='AccountsListReady'; File='accounts' },
    @{ Nav='NavTransactions'; Check='TransactionsContentReady'; File='transactions' },
    # Page and decorative Border roots do not create AutomationPeers; anchor
    # loaded state to the user-visible portfolio value text.
    @{ Nav='NavInvestments'; Check='InvestmentsContentReady'; File='investments' },
    @{ Nav='NavLiving'; Check='LivingContentReady'; File='living' },
    @{ Nav='NavRewards'; Check='RewardsContentReady'; File='rewards' },
    @{ Nav='NavTrends'; Check='TrendsContentReady'; File='trends' },
    @{ Nav='NavSettings'; Check='LanguagePicker'; File='settings' }
)
$selectedPages = $pageCatalog | Where-Object { $_.File -in $Pages }
if ($selectedPages.Count -eq 0) { throw 'No recognized pages were requested.' }
Wait-ForAppReady $window
Wait-ForSyncSettled $window

foreach ($size in $Sizes) {
    if ($size.Count -ne 2) {
        throw "Each review size must contain exactly width and height; received '$($size -join ',')'."
    }
    $width, $height = $size
    # Restore first: SetWindowPos against a maximized window only changes
    # transient maximized bounds on some Windows builds, and a later Restore
    # can snap back to the user's saved (smaller) normal rectangle.
    [AssetScopeVisualReviewNative]::ShowWindow($handle, 9) | Out-Null
    [AssetScopeVisualReviewNative]::SetWindowPos($handle, [IntPtr]::Zero, 24, 24,
        [int][Math]::Round($width * $scale), [int][Math]::Round($height * $scale), 0) | Out-Null
    Start-Sleep -Milliseconds 300

    $navWidth = Wait-ForNavigationMode $window $width $scale
    if ($navWidth -gt 80) { throw "Navigation did not stay compact at $width DIPs (item width $navWidth)." }

    foreach ($page in $selectedPages) {
        Select-Page $window $page.Nav $width
        # NavigationCache and nested ScrollViewers can retain a prior review
        # position. Normalize the initial state before asserting the first
        # viewport so a previous expanded-state capture cannot poison it.
        Scroll-ToTop $window
        Start-Sleep -Milliseconds 100
        Wait-ForPageSettled $window $page.Check $width
        $check = Find-ById $window $page.Check
        Assert-Contained $check $window "$($page.Check) at ${width}x${height}"
        Save-Shot $handle (Join-Path $OutputDirectory "$($page.File)-${width}x${height}.png")

        if ($page.File -eq 'settings' -and $CaptureExpandedSnapTrade) {
            $snapTrade = Find-ById $window 'SnapTradeConnectionExpander'
            $scrollPattern = $null
            if ($snapTrade.TryGetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern, [ref]$scrollPattern)) {
                ([System.Windows.Automation.ScrollItemPattern]$scrollPattern).ScrollIntoView()
            }
            $expandPattern = $null
            if ($snapTrade.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$expandPattern)) {
                if (([System.Windows.Automation.ExpandCollapsePattern]$expandPattern).Current.ExpandCollapseState -eq
                    [System.Windows.Automation.ExpandCollapseState]::Collapsed) {
                    ([System.Windows.Automation.ExpandCollapsePattern]$expandPattern).Expand()
                }
            }
            Start-Sleep -Milliseconds 180
            Assert-Contained $snapTrade $window "SnapTrade settings at ${width}x${height}"
            Save-Shot $handle (Join-Path $OutputDirectory "settings-snaptrade-${width}x${height}.png")
        }
    }

    $results += [pscustomobject]@{ Window="${width}x${height}"; NavigationWidth=$navWidth; PagesContained=$true }
}

$results | Format-Table -AutoSize
