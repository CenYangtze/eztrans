#requires -Version 7.0
<#
.SYNOPSIS
Runs an isolated x64 Windows GUI/IPC smoke test and, optionally, an Inno install/uninstall smoke test.
.DESCRIPTION
The application runs from a fresh copy of the complete SCD directory with portable.txt.
No model requests, captures, language-pack installation or startup registration are performed.
One dictionary lookup of the fixed word "hello" contacts the public dictionary service; its outcome is reported, not asserted.
The final process cleanup is forced; this does not verify the tray's normal Exit command.
Failure artifacts stay in the unique RUNNER_TEMP directory printed by this script.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $Executable,

    [string] $Installer
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $IsWindows) {
    Write-Output '[SKIP] This smoke test requires a real Windows desktop; GUI assertions were not executed.'
    exit 0
}
if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne [Runtime.InteropServices.Architecture]::X64) {
    Write-Output '[SKIP] This smoke test executes only the x64 Windows build; ARM64 execution was not tested.'
    exit 0
}

if (-not ('SnapTrans.Smoke.Native' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace SnapTrans.Smoke {
    public sealed class OwnedWindow {
        public IntPtr Handle { get; set; }
        public string Title { get; set; } = "";
        public bool Visible { get; set; }
    }
    public static class Native {
        private delegate bool EnumProc(IntPtr window, IntPtr parameter);
        [StructLayout(LayoutKind.Sequential)]
        private struct UserObjectFlags { public int Inherit; public int Reserved; public uint Flags; }
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool EnumWindows(EnumProc callback, IntPtr parameter);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetWindowText(IntPtr window, StringBuilder text, int maximum);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetProcessWindowStation();
        [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", SetLastError = true)]
        private static extern bool GetUserObjectInformation(IntPtr handle, int index,
            ref UserObjectFlags flags, int length, out int needed);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool CloseDesktop(IntPtr desktop);

        public static bool HasDesktop(out string reason) {
            var station = GetProcessWindowStation();
            var flags = new UserObjectFlags();
            if (station == IntPtr.Zero || !GetUserObjectInformation(station, 1, ref flags,
                Marshal.SizeOf<UserObjectFlags>(), out _) || (flags.Flags & 1) == 0) {
                reason = "The process window station is not visible; GUI smoke was not executed.";
                return false;
            }
            var desktop = OpenInputDesktop(0, false, 1); // DESKTOP_READOBJECTS; no desktop switching.
            if (desktop == IntPtr.Zero) {
                reason = "The input desktop is unavailable or inaccessible (Win32 " + Marshal.GetLastWin32Error() + ").";
                return false;
            }
            CloseDesktop(desktop);
            reason = "Visible input desktop is accessible.";
            return true;
        }
        public static OwnedWindow[] Windows(int ownerProcessId) {
            var result = new List<OwnedWindow>();
            if (!EnumWindows((window, parameter) => {
                GetWindowThreadProcessId(window, out uint processId);
                if (processId != (uint)ownerProcessId) return true;
                var text = new StringBuilder(2048);
                GetWindowText(window, text, text.Capacity);
                result.Add(new OwnedWindow { Handle = window, Title = text.ToString(), Visible = IsWindowVisible(window) });
                return true;
            }, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot enumerate the test process windows.");
            return result.ToArray();
        }
        public static void CloseOwnedWindow(IntPtr window, int ownerProcessId) {
            GetWindowThreadProcessId(window, out uint actual);
            if (actual != (uint)ownerProcessId) throw new InvalidOperationException("Refusing to close a window outside the test process.");
            if (!PostMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot post WM_CLOSE to the test process window.");
        }
    }
}
'@
}

function Write-Stage([string] $Stage, [string] $Message) {
    Write-Output "[$Stage] $Message"
}
function Assert-Smoke([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
}
function Wait-Smoke([scriptblock] $Condition, [int] $Seconds, [string] $Message) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    do {
        if (& $Condition) { return }
        Start-Sleep -Milliseconds 100
    } while ($clock.Elapsed.TotalSeconds -lt $Seconds)
    throw $Message
}
# Optional, never fatal: saves a PNG of a test window when SNAPTRANS_SCREENSHOT_DIR is set (CI UI review).
function Save-WindowShot([IntPtr] $Handle, [string] $Name) {
    if (-not $env:SNAPTRANS_SCREENSHOT_DIR) { return }
    try {
        if (-not ('SnapTrans.Smoke.Shot' -as [type])) {
            Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace SnapTrans.Smoke {
    public static class Shot {
        [StructLayout(LayoutKind.Sequential)]
        public struct Rect { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr window, out Rect rect);
        [DllImport("user32.dll")]
        public static extern bool PrintWindow(IntPtr window, IntPtr hdc, uint flags);
    }
}
'@
        }
        Add-Type -AssemblyName System.Drawing
        $rect = New-Object 'SnapTrans.Smoke.Shot+Rect'
        [void] [SnapTrans.Smoke.Shot]::GetWindowRect($Handle, [ref] $rect)
        $width = $rect.Right - $rect.Left
        $height = $rect.Bottom - $rect.Top
        if ($width -le 0 -or $height -le 0) { return }
        $bitmap = [System.Drawing.Bitmap]::new($width, $height)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $hdc = $graphics.GetHdc()
        [void] [SnapTrans.Smoke.Shot]::PrintWindow($Handle, $hdc, 2) # PW_RENDERFULLCONTENT
        $graphics.ReleaseHdc($hdc)
        $graphics.Dispose()
        New-Item -ItemType Directory -Force -Path $env:SNAPTRANS_SCREENSHOT_DIR | Out-Null
        $bitmap.Save((Join-Path $env:SNAPTRANS_SCREENSHOT_DIR "${Name}.png"), [System.Drawing.Imaging.ImageFormat]::Png)
        $bitmap.Dispose()
        Write-Stage 'SCREENSHOT' "Saved ${Name}.png (${width}x${height})."
    }
    catch { Write-Stage 'SCREENSHOT' "Skipped ${Name}: $($_.Exception.Message)" }
}
function Test-ProcessAlive([Diagnostics.Process] $Process) {
    $Process.Refresh()
    return -not $Process.HasExited
}
function Get-ApplicationWindows([int] $OwnerProcessId) {
    return @([SnapTrans.Smoke.Native]::Windows($OwnerProcessId) | Where-Object {
        $_.Visible -and ($_.Title.Contains('SnapTrans') -or $_.Title.Contains('屏译'))
    })
}
function Assert-NoExistingInstallation {
    # Inno's AppId and startup value are shared by this user's installations. Refuse
    # an existing installation rather than let a smoke uninstall alter user state.
    foreach ($view in @([Microsoft.Win32.RegistryView]::Registry64, [Microsoft.Win32.RegistryView]::Registry32)) {
        $registry = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, $view)
        try {
            $run = $registry.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Run')
            try {
                if ($null -ne $run -and $run.GetValueNames() -contains 'SnapTrans') {
                    throw 'An existing SnapTrans startup entry was found. Use a dedicated Windows test account; it was not changed.'
                }
            } finally { if ($null -ne $run) { $run.Dispose() } }
            $installed = $registry.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Uninstall\{13C03752-4AB4-4E02-942F-1D7273BE8210}_is1')
            try {
                if ($null -ne $installed) { throw 'An existing SnapTrans installation was found. Use a dedicated Windows test account; it was not changed.' }
            } finally { if ($null -ne $installed) { $installed.Dispose() } }
        } finally { $registry.Dispose() }
    }
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value.Replace('-', '_')
    $existing = $null
    if ([Threading.Mutex]::TryOpenExisting("Local\SnapTrans.$sid", [ref] $existing)) {
        $existing.Dispose()
        throw 'Another SnapTrans instance is already running for this user. It was not contacted or closed.'
    }
}

$smokeRoot = $null
$installationRoot = $null
$installationAttempted = $false
$testPassed = $false
$failure = $null
$applicationProcesses = [Collections.Generic.List[Diagnostics.Process]]::new()
$installerProcesses = [Collections.Generic.List[Diagnostics.Process]]::new()
$cleanupFailures = [Collections.Generic.List[string]]::new()

try {
    $sourceExe = (Resolve-Path -LiteralPath $Executable).Path
    Assert-Smoke ([IO.Path]::GetExtension($sourceExe) -ieq '.exe') 'Executable must identify the x64 SCD SnapTrans.exe.'
    $installerExe = if ($Installer) { (Resolve-Path -LiteralPath $Installer).Path } else { $null }
    $temporaryParent = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }
    $smokeRoot = Join-Path $temporaryParent ('SnapTrans-smoke-' + [Guid]::NewGuid().ToString('N'))
    $null = New-Item -ItemType Directory -Path $smokeRoot
    Write-Stage 'ARTIFACTS' $smokeRoot
    Assert-NoExistingInstallation
    $desktopReason = ''
    Assert-Smoke ([SnapTrans.Smoke.Native]::HasDesktop([ref] $desktopReason)) $desktopReason
    Write-Stage 'DESKTOP' 'A visible input desktop is accessible; real window assertions will run.'

    $payloadRoot = Join-Path $smokeRoot 'portable-app'
    Copy-Item -LiteralPath ([IO.Path]::GetDirectoryName($sourceExe)) -Destination $payloadRoot -Recurse
    # Clean only the private copy. An old developer run in out/scd must never carry
    # saved keys, startup preferences or automatic update settings into this test.
    foreach ($settingsFile in @(Get-ChildItem -LiteralPath $payloadRoot -Filter 'settings*.json*' -File)) {
        Remove-Item -LiteralPath $settingsFile.FullName -Force
    }
    $privateData = Join-Path $payloadRoot 'data'
    if (Test-Path -LiteralPath $privateData) { Remove-Item -LiteralPath $privateData -Recurse -Force }
    $null = New-Item -ItemType File -Path (Join-Path $payloadRoot 'portable.txt') -Force
    $stagedExe = Join-Path $payloadRoot ([IO.Path]::GetFileName($sourceExe))
    $settingsPath = Join-Path $payloadRoot 'settings.json'
    $logPath = Join-Path $payloadRoot 'data/logs'
    $pickerOpened = $false

    if ($installerExe) {
        $installationRoot = Join-Path $smokeRoot 'inno-installed'
        $group = 'SnapTrans-Smoke-' + [Guid]::NewGuid().ToString('N')
        $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER',
            ('/DIR="{0}"' -f $installationRoot), ('/GROUP="{0}"' -f $group),
            ('/LOG="{0}"' -f (Join-Path $smokeRoot 'installer.log')))
        $installationAttempted = $true
        $setup = Start-Process -FilePath $installerExe -ArgumentList $arguments -PassThru
        $installerProcesses.Add($setup)
        Write-Stage 'INSTALL' "Started the specified Inno installer, PID=$($setup.Id), target is the private scratch directory."
        Wait-Smoke { -not (Test-ProcessAlive $setup) } 60 'Inno installation did not finish within 60 seconds.'
        Assert-Smoke ($setup.ExitCode -eq 0) "Inno installation failed, exit=$($setup.ExitCode)."
        Assert-Smoke (Test-Path -LiteralPath (Join-Path $installationRoot 'SnapTrans.exe')) 'Installed SnapTrans.exe was not found.'
        Assert-Smoke (Test-Path -LiteralPath (Join-Path $installationRoot 'unins000.exe')) 'The Inno uninstaller was not found in the private installation.'
        Write-Stage 'INSTALL' 'PASS: silent current-user installation completed and installed executable/uninstaller exist.'
    }

    $first = Start-Process -FilePath $stagedExe -WorkingDirectory $payloadRoot -PassThru
    $applicationProcesses.Add($first)
    Write-Stage 'STARTUP' "Started private portable application, PID=$($first.Id)."
    Wait-Smoke {
        if (-not (Test-ProcessAlive $first)) { throw "The first instance exited during startup, exit=$($first.ExitCode)." }
        $settingsReady = (Test-Path -LiteralPath $settingsPath) -and (Get-Item -LiteralPath $settingsPath).Length -gt 0
        $logsReady = (Test-Path -LiteralPath $logPath) -and @(Get-ChildItem -LiteralPath $logPath -File -Filter '*.log').Count -gt 0
        $welcome = @(Get-ApplicationWindows $first.Id | Where-Object { $_.Title.Contains('欢迎') })
        return $settingsReady -and $logsReady -and $welcome.Count -gt 0
    } 20 'Startup did not generate private settings/logs and a real welcome window within 20 seconds.'
    Write-Stage 'STARTUP' 'PASS: isolated settings, logs and a real first-run window were observed.'
    Start-Sleep -Milliseconds 800
    foreach ($window in @(Get-ApplicationWindows $first.Id | Where-Object { $_.Title.Contains('欢迎') })) { Save-WindowShot $window.Handle 'welcome' }
    foreach ($window in @(Get-ApplicationWindows $first.Id | Where-Object { $_.Title.Contains('欢迎') })) {
        [SnapTrans.Smoke.Native]::CloseOwnedWindow($window.Handle, $first.Id)
    }
    Wait-Smoke {
        Assert-Smoke (Test-ProcessAlive $first) 'Closing the welcome window incorrectly terminated the first instance.'
        return @(Get-ApplicationWindows $first.Id | Where-Object { $_.Title.Contains('欢迎') }).Count -eq 0
    } 5 'The first-run window did not close.'

    $second = Start-Process -FilePath $stagedExe -WorkingDirectory $payloadRoot -PassThru
    $applicationProcesses.Add($second)
    Write-Stage 'SINGLE-INSTANCE' "Started second invocation, PID=$($second.Id)."
    Wait-Smoke {
        Assert-Smoke (Test-ProcessAlive $first) 'The original instance terminated during the IPC test.'
        $secondExited = -not (Test-ProcessAlive $second)
        if ($secondExited) { Assert-Smoke ($second.ExitCode -eq 0) "The second invocation failed, exit=$($second.ExitCode)." }
        $settingsWindows = @(Get-ApplicationWindows $first.Id | Where-Object { $_.Title.Contains('设置') })
        return $secondExited -and $settingsWindows.Count -gt 0
    } 10 'Within 10 seconds, the second invocation must exit 0 and IPC must open Settings in the first PID.'
    Write-Stage 'SINGLE-INSTANCE' 'PASS: second invocation exited 0; the original PID owns the visible Settings window.'
    Start-Sleep -Milliseconds 800
    foreach ($window in @(Get-ApplicationWindows $first.Id | Where-Object { $_.Title.Contains('设置') })) { Save-WindowShot $window.Handle 'settings' }

    # Visit every settings page and open one searchable picker through UI Automation, so a page
    # that throws while it is built or rendered is caught here instead of on a user's machine.
    try {
        Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
        $settingsWindow = @(Get-ApplicationWindows $first.Id | Where-Object { $_.Title.Contains('设置') })[0]
        $root = [System.Windows.Automation.AutomationElement]::FromHandle($settingsWindow.Handle)
        $byId = { param($id) New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id) }
        $nav = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (& $byId 'SettingsNav'))
        if ($null -eq $nav) { throw 'Settings navigation list was not found.' }
        $pages = $nav.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)
        for ($index = 0; $index -lt $pages.Count; $index++) {
            $pages[$index].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
            Start-Sleep -Milliseconds 600
            Assert-Smoke (Test-ProcessAlive $first) "The application exited while showing settings page $index."
            Save-WindowShot $settingsWindow.Handle "settings-page-$index"
            $picker = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (& $byId 'SearchPicker'))
            if ($null -ne $picker -and -not $pickerOpened) {
                $invoke = $picker.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
                $invoke.Invoke(); Start-Sleep -Milliseconds 600
                Assert-Smoke (Test-ProcessAlive $first) 'The application exited while opening a searchable picker.'
                $invoke.Invoke(); Start-Sleep -Milliseconds 400
                $pickerOpened = $true
            }
        }
        Write-Stage 'SETTINGS-TOUR' "Visited $($pages.Count) settings pages; searchable picker opened: $([bool] $pickerOpened)."
    }
    catch [System.Management.Automation.RuntimeException] {
        if ($_.Exception.Message -like '*exited while*') { throw }
        Write-Stage 'SETTINGS-TOUR' "SKIP: UI Automation tour unavailable: $($_.Exception.Message)"
    }

    # "--menu" asks the running instance to open its tray menu.
    $menuCall = Start-Process -FilePath $stagedExe -ArgumentList '--menu' -WorkingDirectory $payloadRoot -PassThru
    $applicationProcesses.Add($menuCall)
    $menuSeen = $false
    $menuClock = [Diagnostics.Stopwatch]::StartNew()
    while ($menuClock.Elapsed.TotalSeconds -lt 8 -and -not $menuSeen) {
        $menus = @(Get-ApplicationWindows $first.Id | Where-Object { $_.Title.Contains('菜单') })
        if ($menus.Count -gt 0) {
            $menuSeen = $true
            Start-Sleep -Milliseconds 500
            Save-WindowShot $menus[0].Handle 'tray-menu'
            [SnapTrans.Smoke.Native]::CloseOwnedWindow($menus[0].Handle, $first.Id)
        } else { Start-Sleep -Milliseconds 50 }
    }
    Assert-Smoke (Test-ProcessAlive $first) 'The application exited while opening the tray menu.'
    Write-Stage 'TRAY-MENU' $(if ($menuSeen) { 'PASS: themed tray menu window was observed.' } else { 'SKIP: tray menu window was not observed (it closes when it loses focus).' })

    # "--lookup hello" asks the running instance to open the dictionary card for one fixed word.
    # The answer depends on the runner's network: an entry or the error state must both render cleanly.
    $lookupCall = Start-Process -FilePath $stagedExe -ArgumentList @('--lookup', 'hello') -WorkingDirectory $payloadRoot -PassThru
    $applicationProcesses.Add($lookupCall)
    Wait-Smoke {
        Assert-Smoke (Test-ProcessAlive $first) 'The application exited while opening the dictionary card.'
        return @(Get-ApplicationWindows $first.Id | Where-Object { $_.Title.Contains('词典') }).Count -gt 0
    } 10 'The dictionary card did not open within 10 seconds of "--lookup hello".'
    $lookupClock = [Diagnostics.Stopwatch]::StartNew()
    $lookupLines = @()
    while ($lookupClock.Elapsed.TotalSeconds -lt 20 -and $lookupLines.Count -eq 0) {
        Start-Sleep -Milliseconds 300
        $lookupLines = @(Get-ChildItem -LiteralPath $logPath -File -Filter '*.log' | Select-String -Pattern 'Dictionary lookup')
    }
    Start-Sleep -Milliseconds 500
    Assert-Smoke (Test-ProcessAlive $first) 'The application exited while showing a dictionary answer.'
    $cards = @(Get-ApplicationWindows $first.Id | Where-Object { $_.Title.Contains('词典') })
    Assert-Smoke ($cards.Count -gt 0) 'The dictionary card closed before its answer was shown.'
    Save-WindowShot $cards[0].Handle 'dictionary'
    foreach ($line in $lookupLines) { Write-Stage 'DICTIONARY' ($line.Line -replace '^\S+ \S+ \S+ ', '') }
    Write-Stage 'DICTIONARY' "PASS: the dictionary card opened; lookup outcome lines: $($lookupLines.Count)."
    [SnapTrans.Smoke.Native]::CloseOwnedWindow($cards[0].Handle, $first.Id)

    Start-Sleep -Milliseconds 500
    $uiFailures = @(Get-ChildItem -LiteralPath $logPath -File -Filter '*.log' | Select-String -Pattern '界面操作失败|Tray menu failed|查词失败|Dictionary lookup failed' -SimpleMatch:$false)
    if ($uiFailures.Count -gt 0) { throw "The application logged UI failures: $(($uiFailures | Select-Object -First 3 | ForEach-Object Line) -join ' || ')" }
    Write-Stage 'UI-LOG' 'PASS: no UI failure was logged during the tour.'

    foreach ($window in @(Get-ApplicationWindows $first.Id)) {
        [SnapTrans.Smoke.Native]::CloseOwnedWindow($window.Handle, $first.Id)
    }
    Wait-Smoke {
        Assert-Smoke (Test-ProcessAlive $first) 'Closing application windows incorrectly terminated the tray-resident process.'
        return @(Get-ApplicationWindows $first.Id).Count -eq 0
    } 5 'Visible test application windows remained after WM_CLOSE.'
    $residence = [Diagnostics.Stopwatch]::StartNew()
    while ($residence.Elapsed.TotalSeconds -lt 1) {
        Assert-Smoke (Test-ProcessAlive $first) 'The first instance exited after its windows were closed.'
        Start-Sleep -Milliseconds 100
    }
    Write-Stage 'RESIDENCE' 'PASS: closing windows leaves the original process resident. Normal tray Exit was not tested.'
    $testPassed = $true
}
catch {
    $failure = $_.Exception.Message
    Write-Stage 'FAIL' $failure
}
finally {
    foreach ($process in $applicationProcesses) {
        try {
            if (Test-ProcessAlive $process) {
                $process.Kill($true)
                Assert-Smoke ($process.WaitForExit(10000)) "Test PID=$($process.Id) did not stop during forced cleanup."
                Write-Stage 'CLEANUP' "Forced termination of only the application started by this test, PID=$($process.Id)."
            }
        } catch { $cleanupFailures.Add($_.Exception.Message) }
        finally { $process.Dispose() }
    }
    foreach ($process in $installerProcesses) {
        try {
            if (Test-ProcessAlive $process) { $process.Kill($true); $null = $process.WaitForExit(10000) }
        } catch { $cleanupFailures.Add($_.Exception.Message) }
        finally { $process.Dispose() }
    }
    if ($installationAttempted -and $installationRoot) {
        $uninstaller = Join-Path $installationRoot 'unins000.exe'
        if (Test-Path -LiteralPath $uninstaller) {
            try {
                $uninstall = Start-Process -FilePath $uninstaller -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART',
                    ('/LOG="{0}"' -f (Join-Path $smokeRoot 'uninstaller.log'))) -WorkingDirectory $smokeRoot -PassThru
                try {
                    Write-Stage 'UNINSTALL' "Started only the private installation's uninstaller, PID=$($uninstall.Id)."
                    Wait-Smoke { -not (Test-ProcessAlive $uninstall) } 60 'Inno uninstall did not finish within 60 seconds.'
                    Assert-Smoke ($uninstall.ExitCode -eq 0) "Inno uninstall failed, exit=$($uninstall.ExitCode)."
                    Wait-Smoke {
                        -not (Test-Path -LiteralPath (Join-Path $installationRoot 'SnapTrans.exe')) -and
                        -not (Test-Path -LiteralPath $uninstaller)
                    } 10 'Uninstall did not remove the private executable and uninstaller.'
                    Write-Stage 'UNINSTALL' 'PASS: silent uninstall completed and removed the private installed executables.'
                } finally {
                    if (Test-ProcessAlive $uninstall) { $uninstall.Kill($true); $null = $uninstall.WaitForExit(10000) }
                    $uninstall.Dispose()
                }
            } catch { $cleanupFailures.Add($_.Exception.Message) }
        } else {
            $cleanupFailures.Add('The attempted private installation has no uninstaller; installer cleanup could not be verified.')
        }
    }
    if ($cleanupFailures.Count -gt 0) {
        $testPassed = $false
        foreach ($message in $cleanupFailures) { Write-Stage 'CLEANUP-FAIL' $message }
    }
    if ($smokeRoot) {
        if ($testPassed) {
            try { Remove-Item -LiteralPath $smokeRoot -Recurse -Force }
            catch { $testPassed = $false; Write-Stage 'CLEANUP-FAIL' 'Private scratch cleanup failed.' }
        }
        if (-not $testPassed) { Write-Stage 'ARTIFACTS' "Failure artifacts retained: $smokeRoot" }
    }
}

if (-not $testPassed) { exit 1 }
Write-Stage 'PASS' 'Real Windows GUI/IPC/residence checks completed; optional Inno install/uninstall checks passed when requested.'
exit 0
