$script:BetterGIRdpDefaultTargetAddress = '127.0.0.2'

function Disable-BetterGIChildSessions {
    param([scriptblock]$LogAction)

    if (-not ('BetterGIWtsChildSessionMode' -as [type])) {
        Add-Type @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

public static class BetterGIWtsChildSessionMode
{
    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSEnableChildSessions(
        [MarshalAs(UnmanagedType.Bool)] bool enable);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSIsChildSessionsEnabled(
        [MarshalAs(UnmanagedType.Bool)] out bool enabled);

    public static bool IsEnabled()
    {
        bool enabled;
        if (!WTSIsChildSessionsEnabled(out enabled))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        return enabled;
    }

    public static void Disable()
    {
        if (!WTSEnableChildSessions(false))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }
}
'@
    }

    if ([BetterGIWtsChildSessionMode]::IsEnabled()) {
        Write-BetterGIRdpLifecycleLog `
            -LogAction $LogAction `
            -Message 'Disabling Windows Child Session mode before starting legacy loopback RDP.'
        [BetterGIWtsChildSessionMode]::Disable()
    }
    else {
        Write-BetterGIRdpLifecycleLog `
            -LogAction $LogAction `
            -Message 'Windows Child Session mode is already disabled.'
    }
}

function Write-BetterGIRdpLifecycleLog {
    param(
        [scriptblock]$LogAction,
        [string]$Message
    )

    if ($LogAction) {
        & $LogAction $Message
    }
}

function Get-BetterGIRdpUserSid {
    param([Parameter(Mandatory = $true)][string]$UserName)

    try {
        $account = [Security.Principal.NTAccount]::new($env:COMPUTERNAME, $UserName)
        return $account.Translate([Security.Principal.SecurityIdentifier])
    }
    catch {
        throw "Unable to resolve local RDP user '$UserName' to a SID: $($_.Exception.Message)"
    }
}

function Test-BetterGIGHubDenyRule {
    param(
        [Parameter(Mandatory = $true)]
        [Security.AccessControl.DirectorySecurity]$Acl,

        [Parameter(Mandatory = $true)]
        [Security.Principal.SecurityIdentifier]$UserSid
    )

    $requiredRights = [Security.AccessControl.FileSystemRights]::ReadAndExecute
    foreach ($rule in $Acl.Access) {
        try {
            $ruleSid = $rule.IdentityReference.Translate(
                [Security.Principal.SecurityIdentifier]).Value
        }
        catch {
            continue
        }

        $hasRequiredRights = ($rule.FileSystemRights -band $requiredRights) -eq $requiredRights
        $hasContainerInheritance = (
            $rule.InheritanceFlags -band
            [Security.AccessControl.InheritanceFlags]::ContainerInherit) -ne 0
        $hasObjectInheritance = (
            $rule.InheritanceFlags -band
            [Security.AccessControl.InheritanceFlags]::ObjectInherit) -ne 0

        if (
            $ruleSid -eq $UserSid.Value -and
            $rule.AccessControlType -eq [Security.AccessControl.AccessControlType]::Deny -and
            $hasRequiredRights -and
            $hasContainerInheritance -and
            $hasObjectInheritance
        ) {
            return $true
        }
    }

    return $false
}

function Ensure-BetterGIGHubRdpIsolation {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RdpUserName,

        [Parameter(Mandatory = $true)]
        [string]$GHubInstallPath,

        [switch]$NoLaunch,

        [scriptblock]$LogAction
    )

    $targetSid = Get-BetterGIRdpUserSid -UserName $RdpUserName
    if (-not (Test-Path -LiteralPath $GHubInstallPath -PathType Container)) {
        Write-BetterGIRdpLifecycleLog `
            -LogAction $LogAction `
            -Message ("G HUB is not installed at '{0}'; no RDP-user executable access exists to isolate." -f $GHubInstallPath)
        return [PSCustomObject]@{
            installPath = $GHubInstallPath
            targetUser = $RdpUserName
            targetSid = $targetSid.Value
            installed = $false
            configured = $true
            changed = $false
            wouldChange = $false
        }
    }

    $resolvedInstallPath = (Resolve-Path -LiteralPath $GHubInstallPath).Path
    $acl = Get-Acl -LiteralPath $resolvedInstallPath
    $configured = Test-BetterGIGHubDenyRule -Acl $acl -UserSid $targetSid
    if ($configured) {
        Write-BetterGIRdpLifecycleLog `
            -LogAction $LogAction `
            -Message ("G HUB RDP isolation is already configured for {0} ({1}) at '{2}'." -f $RdpUserName, $targetSid.Value, $resolvedInstallPath)
        return [PSCustomObject]@{
            installPath = $resolvedInstallPath
            targetUser = $RdpUserName
            targetSid = $targetSid.Value
            installed = $true
            configured = $true
            changed = $false
            wouldChange = $false
        }
    }

    if ($NoLaunch) {
        Write-BetterGIRdpLifecycleLog `
            -LogAction $LogAction `
            -Message ("NoLaunch validation: would deny G HUB ReadAndExecute access to {0} ({1}) at '{2}'." -f $RdpUserName, $targetSid.Value, $resolvedInstallPath)
        return [PSCustomObject]@{
            installPath = $resolvedInstallPath
            targetUser = $RdpUserName
            targetSid = $targetSid.Value
            installed = $true
            configured = $false
            changed = $false
            wouldChange = $true
        }
    }

    $currentSid = [Security.Principal.WindowsIdentity]::GetCurrent().User
    if ($currentSid -and $currentSid.Value -eq $targetSid.Value) {
        throw "Refusing to deny G HUB access while running as the target RDP user '$RdpUserName'."
    }

    $denyRule = [Security.AccessControl.FileSystemAccessRule]::new(
        $targetSid,
        [Security.AccessControl.FileSystemRights]::ReadAndExecute,
        [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit',
        [Security.AccessControl.PropagationFlags]::None,
        [Security.AccessControl.AccessControlType]::Deny)
    $null = $acl.AddAccessRule($denyRule)
    Set-Acl -LiteralPath $resolvedInstallPath -AclObject $acl

    $verifiedAcl = Get-Acl -LiteralPath $resolvedInstallPath
    if (-not (Test-BetterGIGHubDenyRule -Acl $verifiedAcl -UserSid $targetSid)) {
        throw "G HUB RDP isolation ACL verification failed for '$RdpUserName' at '$resolvedInstallPath'."
    }

    Write-BetterGIRdpLifecycleLog `
        -LogAction $LogAction `
        -Message ("Applied G HUB RDP isolation for {0} ({1}) at '{2}'; other users and SYSTEM are unchanged." -f $RdpUserName, $targetSid.Value, $resolvedInstallPath)
    [PSCustomObject]@{
        installPath = $resolvedInstallPath
        targetUser = $RdpUserName
        targetSid = $targetSid.Value
        installed = $true
        configured = $true
        changed = $true
        wouldChange = $false
    }
}

function ConvertTo-BetterGIRdpProcessRecord {
    param($Process)

    if (-not $Process) {
        return $null
    }

    [PSCustomObject]@{
        processId = [int]$Process.ProcessId
        name = [string]$Process.Name
        creationDate = [string]$Process.CreationDate
        commandLine = [string]$Process.CommandLine
    }
}

function Get-BetterGIRdpProcessRecordById {
    param([int]$ProcessId)

    ConvertTo-BetterGIRdpProcessRecord -Process (
        Get-CimInstance Win32_Process -Filter "ProcessId = $ProcessId" -ErrorAction SilentlyContinue
    )
}

function ConvertFrom-BetterGIRdpCimDate {
    param($Value)

    if (-not $Value) {
        return $null
    }
    if ($Value -is [datetime]) {
        return $Value
    }
    try {
        return [Management.ManagementDateTimeConverter]::ToDateTime([string]$Value)
    }
    catch {
        return $null
    }
}

# The readiness budget is shared by all probes and warning confirmation calls.
# UTC can expire it earlier; clock corrections cannot extend the monotonic deadline.
function Get-BetterGIRdpUtcNow { [datetime]::UtcNow }
function Get-BetterGIRdpMonotonicMilliseconds { [System.Diagnostics.Stopwatch]::GetTimestamp() * 1000.0 / [System.Diagnostics.Stopwatch]::Frequency }
function New-BetterGIRdpReadinessBudget {
    param([double]$Milliseconds, $ParentBudget)
    $millisecondsLeft = [Math]::Max(0, $Milliseconds)
    $utcDeadline = (Get-BetterGIRdpUtcNow).AddMilliseconds($millisecondsLeft)
    $monoDeadline = (Get-BetterGIRdpMonotonicMilliseconds) + $millisecondsLeft
    if ($ParentBudget) {
        if ($ParentBudget.UtcDeadline -lt $utcDeadline) { $utcDeadline = $ParentBudget.UtcDeadline }
        $monoDeadline = [Math]::Min($monoDeadline, $ParentBudget.MonotonicDeadline)
    }
    [pscustomobject]@{ UtcDeadline = $utcDeadline; MonotonicDeadline = $monoDeadline }
}
function Get-BetterGIRdpRemainingMilliseconds {
    param($Budget)
    $utc = ($Budget.UtcDeadline - (Get-BetterGIRdpUtcNow)).TotalMilliseconds
    $mono = $Budget.MonotonicDeadline - (Get-BetterGIRdpMonotonicMilliseconds)
    [int][Math]::Min([int]::MaxValue, [Math]::Max(0, [Math]::Floor([Math]::Min($utc, $mono))))
}
function Assert-BetterGIRdpReadinessBudget {
    param($Budget)
    if ((Get-BetterGIRdpRemainingMilliseconds $Budget) -le 0) { throw [TimeoutException]::new('RDP readiness deadline expired.') }
}
function Wait-BetterGIRdpReadinessPoll {
    param($Budget, [int]$Milliseconds)
    $remaining = Get-BetterGIRdpRemainingMilliseconds $Budget
    if ($remaining -gt 0) { Start-Sleep -Milliseconds ([Math]::Min($Milliseconds, $remaining)) }
}

function Initialize-BetterGIRdpNativeWarning {
    if (-not ('BetterGIRdpNativeClick' -as [type])) {
        Add-Type @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
public sealed class BetterGIRdpWarningControl {
    public long Handle; public int ProcessId; public int Id;
    public string ClassName; public string Text; public bool Visible; public bool Enabled;
}
public sealed class BetterGIRdpWarningWindow {
    public long Handle; public int ProcessId; public string ClassName;
    public string Title; public string Text; public bool Complete;
    public BetterGIRdpWarningControl[] Children;
}
public sealed class BetterGIRdpWarningSendResult {
    public bool Attempted; public bool Acknowledged; public bool Uncertain;
}
public static class BetterGIRdpNativeClick {
    private delegate bool EnumCallback(IntPtr handle, IntPtr unused);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumCallback callback, IntPtr unused);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumCallback callback, IntPtr unused);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool IsChild(IntPtr parent, IntPtr child);
    [DllImport("user32.dll")] private static extern int GetDlgCtrlID(IntPtr handle);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetClassName(IntPtr handle, StringBuilder name, int count);
    [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern IntPtr SendMessageTimeout(IntPtr handle, uint message, UIntPtr wParam, StringBuilder text, uint flags, uint timeout, out UIntPtr result);
    [DllImport("user32.dll", EntryPoint="SendMessageTimeoutW", SetLastError=true)] private static extern IntPtr SendClickTimeout(IntPtr handle, uint message, UIntPtr wParam, IntPtr unused, uint flags, uint timeout, out UIntPtr result);
    [DllImport("kernel32.dll")] public static extern uint GetOEMCP();
    private static long Deadline(int milliseconds) { return Stopwatch.GetTimestamp() + (long)(milliseconds * (double)Stopwatch.Frequency / 1000); }
    private static int Remaining(long deadline) { return (int)Math.Max(0, Math.Min(Int32.MaxValue, (deadline - Stopwatch.GetTimestamp()) * 1000.0 / Stopwatch.Frequency)); }
    private static bool Owned(IntPtr handle, int processId) { uint owner; GetWindowThreadProcessId(handle, out owner); return handle != IntPtr.Zero && owner == processId && IsWindowVisible(handle); }
    private static string ClassOf(IntPtr handle) { var text = new StringBuilder(256); return GetClassName(handle, text, text.Capacity) > 0 ? text.ToString() : ""; }
    private static string TextOf(IntPtr handle, long deadline) {
        int remaining = Remaining(deadline); if (remaining <= 0) return null;
        var text = new StringBuilder(4096); UIntPtr result;
        if (SendMessageTimeout(handle, 0x000D, (UIntPtr)text.Capacity, text, 0x23, (uint)remaining, out result) == IntPtr.Zero) return null;
        return Remaining(deadline) > 0 ? text.ToString() : null;
    }
    private static BetterGIRdpWarningWindow ReadWindow(IntPtr handle, int processId, long deadline) {
        if (Remaining(deadline) <= 0 || !Owned(handle, processId) || ClassOf(handle) != "#32770") return null;
        string title = TextOf(handle, deadline); if (title == null) return null;
        var controls = new List<BetterGIRdpWarningControl>(); bool complete = true; int visited = 0;
        EnumChildWindows(handle, (child, unused) => {
            if (Remaining(deadline) <= 0 || ++visited > 128) { complete = false; return false; }
            if (!Owned(child, processId)) return true;
            string text = TextOf(child, deadline);
            if (text == null || text.Length >= 4095) { complete = false; return false; }
            controls.Add(new BetterGIRdpWarningControl { Handle=child.ToInt64(), ProcessId=processId, Id=GetDlgCtrlID(child), ClassName=ClassOf(child), Text=text, Visible=true, Enabled=IsWindowEnabled(child) });
            return true;
        }, IntPtr.Zero);
        if (Remaining(deadline) <= 0) complete = false;
        var names = new StringBuilder(title);
        foreach (var control in controls) names.Append('\n').Append(control.Text);
        return new BetterGIRdpWarningWindow { Handle=handle.ToInt64(), ProcessId=processId, ClassName="#32770", Title=title, Text=names.ToString(), Complete=complete, Children=controls.ToArray() };
    }
    public static BetterGIRdpWarningWindow[] ReadWindows(int processId, int milliseconds) {
        long deadline = Deadline(milliseconds); var windows = new List<BetterGIRdpWarningWindow>(); int visited = 0;
        bool complete = EnumWindows((handle, unused) => {
            if (Remaining(deadline) <= 0) return false;
            if (!Owned(handle, processId)) return true;
            if (++visited > 16) return false;
            var window = ReadWindow(handle, processId, deadline); if (window != null) windows.Add(window);
            return true;
        }, IntPtr.Zero);
        // A truncated enumeration cannot establish that the warning is unique.
        if (!complete || Remaining(deadline) <= 0) return new BetterGIRdpWarningWindow[0];
        return windows.ToArray();
    }
    public static BetterGIRdpWarningSendResult Click(BetterGIRdpWarningWindow expected, long buttonHandle, int processId, int milliseconds) {
        var result = new BetterGIRdpWarningSendResult(); long deadline = Deadline(milliseconds);
        var current = ReadWindow(new IntPtr(expected.Handle), processId, deadline);
        if (current == null || !current.Complete || !expected.Complete || !String.Equals(expected.Text, current.Text, StringComparison.Ordinal)) return result;
        BetterGIRdpWarningControl button = null;
        foreach (var control in current.Children) if (control.Handle == buttonHandle) button = control;
        if (button == null || button.ClassName != "Button" || !button.Visible || !button.Enabled || !Owned(new IntPtr(buttonHandle), processId) || !IsChild(new IntPtr(expected.Handle), new IntPtr(buttonHandle))) return result;
        int remaining = Remaining(deadline); if (remaining <= 0) return result;
        result.Attempted = true; UIntPtr ignored;
        result.Acknowledged = SendClickTimeout(new IntPtr(buttonHandle), 0x00F5, UIntPtr.Zero, IntPtr.Zero, 0x23, (uint)remaining, out ignored) != IntPtr.Zero;
        result.Uncertain = !result.Acknowledged;
        return result;
    }
}
'@
    }
}
function Get-BetterGIRdpVisibleTopLevelWindowElements {
    param([int]$ProcessId, $Budget)
    Initialize-BetterGIRdpNativeWarning
    $remaining = Get-BetterGIRdpRemainingMilliseconds $Budget
    if ($remaining -le 0) { return @() }
    @([BetterGIRdpNativeClick]::ReadWindows($ProcessId, $remaining))
}
function Send-BetterGIRdpWarningClick {
    param($Window, $Button, [int]$ProcessId, $Budget)
    $remaining = Get-BetterGIRdpRemainingMilliseconds $Budget
    if ($remaining -le 0) { return [pscustomobject]@{ Attempted=$false;Acknowledged=$false;Uncertain=$false } }
    [BetterGIRdpNativeClick]::Click($Window, [long]$Button.Handle, $ProcessId, $remaining)
}
function New-BetterGIRdpSessionProbeProcess {
    Initialize-BetterGIRdpNativeWarning
    $encoding = [Text.Encoding]::GetEncoding([int][BetterGIRdpNativeClick]::GetOEMCP())
    $info = New-Object System.Diagnostics.ProcessStartInfo
    $info.FileName = Join-Path $env:SystemRoot 'System32\qwinsta.exe'
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.StandardOutputEncoding = $encoding
    $info.StandardErrorEncoding = $encoding
    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $info
    $process
}
function Invoke-BetterGIRdpSessionProbe {
    param($Budget)
    Assert-BetterGIRdpReadinessBudget $Budget
    $process = New-BetterGIRdpSessionProbeProcess
    $started = $false
    $ownedProbe = 'owned readonly qwinsta probe (PID unavailable)'
    $failure = $null
    $lines = @()
    try {
        Assert-BetterGIRdpReadinessBudget $Budget
        $started = $process.Start()
        if (-not $started) { throw 'Unable to start the read-only RDP session probe.' }
        $ownedProbe = 'owned readonly qwinsta probe PID={0}' -f $process.Id
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $remaining = Get-BetterGIRdpRemainingMilliseconds $Budget
        if ($remaining -le 0 -or -not $process.WaitForExit($remaining)) { throw [TimeoutException]::new('RDP session probe exhausted the readiness budget.') }
        foreach ($read in @($stdout, $stderr)) {
            $remaining = Get-BetterGIRdpRemainingMilliseconds $Budget
            if ($remaining -le 0 -or -not $read.Wait($remaining)) { throw [TimeoutException]::new('RDP session output exhausted the readiness budget.') }
        }
        Assert-BetterGIRdpReadinessBudget $Budget
        if ($process.ExitCode -eq 0) { $lines = @([regex]::Split($stdout.Result, '\r?\n')) }
    }
    catch { $failure = $_.Exception }
    finally {
        try {
            if ($started -and -not $process.HasExited) {
                # Kill accepts a termination request; it does not confirm exit.
                # Never create a cleanup deadline after the original budget ends.
                $process.Kill()
                $remaining = Get-BetterGIRdpRemainingMilliseconds $Budget
                if ($remaining -le 0) { throw [TimeoutException]::new('Original readiness budget ended before owned probe exit could be confirmed.') }
                if (-not $process.HasExited -and -not $process.WaitForExit($remaining)) {
                    throw [InvalidOperationException]::new('Owned probe is still running after the original remaining budget.')
                }
                if ((Get-BetterGIRdpRemainingMilliseconds $Budget) -le 0 -or -not $process.HasExited) {
                    throw [InvalidOperationException]::new('Owned probe exit was not confirmed within the original readiness budget.')
                }
            }
        }
        catch {
            $cleanup = $_.Exception
            if ($failure) { $failure = [AggregateException]::new("$ownedProbe termination is uncertain.", [Exception[]]@($failure, $cleanup)) }
            else { $failure = [InvalidOperationException]::new("$ownedProbe termination is uncertain.", $cleanup) }
        }
        finally { $process.Dispose() }
    }
    if ($failure) { throw $failure }
    $lines
}

function Confirm-BetterGIRdpFileSecurityWarning {
    param(
        [int]$ProcessId,
        [string]$TargetAddress,
        [string]$RdpUserName,
        [int]$TimeoutSeconds = 10,
        [switch]$AllowTargetOmissionForTrackedClient,
        [scriptblock]$LogAction,
        $ParentBudget
    )
    if ($ProcessId -le 0 -or [string]::IsNullOrWhiteSpace($TargetAddress) -or [string]::IsNullOrWhiteSpace($RdpUserName)) { return $false }
    $parsedAddress = $null
    if (-not [Net.IPAddress]::TryParse($TargetAddress, [ref]$parsedAddress) -or -not [Net.IPAddress]::IsLoopback($parsedAddress)) { return $false }
    $budget = New-BetterGIRdpReadinessBudget -Milliseconds ($TimeoutSeconds * 1000.0) -ParentBudget $ParentBudget
    if (-not (Get-Variable -Scope Script -Name BetterGIRdpWarningAttempts -ErrorAction SilentlyContinue)) { $script:BetterGIRdpWarningAttempts = @{} }
    while ((Get-BetterGIRdpRemainingMilliseconds $budget) -gt 0) {
        $process = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
        if ((Get-BetterGIRdpRemainingMilliseconds $budget) -le 0) { return $false }
        if (-not $process -or $process.ProcessName -ine 'mstsc') { return $false }
        $windows = @(Get-BetterGIRdpVisibleTopLevelWindowElements -ProcessId $ProcessId -Budget $budget)
        if ((Get-BetterGIRdpRemainingMilliseconds $budget) -le 0) { return $false }
        $eligible = @()
        foreach ($window in $windows) {
            if (-not $window.Complete -or $window.ProcessId -ne $ProcessId -or [long]$window.Handle -eq 0 -or $window.ClassName -ne '#32770') { continue }
            $hasWarningTitle = $window.Title -match '安全警告|Security Warning'
            $mentionsTarget = $window.Text -match [regex]::Escape($TargetAddress)
            $mentionsUser = [string]::IsNullOrWhiteSpace($RdpUserName) -or $window.Text -match [regex]::Escape($RdpUserName)
            # Only the standard local-file publisher warning, never certificate or remote desktop content.
            if ($window.Text -notmatch '发布者|publisher') { continue }
            if (-not $hasWarningTitle -and -not $mentionsTarget) { continue }
            if (-not $mentionsTarget -and (-not $AllowTargetOmissionForTrackedClient -or -not $hasWarningTitle)) { continue }
            if (-not $mentionsUser -and (-not $AllowTargetOmissionForTrackedClient -or -not $hasWarningTitle)) { continue }
            $buttons = @($window.Children | Where-Object { $_.ProcessId -eq $ProcessId -and [long]$_.Handle -ne 0 -and $_.ClassName -eq 'Button' -and $_.Visible -and $_.Enabled -and $_.Text -match '连接|Connect|^是(?:\W|$)|^Yes(?:\W|$)' })
            $preferred = @($buttons | Where-Object { $_.Id -eq 1 })
            if ($preferred.Count -eq 1) { $button = $preferred[0] }
            elseif ($preferred.Count -eq 0 -and $buttons.Count -eq 1) { $button = $buttons[0] }
            else { continue }
            $eligible += [pscustomobject]@{ Window=$window;Button=$button }
        }
        if ($eligible.Count -gt 1) { return $false }
        if ($eligible.Count -eq 1) {
            $candidate = $eligible[0]
            $key = '{0}:{1}' -f $ProcessId, $candidate.Window.Handle
            if ($script:BetterGIRdpWarningAttempts.ContainsKey($key)) {
                if ($script:BetterGIRdpWarningAttempts[$key] -eq 'acknowledged') { return ((Get-BetterGIRdpRemainingMilliseconds $budget) -gt 0) }
                throw 'RDP warning input was already attempted with an unknown result; refusing repeat input.'
            }
            Write-BetterGIRdpLifecycleLog -LogAction $LogAction -Message 'Confirming the trusted local RDP file warning for the tracked client; session readiness is still required.'
            if ((Get-BetterGIRdpRemainingMilliseconds $budget) -le 0) { return $false }
            $script:BetterGIRdpWarningAttempts[$key] = 'unknown'
            $result = Send-BetterGIRdpWarningClick -Window $candidate.Window -Button $candidate.Button -ProcessId $ProcessId -Budget $budget
            if (-not $result.Attempted) { $script:BetterGIRdpWarningAttempts.Remove($key); return $false }
            if ($result.Uncertain -or -not $result.Acknowledged) { throw 'RDP warning input result is unknown; refusing repeat input.' }
            $script:BetterGIRdpWarningAttempts[$key] = 'acknowledged'
            Assert-BetterGIRdpReadinessBudget $budget
            return $true
        }
        Wait-BetterGIRdpReadinessPoll -Budget $budget -Milliseconds 500
    }
    return $false
}


function Get-BetterGIRdpClientProcess {
    param(
        [datetime]$StartedAfter,
        [int[]]$ExcludeProcessId = @(),
        [string]$RdpFilePath,
        [string]$TargetAddress = $script:BetterGIRdpDefaultTargetAddress
    )

    $escapedRdpFilePath = if ($RdpFilePath) { [regex]::Escape($RdpFilePath) } else { $null }
    $escapedTargetAddress = [regex]::Escape($TargetAddress)

    @(Get-CimInstance Win32_Process -Filter "Name = 'mstsc.exe'" -ErrorAction SilentlyContinue | Where-Object {
        if ($ExcludeProcessId -contains [int]$_.ProcessId) {
            return $false
        }

        $createdAt = ConvertFrom-BetterGIRdpCimDate -Value $_.CreationDate
        $matchesStart = -not $StartedAfter -or (-not $createdAt) -or $createdAt -ge $StartedAfter.AddSeconds(-5)
        $commandLine = [string]$_.CommandLine
        $matchesTarget = $commandLine -match $escapedTargetAddress
        if (-not $matchesTarget -and $escapedRdpFilePath) {
            $matchesTarget = $commandLine -match $escapedRdpFilePath
        }

        $matchesStart -and $matchesTarget
    } | ForEach-Object { ConvertTo-BetterGIRdpProcessRecord -Process $_ })
}

function Get-BetterGIRdpUserSession {
    param([string]$UserName, $Budget, [ref]$RawSessionLines)
    $sessions = if ($Budget) { @(Invoke-BetterGIRdpSessionProbe -Budget $Budget) } else { @(& qwinsta.exe 2>$null) }
    if ($RawSessionLines) { $RawSessionLines.Value = @($sessions) }
    foreach ($line in $sessions) {
        $sessionName = ''
        if ($line -match '^\s*>?(?<sessionName>\S+)\s+(?<userName>\S+)\s+(?<id>\d+)\s+(?<state>\S+)') { $sessionName = $Matches.sessionName }
        elseif ($line -notmatch '^\s*>?(?<userName>\S+)\s+(?<id>\d+)\s+(?<state>\S+)') { continue }
        if ($sessionName -and $sessionName -notlike 'rdp-tcp#*') { continue }
        if (-not $Matches.userName.Equals($UserName, [StringComparison]::OrdinalIgnoreCase)) { continue }
        [pscustomobject]@{ sessionName=$sessionName;userName=$Matches.userName;id=[int]$Matches.id;state=$Matches.state }
    }
}

function Get-BetterGIRdpSessionById {
    param([int]$SessionId)

    $sessions = @(& qwinsta.exe 2>$null)
    foreach ($line in $sessions) {
        $sessionName = ''
        if ($line -match '^\s*>?(?<sessionName>\S+)\s+(?<userName>\S+)\s+(?<id>\d+)\s+(?<state>\S+)') {
            $sessionName = $Matches.sessionName
        }
        elseif ($line -notmatch '^\s*>?(?<userName>\S+)\s+(?<id>\d+)\s+(?<state>\S+)') {
            continue
        }

        if ([int]$Matches.id -ne $SessionId) {
            continue
        }

        [PSCustomObject]@{
            sessionName = $sessionName
            userName = $Matches.userName
            id = [int]$Matches.id
            state = $Matches.state
        }
    }
}

function Wait-BetterGIRdpSessionReadyForUser {
    param(
        [string]$UserName,
        [int]$TimeoutSeconds = 120,
        [int[]]$RdpClientProcessIds = @(),
        [string]$TargetAddress,
        [string]$RdpUserName,
        [switch]$AllowTargetOmissionForTrackedClient,
        [scriptblock]$LogAction
    )
    $budget = New-BetterGIRdpReadinessBudget -Milliseconds ($TimeoutSeconds * 1000.0)
    $lastObserved = $null
    $securityWarningConfirmed = $false
    $securityWarningAttempted = $false
    $nextWarning = Get-BetterGIRdpMonotonicMilliseconds
    if ([string]::IsNullOrWhiteSpace($RdpUserName)) { $RdpUserName = $UserName }
    try {
        while ((Get-BetterGIRdpRemainingMilliseconds $budget) -gt 0) {
            $raw = @()
            $session = @(Get-BetterGIRdpUserSession -UserName $UserName -Budget $budget -RawSessionLines ([ref]$raw) | Where-Object { $_.sessionName -like 'rdp-tcp#*' -and $_.state -match '^(Active|运行中)$' } | Sort-Object id -Descending | Select-Object -First 1)
            Assert-BetterGIRdpReadinessBudget $budget
            if ($session.Count -gt 0) {
                Write-BetterGIRdpLifecycleLog -LogAction $LogAction -Message ("Detected RDP session for {0}: {1} id {2} state {3}." -f $UserName,$session[0].sessionName,$session[0].id,$session[0].state)
                Assert-BetterGIRdpReadinessBudget $budget
                return $session[0]
            }
            $running = @(foreach ($clientId in $RdpClientProcessIds) { if (Get-Process -Id $clientId -ErrorAction SilentlyContinue) { [int]$clientId } })
            Assert-BetterGIRdpReadinessBudget $budget
            if ($RdpClientProcessIds.Count -gt 0 -and $running.Count -eq 0) { throw "Remote Desktop client exited before an active RDP session was created for user '$UserName'." }
            if (-not $securityWarningConfirmed -and $running.Count -gt 0 -and -not [string]::IsNullOrWhiteSpace($TargetAddress) -and (Get-BetterGIRdpMonotonicMilliseconds) -ge $nextWarning) {
                $securityWarningAttempted = $true
                foreach ($clientId in $running) {
                    if (Confirm-BetterGIRdpFileSecurityWarning -ProcessId $clientId -TargetAddress $TargetAddress -RdpUserName $RdpUserName -AllowTargetOmissionForTrackedClient:$AllowTargetOmissionForTrackedClient -TimeoutSeconds 1 -LogAction $LogAction -ParentBudget $budget) { $securityWarningConfirmed = $true; break }
                    Assert-BetterGIRdpReadinessBudget $budget
                }
                $nextWarning = (Get-BetterGIRdpMonotonicMilliseconds) + 2000
            }
            Assert-BetterGIRdpReadinessBudget $budget
            $snapshot = @($raw | Where-Object { $_ -match 'rdp-tcp#|rdp-tcp\s+65536|Listen|Active|Disc|Conn|运行中|已连接|侦听' }) -join ' | '
            if ($snapshot -and $snapshot -ne $lastObserved) { Write-BetterGIRdpLifecycleLog -LogAction $LogAction -Message ("Waiting for RDP session for {0}; current sessions: {1}" -f $UserName,$snapshot);$lastObserved=$snapshot }
            Wait-BetterGIRdpReadinessPoll -Budget $budget -Milliseconds 2000
        }
    }
    catch {
        # Aggregate cleanup uncertainty must not be collapsed into an ordinary timeout.
        if ($_.Exception -isnot [TimeoutException]) { throw }
    }
    if ($securityWarningAttempted -and -not $securityWarningConfirmed) { throw "Timed out waiting for RDP session for user '$UserName'; guarded warning confirmation did not establish readiness within the original budget." }
    if ($securityWarningConfirmed) { throw "Timed out waiting for RDP session for user '$UserName' after the trusted local RDP security warning was confirmed." }
    throw "Timed out waiting for RDP session for user '$UserName'."
}

function Resolve-BetterGIMuMuCliPath {
    param([string]$CliPath)

    $candidates = @()
    if (-not [string]::IsNullOrWhiteSpace($CliPath)) {
        $candidates += $CliPath
    }
    if (-not [string]::IsNullOrWhiteSpace($env:USERPROFILE)) {
        $candidates += (Join-Path $env:USERPROFILE 'Programs\MuMu Player 12\nx_main\mumu-cli.exe')
    }
    $candidates += @(
        'C:\Program Files\Netease\MuMuPlayer-12.0\nx_main\mumu-cli.exe',
        'C:\Program Files\MuMu Player 12\nx_main\mumu-cli.exe'
    )

    foreach ($candidate in $candidates) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    return $null
}

function Invoke-BetterGIMuMuCliInfo {
    param([string]$CliPath)

    if ([string]::IsNullOrWhiteSpace($CliPath) -or -not (Test-Path -LiteralPath $CliPath -PathType Leaf)) {
        return $null
    }

    $output = @(& $CliPath info --vmindex all 2>&1 | ForEach-Object { $_.ToString() })
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw ("mumu-cli info failed with exit code {0}: {1}" -f $exitCode, ($output -join ' '))
    }

    $text = ($output -join "`n")
    $jsonMatch = [regex]::Match($text, '(?s)\{.*\}')
    if (-not $jsonMatch.Success) {
        throw "mumu-cli info did not return JSON: $text"
    }

    $jsonMatch.Value | ConvertFrom-Json
}

function ConvertFrom-BetterGIMuMuInfo {
    param($Info)

    if (-not $Info) {
        return @()
    }

    @($Info.PSObject.Properties | ForEach-Object {
        $value = $_.Value
        [PSCustomObject]@{
            index = [string]$value.index
            name = [string]$value.name
            isProcessStarted = [bool]$value.is_process_started
            isAndroidStarted = [bool]$value.is_android_started
            errorCode = if ($null -ne $value.error_code) { [int]$value.error_code } else { $null }
            source = 'mumu-cli'
        }
    })
}

function Get-BetterGIMuMuRunningProcessIndexes {
    $records = @()
    foreach ($process in @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object { $_.Name -in @('MuMuNxDevice.exe', 'MuMuVMMHeadless.exe') })) {
        $commandLine = [string]$process.CommandLine
        $index = $null
        if ($process.Name -eq 'MuMuNxDevice.exe') {
            if ($commandLine -match '(?:^|\s)-v\s+(?<index>\d+)') {
                $index = $Matches.index
            }
            else {
                $index = '0'
            }
        }
        elseif ($commandLine -match 'MuMuPlayer-12\.0-(?<index>\d+)') {
            $index = $Matches.index
        }

        if ($null -eq $index) {
            continue
        }

        $records += [PSCustomObject]@{
            index = [string]$index
            processId = [int]$process.ProcessId
            name = [string]$process.Name
            sessionId = [int]$process.SessionId
            commandLine = $commandLine
            source = 'process'
        }
    }

    @($records)
}

function Get-BetterGIMuMuRunningInstanceSnapshot {
    param([string]$CliPath)

    $resolvedCliPath = Resolve-BetterGIMuMuCliPath -CliPath $CliPath
    $infoInstances = @()
    $infoError = $null
    if ($resolvedCliPath) {
        try {
            $infoInstances = @(ConvertFrom-BetterGIMuMuInfo -Info (Invoke-BetterGIMuMuCliInfo -CliPath $resolvedCliPath))
        }
        catch {
            $infoError = $_.Exception.Message
        }
    }

    $processRecords = @(Get-BetterGIMuMuRunningProcessIndexes)
    $runningByIndex = [ordered]@{}

    foreach ($instance in $infoInstances) {
        if ($instance.isProcessStarted -or $instance.isAndroidStarted) {
            $runningByIndex[$instance.index] = [PSCustomObject]@{
                index = [string]$instance.index
                name = [string]$instance.name
                isProcessStarted = [bool]$instance.isProcessStarted
                isAndroidStarted = [bool]$instance.isAndroidStarted
                processIds = @()
                source = 'mumu-cli'
            }
        }
    }

    foreach ($group in @($processRecords | Group-Object -Property index)) {
        $index = [string]$group.Name
        $processIds = @($group.Group | ForEach-Object { [int]$_.processId })
        if ($runningByIndex.Contains($index)) {
            $runningByIndex[$index].processIds = @($processIds)
            if ($runningByIndex[$index].source -notmatch 'process') {
                $runningByIndex[$index].source = $runningByIndex[$index].source + '+process'
            }
        }
        else {
            $info = @($infoInstances | Where-Object { $_.index -eq $index } | Select-Object -First 1)
            $runningByIndex[$index] = [PSCustomObject]@{
                index = $index
                name = if ($info.Count -gt 0) { [string]$info[0].name } else { "MuMuPlayer-12.0-$index" }
                isProcessStarted = $true
                isAndroidStarted = $false
                processIds = @($processIds)
                source = 'process'
            }
        }
    }

    [PSCustomObject]@{
        capturedAt = (Get-Date).ToString('o')
        cliPath = $resolvedCliPath
        cliInfoError = $infoError
        allInstances = @($infoInstances)
        processRecords = @($processRecords)
        runningInstances = @($runningByIndex.Values | Sort-Object { [int]$_.index })
    }
}

function Restore-BetterGIMuMuInstancesAfterRdp {
    param(
        $Snapshot,

        [string]$CliPath,

        [switch]$NoLaunch,

        [int]$DelaySeconds = 5,

        [scriptblock]$LogAction
    )

    $targetInstances = @()
    if ($Snapshot -and $Snapshot.runningInstances) {
        $targetInstances = @($Snapshot.runningInstances)
    }

    $resolvedCliPath = Resolve-BetterGIMuMuCliPath -CliPath $CliPath
    $result = [ordered]@{
        enabled = $true
        noLaunch = [bool]$NoLaunch
        cliPath = $resolvedCliPath
        delaySeconds = $DelaySeconds
        targetIndexes = @($targetInstances | ForEach-Object { [string]$_.index })
        launched = @()
        skipped = @()
        failed = @()
    }

    if ($targetInstances.Count -eq 0) {
        Write-BetterGIRdpLifecycleLog -LogAction $LogAction -Message 'No running MuMu emulator instances were recorded before RDP startup; nothing to restore.'
        return [PSCustomObject]$result
    }

    if (-not $resolvedCliPath) {
        $message = 'mumu-cli.exe was not found; cannot restore recorded MuMu emulator instances.'
        Write-BetterGIRdpLifecycleLog -LogAction $LogAction -Message $message
        $result.failed = @($targetInstances | ForEach-Object {
            [PSCustomObject]@{
                index = [string]$_.index
                name = [string]$_.name
                error = $message
            }
        })
        return [PSCustomObject]$result
    }

    if ($NoLaunch) {
        $result.wouldLaunch = @($targetInstances | ForEach-Object {
            [PSCustomObject]@{
                index = [string]$_.index
                name = [string]$_.name
            }
        })
        return [PSCustomObject]$result
    }

    if ($DelaySeconds -gt 0) {
        Write-BetterGIRdpLifecycleLog -LogAction $LogAction -Message ("Waiting {0}s before restoring recorded MuMu emulator instances." -f $DelaySeconds)
        Start-Sleep -Seconds $DelaySeconds
    }

    $currentSnapshot = Get-BetterGIMuMuRunningInstanceSnapshot -CliPath $resolvedCliPath
    $currentlyRunning = @{}
    foreach ($instance in @($currentSnapshot.runningInstances)) {
        $currentlyRunning[[string]$instance.index] = $true
    }

    foreach ($instance in $targetInstances) {
        $index = [string]$instance.index
        if ($currentlyRunning.ContainsKey($index)) {
            Write-BetterGIRdpLifecycleLog -LogAction $LogAction -Message ("MuMu emulator instance {0} ({1}) is already running after RDP startup; skipping restore launch." -f $index, $instance.name)
            $result.skipped += [PSCustomObject]@{
                index = $index
                name = [string]$instance.name
                reason = 'already-running'
            }
            continue
        }

        try {
            Write-BetterGIRdpLifecycleLog -LogAction $LogAction -Message ("Restoring MuMu emulator instance {0} ({1}) after RDP startup." -f $index, $instance.name)
            $output = @(& $resolvedCliPath control --vmindex $index launch 2>&1 | ForEach-Object { $_.ToString() })
            $exitCode = $LASTEXITCODE
            if ($exitCode -ne 0) {
                throw ("mumu-cli launch exited with code {0}: {1}" -f $exitCode, ($output -join ' '))
            }

            $result.launched += [PSCustomObject]@{
                index = $index
                name = [string]$instance.name
                output = @($output)
            }
        }
        catch {
            $message = $_.Exception.Message
            Write-BetterGIRdpLifecycleLog -LogAction $LogAction -Message ("Failed to restore MuMu emulator instance {0}: {1}" -f $index, $message)
            $result.failed += [PSCustomObject]@{
                index = $index
                name = [string]$instance.name
                error = $message
            }
        }
    }

    [PSCustomObject]$result
}

function Get-BetterGIMuMuRemoteProtectionState {
    param([string]$ServiceName = 'MuMuRemoteService')

    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if (-not $service) {
        return [PSCustomObject]@{
            serviceName = $ServiceName
            exists = $false
            status = $null
            startType = $null
            wasRunning = $false
        }
    }

    [PSCustomObject]@{
        serviceName = $ServiceName
        exists = $true
        status = [string]$service.Status
        startType = [string]$service.StartType
        wasRunning = ($service.Status -eq 'Running')
    }
}

function Stop-BetterGIMuMuRemoteHelperProcesses {
    param([scriptblock]$LogAction)

    $processNames = @('MuMuRemoteBackend.exe', 'MumuRemoteHealthd.exe')
    $stopped = @()
    foreach ($process in @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object { $_.Name -in $processNames })) {
        try {
            Write-BetterGIRdpLifecycleLog -LogAction $LogAction -Message ("Stopping MuMu remote helper process {0} ({1}) before RDP startup." -f $process.Name, $process.ProcessId)
            Stop-Process -Id $process.ProcessId -Force -ErrorAction Stop
            $stopped += [PSCustomObject]@{
                name = [string]$process.Name
                processId = [int]$process.ProcessId
                sessionId = [int]$process.SessionId
            }
        }
        catch {
            throw ("Failed to stop MuMu remote helper process {0} ({1}): {2}" -f $process.Name, $process.ProcessId, $_.Exception.Message)
        }
    }

    @($stopped)
}

function Suspend-BetterGIMuMuRemoteForRdp {
    param(
        [string]$ServiceName = 'MuMuRemoteService',

        [switch]$NoLaunch,

        [scriptblock]$LogAction
    )

    $state = Get-BetterGIMuMuRemoteProtectionState -ServiceName $ServiceName
    $result = [ordered]@{
        serviceName = $ServiceName
        exists = [bool]$state.exists
        initialStatus = $state.status
        initialStartType = $state.startType
        wasRunning = [bool]$state.wasRunning
        noLaunch = [bool]$NoLaunch
        stoppedService = $false
        stoppedProcesses = @()
        restored = $false
    }

    if (-not $state.exists) {
        Write-BetterGIRdpLifecycleLog -LogAction $LogAction -Message ("MuMu remote service '{0}' not found; RDP startup will continue without service protection." -f $ServiceName)
        return [PSCustomObject]$result
    }

    if ($NoLaunch) {
        $result.wouldStopService = [bool]$state.wasRunning
        $result.wouldStopProcesses = $true
        return [PSCustomObject]$result
    }

    try {
        if ($state.wasRunning) {
            Write-BetterGIRdpLifecycleLog -LogAction $LogAction -Message ("Stopping {0} before local RDP startup so MuMu/UU Remote cannot close console emulator instances." -f $ServiceName)
            Stop-Service -Name $ServiceName -ErrorAction Stop
            (Get-Service -Name $ServiceName -ErrorAction Stop).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(15))
            $result.stoppedService = $true
        }
        else {
            Write-BetterGIRdpLifecycleLog -LogAction $LogAction -Message ("{0} is already {1}; no service stop needed before RDP startup." -f $ServiceName, $state.status)
        }

        $result.stoppedProcesses = @(Stop-BetterGIMuMuRemoteHelperProcesses -LogAction $LogAction)
    }
    catch {
        if ($result.stoppedService -and $state.wasRunning) {
            try {
                Write-BetterGIRdpLifecycleLog -LogAction $LogAction -Message ("MuMu Remote protection failed; restoring {0} before aborting RDP startup." -f $ServiceName)
                Start-Service -Name $ServiceName -ErrorAction Stop
                (Get-Service -Name $ServiceName -ErrorAction Stop).WaitForStatus('Running', [TimeSpan]::FromSeconds(20))
            }
            catch {
                Write-BetterGIRdpLifecycleLog -LogAction $LogAction -Message ("Failed to restore {0} after protection error: {1}" -f $ServiceName, $_.Exception.Message)
            }
        }
        throw
    }

    [PSCustomObject]$result
}

function Restore-BetterGIMuMuRemoteAfterRdp {
    param(
        $ProtectionState,

        [scriptblock]$LogAction
    )

    if (-not $ProtectionState -or -not $ProtectionState.exists -or $ProtectionState.noLaunch) {
        return
    }

    if (-not $ProtectionState.wasRunning) {
        Write-BetterGIRdpLifecycleLog -LogAction $LogAction -Message ("Leaving {0} stopped because it was not running before RDP startup." -f $ProtectionState.serviceName)
        return
    }

    Write-BetterGIRdpLifecycleLog -LogAction $LogAction -Message ("Restarting {0} after local RDP cleanup." -f $ProtectionState.serviceName)
    Start-Service -Name $ProtectionState.serviceName -ErrorAction Stop
    (Get-Service -Name $ProtectionState.serviceName -ErrorAction Stop).WaitForStatus('Running', [TimeSpan]::FromSeconds(20))
    $ProtectionState.restored = $true
}

function Invoke-BetterGIRdpUserLogoff {
    param(
        [string]$UserName,

        [scriptblock]$LogAction
    )

    if ([string]::IsNullOrWhiteSpace($UserName)) {
        return
    }

    $currentSessionId = (Get-Process -Id $PID).SessionId
    $sessions = @(Get-BetterGIRdpUserSession -UserName $UserName)
    if ($sessions.Count -eq 0) {
        Write-BetterGIRdpLifecycleLog -LogAction $LogAction -Message ("No RDP session found for user {0}; nothing to log off." -f $UserName)
        return
    }

    foreach ($session in $sessions) {
        Write-BetterGIRdpLifecycleLog -LogAction $LogAction -Message ("Logging off RDP session {0} ({1}) for user {2}." -f $session.id, $session.sessionName, $UserName)
        if ($session.id -eq $currentSessionId) {
            $command = 'timeout /t 3 /nobreak >nul & logoff {0}' -f $session.id
            Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\cmd.exe') -ArgumentList @('/d', '/c', $command) -WindowStyle Hidden | Out-Null
        }
        else {
            $output = & (Join-Path $env:SystemRoot 'System32\logoff.exe') $session.id 2>&1
            if ($LASTEXITCODE -ne 0) {
                throw ("Failed to log off RDP session {0} for user {1}: {2}" -f $session.id, $UserName, (@($output) -join ' '))
            }
        }
    }
}

function Reset-BetterGIRdpUserState {
    param(
        [string]$UserName,
        [string]$RdpFilePath,
        [string]$TargetAddress = $script:BetterGIRdpDefaultTargetAddress,
        [int]$CloseTimeoutSeconds = 10,
        [scriptblock]$LogAction
    )

    $sessions = @(Get-BetterGIRdpUserSession -UserName $UserName)
    $clientRecords = @(Get-BetterGIRdpClientProcess `
        -RdpFilePath $RdpFilePath `
        -TargetAddress $TargetAddress)

    if ($sessions.Count -gt 0) {
        Write-BetterGIRdpLifecycleLog -LogAction $LogAction -Message (
            "Resetting {0} pre-existing RDP session(s) for {1} before a new scheduled run." -f
            $sessions.Count,
            $UserName)
        Invoke-BetterGIRdpUserLogoff -UserName $UserName -LogAction $LogAction
    }

    foreach ($record in $clientRecords) {
        Stop-BetterGIRdpClientProcessRecord `
            -Record $record `
            -CloseTimeoutSeconds $CloseTimeoutSeconds `
            -LogAction $LogAction
    }
}

function Start-BetterGIRdpClientProcess {
    param(
        [string]$MstscPath,
        [string]$RdpFilePath
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $MstscPath
    # The outer task runs PowerShell hidden. Launch mstsc through ShellExecute
    # with an explicit normal window so the RDP UI does not inherit SW_HIDE.
    $startInfo.UseShellExecute = $true
    $startInfo.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Normal
    # Windows PowerShell 5.1 runs the outer scheduled task on .NET Framework,
    # where ProcessStartInfo.ArgumentList does not exist.
    $startInfo.Arguments = '"{0}"' -f $RdpFilePath.Replace('"', '\"')
    [System.Diagnostics.Process]::Start($startInfo)
}

function Ensure-BetterGIRdpMinimizeSupport {
    param(
        [switch]$CheckOnly,

        [scriptblock]$ReadValue,

        [scriptblock]$WriteValue,

        [scriptblock]$LogAction
    )

    $registryPath = 'Registry::HKEY_CURRENT_USER\Software\Microsoft\Terminal Server Client'
    $valueName = 'RemoteDesktop_SuppressWhenMinimized'
    $requiredValue = 2

    if (-not $ReadValue) {
        $ReadValue = {
            $property = Get-ItemProperty `
                -LiteralPath $registryPath `
                -Name $valueName `
                -ErrorAction SilentlyContinue
            if ($property) { $property.$valueName } else { $null }
        }.GetNewClosure()
    }
    if (-not $WriteValue) {
        $WriteValue = {
            param([int]$Value)
            if (-not (Test-Path -LiteralPath $registryPath)) {
                New-Item -Path $registryPath -Force | Out-Null
            }
            New-ItemProperty `
                -LiteralPath $registryPath `
                -Name $valueName `
                -PropertyType DWord `
                -Value $Value `
                -Force | Out-Null
        }.GetNewClosure()
    }

    $currentValue = & $ReadValue
    if ($null -ne $currentValue -and [int]$currentValue -eq $requiredValue) {
        return [pscustomobject]@{
            status = 'configured'
            registryPath = $registryPath
            valueName = $valueName
            value = $requiredValue
            changed = $false
            wouldChange = $false
        }
    }

    if ($CheckOnly) {
        Write-BetterGIRdpLifecycleLog -LogAction $LogAction -Message (
            "NoLaunch validation: minimized RDP support would set {0}\\{1} to {2}." -f
            $registryPath, $valueName, $requiredValue)
        return [pscustomobject]@{
            status = 'would-configure'
            registryPath = $registryPath
            valueName = $valueName
            value = $requiredValue
            changed = $false
            wouldChange = $true
        }
    }

    & $WriteValue $requiredValue
    $verifiedValue = & $ReadValue
    if ($null -eq $verifiedValue -or [int]$verifiedValue -ne $requiredValue) {
        throw "Unable to verify $registryPath\\$valueName=$requiredValue."
    }

    Write-BetterGIRdpLifecycleLog -LogAction $LogAction -Message (
        "Configured minimized RDP support: {0}\\{1}={2}." -f
        $registryPath, $valueName, $requiredValue)
    [pscustomobject]@{
        status = 'configured'
        registryPath = $registryPath
        valueName = $valueName
        value = $requiredValue
        changed = $true
        wouldChange = $false
    }
}

function Set-BetterGIRdpClientWindowIsolated {
    param(
        [object[]]$Records,

        [ValidateSet('Offscreen', 'Minimize', 'None')]
        [string]$Mode = 'Offscreen',

        [scriptblock]$LogAction,

        [scriptblock]$ProcessRecordProvider,

        [scriptblock]$WindowHandleProvider,

        [scriptblock]$WindowIsMinimizedProvider,

        [scriptblock]$MinimizeAction,

        [scriptblock]$WindowIsOffscreenProvider,

        [scriptblock]$MoveOffscreenAction,

        [scriptblock]$SleepAction = { param([int]$Milliseconds) Start-Sleep -Milliseconds $Milliseconds }
    )

    if ($Mode -eq 'None') {
        Write-BetterGIRdpLifecycleLog -LogAction $LogAction -Message (
            '[DEBUG] RDP client window isolation is disabled for this launch.')
        return [PSCustomObject]@{
            mode = $Mode
            offscreenCount = 0
            minimizedCount = 0
            alreadyIsolatedCount = 0
            failedCount = 0
            skippedCount = @($Records).Count
        }
    }

    if (-not $ProcessRecordProvider) {
        $ProcessRecordProvider = {
            param([int]$ProcessId)
            Get-BetterGIRdpProcessRecordById -ProcessId $ProcessId
        }
    }
    if (-not $WindowHandleProvider) {
        $WindowHandleProvider = {
            param([int]$ProcessId)
            $process = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
            if ($process) { $process.MainWindowHandle } else { [IntPtr]::Zero }
        }
    }
    if (($Mode -eq 'Minimize' -and (-not $WindowIsMinimizedProvider -or -not $MinimizeAction)) -or
        ($Mode -eq 'Offscreen' -and (-not $WindowIsOffscreenProvider -or -not $MoveOffscreenAction))) {
        if (-not ('BetterGIRdpWindowIsolation' -as [type])) {
            Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class BetterGIRdpWindowIsolation
{
    [StructLayout(LayoutKind.Sequential)]
    public struct WindowRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out WindowRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint flags);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int index);

    public static WindowRect ReadWindowRect(IntPtr hWnd)
    {
        WindowRect rect;
        if (!GetWindowRect(hWnd, out rect))
        {
            throw new InvalidOperationException("Unable to read the RDP client window bounds.");
        }
        return rect;
    }

    public static bool IsOffscreen(IntPtr hWnd)
    {
        WindowRect rect = ReadWindowRect(hWnd);
        const int SM_XVIRTUALSCREEN = 76;
        const int SM_YVIRTUALSCREEN = 77;
        const int SM_CXVIRTUALSCREEN = 78;
        const int SM_CYVIRTUALSCREEN = 79;
        int left = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int top = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int right = left + GetSystemMetrics(SM_CXVIRTUALSCREEN);
        int bottom = top + GetSystemMetrics(SM_CYVIRTUALSCREEN);
        return rect.Left >= right - 1 || rect.Top >= bottom - 1 ||
            rect.Right <= left + 1 || rect.Bottom <= top + 1;
    }

    public static int MoveOffscreenWithoutActivation(IntPtr hWnd)
    {
        const int SW_SHOWNOACTIVATE = 4;
        const uint FLAGS = 0x0001u | 0x0004u | 0x0010u | 0x0200u | 0x0400u;
        const int SM_XVIRTUALSCREEN = 76;
        const int SM_YVIRTUALSCREEN = 77;
        const int SM_CXVIRTUALSCREEN = 78;
        int targetX = GetSystemMetrics(SM_XVIRTUALSCREEN) +
            GetSystemMetrics(SM_CXVIRTUALSCREEN) - 1;
        int targetY = GetSystemMetrics(SM_YVIRTUALSCREEN);
        ShowWindowAsync(hWnd, SW_SHOWNOACTIVATE);
        if (!SetWindowPos(hWnd, IntPtr.Zero, targetX, targetY, 0, 0, FLAGS))
        {
            int error = Marshal.GetLastWin32Error();
            return error == 0 ? -1 : error;
        }
        return 0;
    }
}
'@
        }
    }

    if (-not $WindowIsMinimizedProvider) {
        $WindowIsMinimizedProvider = {
            param([IntPtr]$WindowHandle)
            [BetterGIRdpWindowIsolation]::IsIconic($WindowHandle)
        }
    }
    if (-not $MinimizeAction) {
        $MinimizeAction = {
            param([IntPtr]$WindowHandle)
            $null = [BetterGIRdpWindowIsolation]::ShowWindowAsync($WindowHandle, 6)
            if (-not [BetterGIRdpWindowIsolation]::IsIconic($WindowHandle)) {
                $null = [BetterGIRdpWindowIsolation]::ShowWindow($WindowHandle, 6)
            }
        }
    }

    if (-not $WindowIsOffscreenProvider) {
        $WindowIsOffscreenProvider = {
            param([IntPtr]$WindowHandle)
            [BetterGIRdpWindowIsolation]::IsOffscreen($WindowHandle)
        }
    }
    if (-not $MoveOffscreenAction) {
        $MoveOffscreenAction = {
            param([IntPtr]$WindowHandle)
            $errorCode = [BetterGIRdpWindowIsolation]::MoveOffscreenWithoutActivation($WindowHandle)
            if ($errorCode -ne 0) {
                throw "Unable to move the RDP client window offscreen; Win32 error $errorCode."
            }
        }
    }

    $offscreenCount = 0
    $minimizedCount = 0
    $alreadyIsolatedCount = 0
    $failedCount = 0
    $skippedCount = 0
    foreach ($record in @($Records)) {
        if (-not $record -or -not $record.processId) {
            $skippedCount++
            continue
        }

        $currentRecord = & $ProcessRecordProvider ([int]$record.processId)
        if (-not $currentRecord -or $currentRecord.name -ne 'mstsc.exe') {
            $skippedCount++
            continue
        }
        if ($record.creationDate -and $currentRecord.creationDate -ne $record.creationDate) {
            Write-BetterGIRdpLifecycleLog -LogAction $LogAction -Message (
                "Skipping RDP window isolation for process {0}; creation time changed." -f $record.processId)
            $skippedCount++
            continue
        }
        if ($record.commandLine -and $currentRecord.commandLine -and $currentRecord.commandLine -ne $record.commandLine) {
            Write-BetterGIRdpLifecycleLog -LogAction $LogAction -Message (
                "Skipping RDP window isolation for process {0}; command line changed." -f $record.processId)
            $skippedCount++
            continue
        }

        $windowHandle = & $WindowHandleProvider ([int]$record.processId)
        if (-not $windowHandle -or $windowHandle -eq [IntPtr]::Zero) {
            $skippedCount++
            continue
        }

        if ($Mode -eq 'Minimize') {
            if (& $WindowIsMinimizedProvider ([IntPtr]$windowHandle)) {
                $alreadyIsolatedCount++
                continue
            }

            $null = & $MinimizeAction ([IntPtr]$windowHandle)
            $isMinimized = $false
            foreach ($attempt in 1..5) {
                if (& $WindowIsMinimizedProvider ([IntPtr]$windowHandle)) {
                    $isMinimized = $true
                    break
                }
                & $SleepAction 100
            }

            if ($isMinimized) {
                $minimizedCount++
            }
            else {
                $failedCount++
                Write-BetterGIRdpLifecycleLog -LogAction $LogAction -Message (
                    "Failed to verify minimized state for tracked RDP client process {0}." -f $record.processId)
            }
            continue
        }

        if (& $WindowIsOffscreenProvider ([IntPtr]$windowHandle)) {
            $alreadyIsolatedCount++
            continue
        }

        $null = & $MoveOffscreenAction ([IntPtr]$windowHandle)
        $isOffscreen = $false
        foreach ($attempt in 1..5) {
            if (& $WindowIsOffscreenProvider ([IntPtr]$windowHandle)) {
                $isOffscreen = $true
                break
            }
            & $SleepAction 100
        }

        if ($isOffscreen) {
            $offscreenCount++
        }
        else {
            $failedCount++
            Write-BetterGIRdpLifecycleLog -LogAction $LogAction -Message (
                "Failed to verify offscreen state for tracked RDP client process {0}." -f $record.processId)
        }
    }

    if ($minimizedCount -gt 0) {
        Write-BetterGIRdpLifecycleLog -LogAction $LogAction -Message (
            "Kept {0} tracked RDP client window(s) minimized to isolate Cyan pointer activity from the hcy desktop." -f
            $minimizedCount)
    }
    if ($offscreenCount -gt 0) {
        Write-BetterGIRdpLifecycleLog -LogAction $LogAction -Message (
            "Kept {0} tracked RDP client window(s) offscreen without minimizing so the Cyan remote input desktop stays active." -f
            $offscreenCount)
    }

    [PSCustomObject]@{
        mode = $Mode
        offscreenCount = $offscreenCount
        minimizedCount = $minimizedCount
        alreadyIsolatedCount = $alreadyIsolatedCount
        failedCount = $failedCount
        skippedCount = $skippedCount
    }
}

function Stop-BetterGIRdpClientProcessRecord {
    param(
        $Record,

        [int]$CloseTimeoutSeconds = 10,

        [scriptblock]$LogAction
    )

    if (-not $Record -or -not $Record.processId) {
        return
    }

    $currentRecord = Get-BetterGIRdpProcessRecordById -ProcessId $Record.processId
    if (-not $currentRecord) {
        return
    }

    if ($currentRecord.name -ne 'mstsc.exe') {
        Write-BetterGIRdpLifecycleLog -LogAction $LogAction -Message ("Skipping process {0}; expected mstsc.exe but found {1}." -f $Record.processId, $currentRecord.name)
        return
    }

    if ($Record.creationDate -and $currentRecord.creationDate -ne $Record.creationDate) {
        Write-BetterGIRdpLifecycleLog -LogAction $LogAction -Message ("Skipping process {0}; creation time changed, so the PID is no longer owned by this run." -f $Record.processId)
        return
    }

    if ($Record.commandLine -and $currentRecord.commandLine -and $currentRecord.commandLine -ne $Record.commandLine) {
        Write-BetterGIRdpLifecycleLog -LogAction $LogAction -Message ("Skipping process {0}; command line changed, so the PID is no longer owned by this run." -f $Record.processId)
        return
    }

    $process = Get-Process -Id $Record.processId -ErrorAction SilentlyContinue
    if (-not $process) {
        return
    }

    Write-BetterGIRdpLifecycleLog -LogAction $LogAction -Message ("Closing RDP client process {0}." -f $Record.processId)
    $closedGracefully = $false
    if ($process.MainWindowHandle -ne 0) {
        $closedGracefully = $process.CloseMainWindow()
    }

    if ($closedGracefully) {
        if ($process.WaitForExit($CloseTimeoutSeconds * 1000)) {
            return
        }
    }

    $stillRunning = Get-Process -Id $Record.processId -ErrorAction SilentlyContinue
    if ($stillRunning) {
        Write-BetterGIRdpLifecycleLog -LogAction $LogAction -Message ("Force-closing tracked RDP client process {0}." -f $Record.processId)
        Stop-Process -Id $Record.processId -Force
    }
}

function Start-BetterGIRdpSession {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RdpFilePath,

        [string]$TargetAddress = $script:BetterGIRdpDefaultTargetAddress,

        [string]$RdpUserName = 'Cyan',

        [bool]$OpenRdpClient = $true,

        [bool]$RequireCurrentRdpSession = $false,

        [bool]$EnableRdpLifecycle = $true,

        [switch]$NoLaunch,

        [int]$WarmupSeconds = 20,

        [string]$GHubInstallPath = (Join-Path $env:ProgramFiles 'LGHUB'),

        [scriptblock]$LogAction
    )

    if (-not $EnableRdpLifecycle) {
        Write-BetterGIRdpLifecycleLog -LogAction $LogAction -Message 'RDP lifecycle is disabled for this scheduled run.'
        return [PSCustomObject]@{
            enabled = $false
            noLaunch = [bool]$NoLaunch
            rdpFilePath = $RdpFilePath
            targetAddress = $TargetAddress
            userName = $RdpUserName
            openRdpClient = $OpenRdpClient
            requireCurrentRdpSession = $RequireCurrentRdpSession
            wouldOpenRdp = $false
            wouldLogoffRdpUser = $false
            startedProcessId = $null
            trackedProcessIds = @()
            trackedProcesses = @()
            currentSession = $null
            preExistingProcessIds = @()
            startedAt = $null
            ghubIsolation = $null
        }
    }

    if (-not (Test-Path -LiteralPath $RdpFilePath -PathType Leaf)) {
        throw "RDP file not found: $RdpFilePath"
    }

    $resolvedRdpFilePath = (Resolve-Path -LiteralPath $RdpFilePath).Path
    $rdpFileContent = Get-Content -LiteralPath $resolvedRdpFilePath -Raw -Encoding UTF8
    if ($rdpFileContent -notmatch ('(?m)^full address:s:{0}$' -f [regex]::Escape($TargetAddress))) {
        throw "RDP file '$resolvedRdpFilePath' does not target $TargetAddress."
    }

    $ghubIsolation = $null
    if ($OpenRdpClient) {
        $ghubIsolation = Ensure-BetterGIGHubRdpIsolation `
            -RdpUserName $RdpUserName `
            -GHubInstallPath $GHubInstallPath `
            -NoLaunch:$NoLaunch `
            -LogAction $LogAction
    }

    $mstscPath = Join-Path $env:SystemRoot 'System32\mstsc.exe'
    if (-not (Test-Path -LiteralPath $mstscPath -PathType Leaf)) {
        $mstscPath = 'mstsc.exe'
    }

    if ($NoLaunch) {
        Write-BetterGIRdpLifecycleLog -LogAction $LogAction -Message ("NoLaunch validation: would start mstsc.exe with '{0}'." -f $resolvedRdpFilePath)
        return [PSCustomObject]@{
            enabled = $true
            noLaunch = $true
            rdpFilePath = $resolvedRdpFilePath
            targetAddress = $TargetAddress
            userName = $RdpUserName
            openRdpClient = $OpenRdpClient
            requireCurrentRdpSession = $RequireCurrentRdpSession
            wouldOpenRdp = [bool]$OpenRdpClient
            wouldLogoffRdpUser = $true
            startedProcessId = $null
            trackedProcessIds = @()
            trackedProcesses = @()
            currentSession = $null
            preExistingProcessIds = @()
            startedAt = $null
            ghubIsolation = $ghubIsolation
        }
    }

    if (-not $OpenRdpClient) {
        $currentSessionId = (Get-Process -Id $PID).SessionId
        $currentSession = Get-BetterGIRdpSessionById -SessionId $currentSessionId
        if ($RequireCurrentRdpSession) {
            if (-not $currentSession) {
                throw "Current process is not running inside a queryable RDP session."
            }
            if ($currentSession.sessionName -notlike 'rdp-tcp#*') {
                throw "Current process is running in session '$($currentSession.sessionName)', not an RDP session."
            }
            if (-not $currentSession.userName.Equals($RdpUserName, [StringComparison]::OrdinalIgnoreCase)) {
                throw "Current RDP session belongs to '$($currentSession.userName)', expected '$RdpUserName'."
            }
        }

        Write-BetterGIRdpLifecycleLog -LogAction $LogAction -Message ("Skipping RDP client startup; current session cleanup will target user {0}." -f $RdpUserName)
        return [PSCustomObject]@{
            enabled = $true
            noLaunch = $false
            rdpFilePath = $resolvedRdpFilePath
            targetAddress = $TargetAddress
            userName = $RdpUserName
            openRdpClient = $false
            requireCurrentRdpSession = $RequireCurrentRdpSession
            wouldOpenRdp = $false
            wouldLogoffRdpUser = $true
            startedProcessId = $null
            trackedProcessIds = @()
            trackedProcesses = @()
            currentSession = $currentSession
            preExistingProcessIds = @()
            startedAt = Get-Date
            ghubIsolation = $null
        }
    }

    $preExistingProcessIds = @(Get-Process -Name 'mstsc' -ErrorAction SilentlyContinue | ForEach-Object { [int]$_.Id })
    $startedAt = Get-Date
    $process = $null
    $startedProcessRecord = $null
    $securityWarningConfirmed = $false

    try {
        Write-BetterGIRdpLifecycleLog -LogAction $LogAction -Message ("Starting Remote Desktop client for {0} using {1}." -f $TargetAddress, $resolvedRdpFilePath)
        $process = Start-BetterGIRdpClientProcess -MstscPath $mstscPath -RdpFilePath $resolvedRdpFilePath
        if ($process) {
            Start-Sleep -Milliseconds 250
            $startedProcessRecord = Get-BetterGIRdpProcessRecordById -ProcessId $process.Id
            $securityWarningConfirmed = Confirm-BetterGIRdpFileSecurityWarning `
                -ProcessId $process.Id `
                -TargetAddress $TargetAddress `
                -RdpUserName $RdpUserName `
                -AllowTargetOmissionForTrackedClient `
                -LogAction $LogAction
            if (-not $securityWarningConfirmed) {
                Write-BetterGIRdpLifecycleLog -LogAction $LogAction -Message 'RDP security warning was not observed during startup; session readiness will continue guarded confirmation attempts.'
            }
        }

        if ($WarmupSeconds -gt 0) {
            Start-Sleep -Seconds $WarmupSeconds
        }

        $matchedProcesses = Get-BetterGIRdpClientProcess `
            -StartedAfter $startedAt `
            -ExcludeProcessId $preExistingProcessIds `
            -RdpFilePath $resolvedRdpFilePath `
            -TargetAddress $TargetAddress
        $trackedProcesses = @($startedProcessRecord) + @($matchedProcesses) |
            Where-Object { $_ -and $_.processId } |
            Sort-Object -Property processId -Unique
        $trackedProcessIds = @($trackedProcesses | ForEach-Object { [int]$_.processId })

        if ($trackedProcessIds.Count -eq 0) {
            throw "Remote Desktop client for $TargetAddress did not remain running after startup."
        }

        Write-BetterGIRdpLifecycleLog -LogAction $LogAction -Message ("Tracking RDP client process id(s): {0}" -f ($trackedProcessIds -join ', '))
        [PSCustomObject]@{
            enabled = $true
            noLaunch = $false
            rdpFilePath = $resolvedRdpFilePath
            targetAddress = $TargetAddress
            userName = $RdpUserName
            openRdpClient = $OpenRdpClient
            requireCurrentRdpSession = $RequireCurrentRdpSession
            wouldOpenRdp = $true
            wouldLogoffRdpUser = $true
            startedProcessId = if ($process) { [int]$process.Id } else { $null }
            trackedProcessIds = @($trackedProcessIds)
            trackedProcesses = @($trackedProcesses)
            securityWarningConfirmed = $securityWarningConfirmed
            currentSession = $null
            preExistingProcessIds = @($preExistingProcessIds)
            startedAt = $startedAt
            ghubIsolation = $ghubIsolation
        }
    }
    catch {
        $recordToClose = if ($startedProcessRecord) { $startedProcessRecord } elseif ($process) { Get-BetterGIRdpProcessRecordById -ProcessId $process.Id } else { $null }
        if ($recordToClose) {
            Stop-BetterGIRdpClientProcessRecord -Record $recordToClose -CloseTimeoutSeconds 5 -LogAction $LogAction
        }
        throw
    }
}

function Stop-BetterGIRdpSession {
    param(
        $Session,

        [int]$CloseTimeoutSeconds = 10,

        [bool]$LogoffRdpUser = $true,

        [scriptblock]$LogAction
    )

    if (-not $Session -or -not $Session.enabled -or $Session.noLaunch) {
        return
    }

    $logoffError = $null
    if ($LogoffRdpUser) {
        try {
            Invoke-BetterGIRdpUserLogoff -UserName $Session.userName -LogAction $LogAction
        }
        catch {
            $logoffError = $_
            Write-BetterGIRdpLifecycleLog -LogAction $LogAction -Message $_.Exception.Message
        }
    }

    foreach ($record in @($Session.trackedProcesses)) {
        Stop-BetterGIRdpClientProcessRecord -Record $record -CloseTimeoutSeconds $CloseTimeoutSeconds -LogAction $LogAction
    }

    if ($logoffError) {
        throw $logoffError
    }
}
