[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ExecutablePath,

    [Parameter(Mandatory = $true)]
    [string]$FixturePath,

    [ValidateRange(5, 300)]
    [int]$TimeoutSeconds = 30,

    [switch]$KeepOpen
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

function Resolve-RequiredFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Description,

        [switch]$AllowDirectory
    )

    if (-not (Test-Path -LiteralPath $Path) -or
        (-not $AllowDirectory -and -not (Test-Path -LiteralPath $Path -PathType Leaf))) {
        throw "$Description does not exist: $Path"
    }

    return (Resolve-Path -LiteralPath $Path).Path
}

function Wait-MainWindow {
    param(
        [Parameter(Mandatory = $true)]
        [System.Diagnostics.Process]$Process,

        [Parameter(Mandatory = $true)]
        [datetime]$Deadline
    )

    while ([datetime]::UtcNow -lt $Deadline) {
        if ($Process.HasExited) {
            throw "NoGaReader exited before its main window appeared (exit code $($Process.ExitCode))."
        }

        $Process.Refresh()
        if ($Process.MainWindowHandle -ne [IntPtr]::Zero) {
            $window = [System.Windows.Automation.AutomationElement]::FromHandle($Process.MainWindowHandle)
            if ($null -ne $window -and $window.Current.ProcessId -eq $Process.Id) {
                return $window
            }
        }

        Start-Sleep -Milliseconds 100
    }

    throw "Timed out waiting for the NoGaReader main window."
}

function Find-NamedElement {
    param(
        [Parameter(Mandatory = $true)]
        $Root,

        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty,
        $Name)
    return $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Wait-NamedElement {
    param(
        [Parameter(Mandatory = $true)]
        $Root,

        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [datetime]$Deadline
    )

    while ([datetime]::UtcNow -lt $Deadline) {
        $element = Find-NamedElement -Root $Root -Name $Name
        if ($null -ne $element) {
            return $element
        }

        Start-Sleep -Milliseconds 100
    }

    throw "Timed out waiting for UI Automation element '$Name'."
}

function Wait-ProcessWindowContainingElement {
    param(
        [Parameter(Mandatory = $true)]
        [System.Diagnostics.Process]$Process,

        [Parameter(Mandatory = $true)]
        [string]$ElementName,

        [Parameter(Mandatory = $true)]
        [datetime]$Deadline
    )

    $processCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
        $Process.Id)
    while ([datetime]::UtcNow -lt $Deadline) {
        if ($Process.HasExited) {
            throw "NoGaReader exited while waiting for UI Automation element '$ElementName'."
        }

        $windows = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
            [System.Windows.Automation.TreeScope]::Children,
            $processCondition)
        foreach ($window in $windows) {
            $element = Find-NamedElement -Root $window -Name $ElementName
            if ($null -ne $element) {
                return [pscustomobject]@{
                    Window = $window
                    Element = $element
                }
            }
        }

        Start-Sleep -Milliseconds 100
    }

    throw "Timed out waiting for a NoGaReader window containing UI Automation element '$ElementName'."
}

function Invoke-AutomationElement {
    param(
        [Parameter(Mandatory = $true)]
        $Element,

        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    if (-not $Element.Current.IsEnabled) {
        throw "UI Automation element '$Name' is disabled."
    }

    try {
        $pattern = $Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
        $pattern.Invoke()
    }
    catch {
        throw "UI Automation element '$Name' is not invokable: $($_.Exception.Message)"
    }
}

function Wait-ElementEnabled {
    param(
        [Parameter(Mandatory = $true)]
        $Element,

        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [datetime]$Deadline
    )

    while ([datetime]::UtcNow -lt $Deadline) {
        if ($Element.Current.IsEnabled) {
            return
        }

        Start-Sleep -Milliseconds 100
    }

    throw "Timed out waiting for UI Automation element '$Name' to become enabled."
}

function Set-ToggleOn {
    param(
        [Parameter(Mandatory = $true)]
        $Element,

        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    try {
        $pattern = $Element.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
        if ($pattern.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::On) {
            $pattern.Toggle()
        }
        if ($pattern.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::On) {
            throw "Toggle did not enter the On state."
        }
    }
    catch {
        throw "UI Automation toggle '$Name' could not be selected: $($_.Exception.Message)"
    }
}

function Stop-OwnedProcess {
    param(
        [Parameter(Mandatory = $true)]
        [System.Diagnostics.Process]$Process,

        $Window
    )

    if ($Process.HasExited) {
        return
    }

    if ($null -ne $Window) {
        try {
            $windowPattern = $Window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
            $windowPattern.Close()
            if ($Process.WaitForExit(3000)) {
                return
            }
        }
        catch {
            # The exact process is force-closed below if graceful UI close fails.
        }
    }

    if (-not $Process.HasExited) {
        try {
            $Process.Kill($true)
        }
        catch [System.Management.Automation.MethodException] {
            $Process.Kill()
        }

        [void]$Process.WaitForExit(5000)
    }
}

$launchedProcess = $null
$mainWindow = $null
$readerWindow = $null
$runDataRoot = $null
$succeeded = $false
$failureMessage = $null

try {
    $resolvedExecutable = Resolve-RequiredFile -Path $ExecutablePath -Description 'Executable'
    $resolvedFixture = Resolve-RequiredFile -Path $FixturePath -Description 'Fixture' -AllowDirectory
    $isComicFixture = [System.IO.Directory]::Exists($resolvedFixture) -or
        @('.cbz', '.cbr', '.cb7') -contains [System.IO.Path]::GetExtension($resolvedFixture).ToLowerInvariant()
    if ([System.IO.Path]::GetExtension($resolvedExecutable) -ne '.exe') {
        throw "ExecutablePath must point to an .exe file: $resolvedExecutable"
    }

    $repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
    $uiSmokeRoot = Join-Path $repositoryRoot '.tmp\ui-smoke'
    $runDataRoot = Join-Path $uiSmokeRoot ([guid]::NewGuid().ToString('N'))
    [void][System.IO.Directory]::CreateDirectory($runDataRoot)

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $resolvedExecutable
    $startInfo.WorkingDirectory = [System.IO.Path]::GetDirectoryName($resolvedExecutable)
    $startInfo.UseShellExecute = $false
    if ($null -ne $startInfo.PSObject.Properties['ArgumentList']) {
        $startInfo.ArgumentList.Add($resolvedFixture)
    }
    else {
        # Windows file names cannot contain a quote, so conventional quoting is safe here.
        $startInfo.Arguments = '"' + $resolvedFixture + '"'
    }

    if ($null -ne $startInfo.PSObject.Properties['Environment']) {
        $startInfo.Environment['NOGAREADER_DATA_DIR'] = $runDataRoot
    }
    else {
        $startInfo.EnvironmentVariables['NOGAREADER_DATA_DIR'] = $runDataRoot
    }

    $launchedProcess = [System.Diagnostics.Process]::Start($startInfo)
    if ($null -eq $launchedProcess) {
        throw 'Process.Start did not return a NoGaReader process.'
    }

    $launchedProcess.Refresh()
    $actualExecutable = $launchedProcess.MainModule.FileName
    if (-not [string]::Equals(
            [System.IO.Path]::GetFullPath($actualExecutable),
            [System.IO.Path]::GetFullPath($resolvedExecutable),
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Started process path mismatch. Expected '$resolvedExecutable', got '$actualExecutable'."
    }

    Write-Host "[INFO] Started isolated NoGaReader PID $($launchedProcess.Id)."
    $deadline = [datetime]::UtcNow.AddSeconds($TimeoutSeconds)
    $mainWindow = Wait-MainWindow -Process $launchedProcess -Deadline $deadline
    Write-Host "[PASS] Main window appeared: $($mainWindow.Current.Name)"

    $consoleControlNames = @(
        '打开文件',
        '打开漫画图片文件夹',
        '添加书库文件夹',
        '管理书库文件夹',
        '刷新书库'
    )
    $readerControlNames = @(
        '高亮选中文字',
        '为选中文字添加笔记',
        '在当前位置添加书签',
        '打开书签与笔记'
    )
    if ($isComicFixture) {
        $readerControlNames += '打开漫画模式设置'
    }

    $controls = @{}
    foreach ($name in $consoleControlNames) {
        $control = Wait-NamedElement -Root $mainWindow -Name $name -Deadline $deadline
        $controls[$name] = $control
        Write-Host "[PASS] Found '$name'."
    }

    $readerMatch = Wait-ProcessWindowContainingElement `
        -Process $launchedProcess `
        -ElementName $readerControlNames[0] `
        -Deadline $deadline
    $readerWindow = $readerMatch.Window
    $controls[$readerControlNames[0]] = $readerMatch.Element
    Write-Host "[PASS] Reader window appeared: $($readerWindow.Current.Name)"
    Write-Host "[PASS] Found '$($readerControlNames[0])'."
    foreach ($name in $readerControlNames | Select-Object -Skip 1) {
        $control = Wait-NamedElement -Root $readerWindow -Name $name -Deadline $deadline
        $controls[$name] = $control
        Write-Host "[PASS] Found '$name'."
    }

    Wait-ElementEnabled -Element $controls['打开书签与笔记'] -Name '打开书签与笔记' -Deadline $deadline
    Invoke-AutomationElement -Element $controls['打开书签与笔记'] -Name '打开书签与笔记'
    $annotationList = Wait-NamedElement -Root $readerWindow -Name '批注列表' -Deadline $deadline
    $closeAnnotations = Wait-NamedElement -Root $readerWindow -Name '关闭批注面板' -Deadline $deadline
    if (-not $annotationList.Current.IsEnabled) {
        throw "The annotation list is present but not interactive."
    }

    Write-Host "[PASS] Annotation panel opened and its list is interactive."
    Invoke-AutomationElement -Element $closeAnnotations -Name '关闭批注面板'

    while ([datetime]::UtcNow -lt $deadline) {
        $closedElement = Find-NamedElement -Root $readerWindow -Name '关闭批注面板'
        if ($null -eq $closedElement -or $closedElement.Current.IsOffscreen) {
            break
        }

        Start-Sleep -Milliseconds 100
    }

    $stillOpen = Find-NamedElement -Root $readerWindow -Name '关闭批注面板'
    if ($null -ne $stillOpen -and -not $stillOpen.Current.IsOffscreen) {
        throw 'The annotation panel did not close after invoking its close button.'
    }

    Write-Host '[PASS] Annotation panel close action completed.'

    if ($isComicFixture) {
        Wait-ElementEnabled -Element $controls['打开漫画模式设置'] -Name '打开漫画模式设置' -Deadline $deadline
        Invoke-AutomationElement -Element $controls['打开漫画模式设置'] -Name '打开漫画模式设置'
        $comicPageList = Wait-NamedElement -Root $readerWindow -Name '漫画页缩略图' -Deadline $deadline
        $closeComicPanel = Wait-NamedElement -Root $readerWindow -Name '关闭漫画模式设置' -Deadline $deadline
        foreach ($name in @('单页', '双页', '连续', '左 → 右', '右 → 左', '宽', '高', '1:1')) {
            [void](Wait-NamedElement -Root $readerWindow -Name $name -Deadline $deadline)
        }
        if (-not $comicPageList.Current.IsEnabled) {
            throw 'The comic thumbnail list is present but not interactive.'
        }

        $doubleMode = Wait-NamedElement -Root $readerWindow -Name '双页' -Deadline $deadline
        Set-ToggleOn -Element $doubleMode -Name '双页'
        Start-Sleep -Milliseconds 300
        $nextPage = Wait-NamedElement -Root $readerWindow -Name '下一页' -Deadline $deadline
        $pageTurnTested = $false
        if ($nextPage.Current.IsEnabled) {
            Invoke-AutomationElement -Element $nextPage -Name '下一页'
            Start-Sleep -Milliseconds 300
            $pageTurnTested = $true
        }
        else {
            Write-Host '[INFO] Comic fixture has no next page; page-turn action skipped.'
        }
        $continuousMode = Wait-NamedElement -Root $readerWindow -Name '连续' -Deadline $deadline
        Set-ToggleOn -Element $continuousMode -Name '连续'

        if ($pageTurnTested) {
            Write-Host '[PASS] Comic panel is interactive; double-page turn and continuous-mode switch completed.'
        }
        else {
            Write-Host '[PASS] Comic panel is interactive; continuous-mode switch completed for a single-page fixture.'
        }
        Invoke-AutomationElement -Element $closeComicPanel -Name '关闭漫画模式设置'
    }

    Write-Host '[PASS] NoGaReader UI smoke test passed.'
    $succeeded = $true
}
catch {
    $failureMessage = $_.Exception.Message
}
finally {
    if ($null -ne $launchedProcess) {
        if ($KeepOpen) {
            if (-not $launchedProcess.HasExited) {
                Write-Host "[INFO] Keeping owned PID $($launchedProcess.Id) open."
                Write-Host "[INFO] Isolated data directory: $runDataRoot"
            }
        }
        else {
            Stop-OwnedProcess -Process $launchedProcess -Window $mainWindow
        }
    }

    if (-not $KeepOpen -and -not [string]::IsNullOrWhiteSpace($runDataRoot) -and
        [System.IO.Directory]::Exists($runDataRoot)) {
        try {
            $repositoryRootForCleanup = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
            $allowedCleanupRoot = [System.IO.Path]::GetFullPath(
                (Join-Path $repositoryRootForCleanup '.tmp\ui-smoke')) +
                [System.IO.Path]::DirectorySeparatorChar
            $resolvedCleanupTarget = [System.IO.Path]::GetFullPath($runDataRoot)
            if (-not $resolvedCleanupTarget.StartsWith(
                    $allowedCleanupRoot,
                    [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "Refusing to remove unexpected UI smoke directory '$resolvedCleanupTarget'."
            }

            $cleanupDeadline = [datetime]::UtcNow.AddSeconds(5)
            do {
                try {
                    [System.IO.Directory]::Delete($resolvedCleanupTarget, $true)
                    break
                }
                catch [System.IO.IOException] {
                    if ([datetime]::UtcNow -ge $cleanupDeadline) {
                        throw
                    }

                    # WebView2 can hold its own lock file briefly after the WPF
                    # parent exits. Wait for normal child shutdown; never target
                    # unrelated processes by name.
                    Start-Sleep -Milliseconds 200
                }
                catch [System.UnauthorizedAccessException] {
                    if ([datetime]::UtcNow -ge $cleanupDeadline) {
                        throw
                    }

                    Start-Sleep -Milliseconds 200
                }
            } while ([System.IO.Directory]::Exists($resolvedCleanupTarget))
        }
        catch {
            Write-Warning "Could not remove isolated UI smoke data: $($_.Exception.Message)"
        }
    }
}

if (-not $succeeded) {
    Write-Error "NoGaReader UI smoke test failed: $failureMessage"
    exit 1
}

exit 0
