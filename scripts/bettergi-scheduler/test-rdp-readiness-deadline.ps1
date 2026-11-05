# Offline only: no task, registry, App, RDP, UI or real native input.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Use-BetterGI-RdpSession.ps1')
$script:RealSessionParser=(Get-Command Get-BetterGIRdpUserSession).ScriptBlock
$script:RealProbeFactory=(Get-Command New-BetterGIRdpSessionProbeProcess).ScriptBlock
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
# The legacy code's native fallback is fake too: no user32 import.
Add-Type 'public static class BetterGIRdpNativeClick { public static int Calls; public static uint GetOEMCP() { return 936; } public static System.IntPtr SendMessage(System.IntPtr h,int m,System.IntPtr w,System.IntPtr l) { Calls++; return System.IntPtr.Zero; } }'
function Get-Date { $script:Origin.AddMilliseconds($script:Elapsed) }
function Get-BetterGIRdpUtcNow { $script:Origin.AddMilliseconds($script:Elapsed + $script:UtcSkew) }
function Get-BetterGIRdpMonotonicMilliseconds { $script:Elapsed }
function Start-Sleep { param([int]$Milliseconds,[int]$Seconds) $script:Elapsed += $Milliseconds + 1000 * $Seconds }
function Get-Process { param($Id,$ErrorAction) [pscustomobject]@{ Id=$Id; ProcessName='mstsc' } }
function Get-CimInstance { throw 'Forbidden live CIM' }
function Start-Process { throw 'Forbidden live process' }
function Start-ScheduledTask { throw 'Forbidden task operation' }
function qwinsta.exe { @() }
function Get-BetterGIRdpUserSession {
    param([string]$UserName,$Budget,[ref]$RawSessionLines)
    if ($script:SessionMode -eq 'probe') { &$script:RealSessionParser -UserName $UserName -Budget $Budget -RawSessionLines $RawSessionLines; return }
    if ($script:SessionMode -eq 'late') { $script:Elapsed += 3000 }
    if ($script:SessionMode -ne 'none') { [pscustomobject]@{ userName=$UserName;sessionName='rdp-tcp#44';id=44;state='Active' } }
}
function Get-BetterGIRdpVisibleTopLevelWindowElements {
    param([int]$ProcessId,$Budget)
    $script:Elapsed += $script:ReadDelay
    @($script:Window)
}
function Get-BetterGIRdpAutomationNames { param($Element) @($script:Window.Text) }
function Send-BetterGIRdpWarningClick {
    param($Window,$Button,[int]$ProcessId,$Budget)
    $script:Clicks++
    $script:Elapsed += $script:SendDelay
    [pscustomobject]@{ Attempted=$true; Acknowledged=(-not $script:UnknownSend); Uncertain=$script:UnknownSend }
}
function Reset-Fixture {
    $script:Elapsed=0L; $script:UtcSkew=0L; $script:SendDelay=0; $script:Origin=[datetime]'2026-01-01T00:00:00Z'
    $script:SessionMode='none'; $script:ReadDelay=0; $script:Clicks=0; $script:UnknownSend=$false
    $script:BetterGIRdpWarningAttempts=@{}
    [BetterGIRdpNativeClick]::Calls=0
    $script:Button=[pscustomobject]@{ Handle=1002L; ProcessId=44;ClassName='Button';Id=1;Text='Connect';Visible=$true;Enabled=$true;Current=[pscustomobject]@{NativeWindowHandle=1002;AutomationId='1';Name='Connect'} }
    $script:Button | Add-Member ScriptMethod TryGetCurrentPattern { param($kind,$value) $false }
    $script:Window=[pscustomobject]@{ Handle=1001L;ProcessId=44;ClassName='#32770';Title='Security Warning';Text='The publisher of this remote connection cannot be identified. 127.0.0.2 FixtureUser';Complete=$true;Children=@($script:Button);Current=[pscustomobject]@{Name='Security Warning'} }
    $script:Window | Add-Member ScriptMethod FindFirst { param($scope,$condition) $script:Button }
    $script:Window | Add-Member ScriptMethod FindAll { param($scope,$condition) @($script:Button) }
}
function Assert-True { param([bool]$Value,[string]$Message) if(-not $Value){throw $Message} }
function Expect-Timeout { param([scriptblock]$Action) $caught=$false;try{&$Action}catch{if($_.Exception.Message -notmatch 'Timed out|deadline|budget'){throw};$caught=$true};Assert-True $caught 'late Active must not be returned as Ready after the original deadline' }
$cases=[ordered]@{
    'late Active is not Ready'={ $script:SessionMode='late';Expect-Timeout { $null=Wait-BetterGIRdpSessionReadyForUser -UserName 'FixtureUser' -TimeoutSeconds 2 } }
    'late UI read never clicks'={ $script:ReadDelay=2000;$null=Confirm-BetterGIRdpFileSecurityWarning -ProcessId 44 -TargetAddress '127.0.0.2' -RdpUserName 'FixtureUser' -TimeoutSeconds 1;Assert-True (([BetterGIRdpNativeClick]::Calls+$script:Clicks)-eq 0) 'UI read consumed budget but a late click was sent' }
    'normal Ready returns original session'={ $script:SessionMode='active';$s=Wait-BetterGIRdpSessionReadyForUser -UserName 'FixtureUser' -TimeoutSeconds 2;Assert-True ($s.id-eq 44) 'Normal Active session was lost' }
    'normal trusted local warning'={ $ok=Confirm-BetterGIRdpFileSecurityWarning -ProcessId 44 -TargetAddress '127.0.0.2' -RdpUserName 'FixtureUser' -TimeoutSeconds 1;Assert-True $ok 'Trusted warning was lost';Assert-True (([BetterGIRdpNativeClick]::Calls+$script:Clicks)-eq 1) 'Trusted warning must send once' }
}
# Compile the actual embedded native source under a distinct type name; do not invoke it.
$cases.Add('native source compiles without invocation', {
    $tokens=$null;$parseErrors=$null
    $ast=[System.Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Use-BetterGI-RdpSession.ps1'),[ref]$tokens,[ref]$parseErrors)
    Assert-True ($parseErrors.Count-eq 0) 'Helper syntax errors'
    $nativeText=@($ast.FindAll({param($n) $n -is [System.Management.Automation.Language.StringConstantExpressionAst] -and $n.Value.Contains('public static class BetterGIRdpNativeClick')},$true))
    Assert-True ($nativeText.Count-eq 1) 'Native source must be uniquely identified'
    Add-Type ($nativeText[0].Value.Replace('BetterGIRdpNativeClick','BetterGIRdpNativeCompileOnly'))
})
$cases.Add('wrong process ownership cannot click', { $script:Window.ProcessId=45;$null=Confirm-BetterGIRdpFileSecurityWarning -ProcessId 44 -TargetAddress '127.0.0.2' -RdpUserName 'FixtureUser' -TimeoutSeconds 1;Assert-True ($script:Clicks-eq 0) 'Wrong PID clicked' })
$cases.Add('zero native window cannot click', { $script:Window.Handle=0L;$null=Confirm-BetterGIRdpFileSecurityWarning -ProcessId 44 -TargetAddress '127.0.0.2' -RdpUserName 'FixtureUser' -TimeoutSeconds 1;Assert-True ($script:Clicks-eq 0) 'Zero HWND clicked' })
$cases.Add('wrong target cannot click', { $script:Window.Text='publisher 203.0.113.1 FixtureUser';$null=Confirm-BetterGIRdpFileSecurityWarning -ProcessId 44 -TargetAddress '127.0.0.2' -RdpUserName 'FixtureUser' -TimeoutSeconds 1;Assert-True ($script:Clicks-eq 0) 'Wrong target clicked' })
$cases.Add('wrong user cannot click', { $script:Window.Text='publisher 127.0.0.2 SomeoneElse';$null=Confirm-BetterGIRdpFileSecurityWarning -ProcessId 44 -TargetAddress '127.0.0.2' -RdpUserName 'FixtureUser' -TimeoutSeconds 1;Assert-True ($script:Clicks-eq 0) 'Wrong user clicked' })
$cases.Add('non-loopback target cannot click', { $script:Window.Text='publisher 203.0.113.1 FixtureUser';$null=Confirm-BetterGIRdpFileSecurityWarning -ProcessId 44 -TargetAddress '203.0.113.1' -RdpUserName 'FixtureUser' -TimeoutSeconds 1;Assert-True ($script:Clicks-eq 0) 'Remote target clicked' })
$cases.Add('remote content is not local file warning', { $script:Window.ClassName='TscShellContainerClass';$null=Confirm-BetterGIRdpFileSecurityWarning -ProcessId 44 -TargetAddress '127.0.0.2' -RdpUserName 'FixtureUser' -TimeoutSeconds 1;Assert-True ($script:Clicks-eq 0) 'Remote desktop content clicked' })
$cases.Add('certificate dialog is not publisher warning', { $script:Window.Text='The identity of remote computer 127.0.0.2 cannot be verified. FixtureUser';$null=Confirm-BetterGIRdpFileSecurityWarning -ProcessId 44 -TargetAddress '127.0.0.2' -RdpUserName 'FixtureUser' -TimeoutSeconds 1;Assert-True ($script:Clicks-eq 0) 'Certificate dialog clicked' })
$cases.Add('unknown send is never repeated', {
    $script:UnknownSend=$true;$caught=0
    foreach($iteration in 1..2){try{$null=Confirm-BetterGIRdpFileSecurityWarning -ProcessId 44 -TargetAddress '127.0.0.2' -RdpUserName 'FixtureUser' -TimeoutSeconds 1}catch{if($_.Exception.Message -notmatch 'unknown'){throw};$caught++}}
    Assert-True ($caught-eq 2 -and $script:Clicks-eq 1) 'Unknown native send was repeated or hidden'
})
$cases.Add('acknowledged send is not repeated', {foreach($iteration in 1..2){$null=Confirm-BetterGIRdpFileSecurityWarning -ProcessId 44 -TargetAddress '127.0.0.2' -RdpUserName 'FixtureUser' -TimeoutSeconds 1};Assert-True ($script:Clicks-eq 1) 'Acknowledged native send repeated'})
$cases.Add('log callback cannot dispatch after expiry', { $null=Confirm-BetterGIRdpFileSecurityWarning -ProcessId 44 -TargetAddress '127.0.0.2' -RdpUserName 'FixtureUser' -TimeoutSeconds 1 -LogAction {$script:Elapsed+=2000};Assert-True ($script:Clicks-eq 0) 'Late log callback caused click' })
$cases.Add('parent deadline clips confirmation window', { $parent=New-BetterGIRdpReadinessBudget -Milliseconds 100;$script:ReadDelay=200;$null=Confirm-BetterGIRdpFileSecurityWarning -ProcessId 44 -TargetAddress '127.0.0.2' -RdpUserName 'FixtureUser' -TimeoutSeconds 10 -ParentBudget $parent;Assert-True ($script:Clicks-eq 0) 'Child confirmation reset parent deadline' })
$cases.Add('tracked target omission keeps original exception', { $script:Window.Text='The publisher of this remote connection cannot be identified.';$ok=Confirm-BetterGIRdpFileSecurityWarning -ProcessId 44 -TargetAddress '127.0.0.2' -RdpUserName 'FixtureUser' -AllowTargetOmissionForTrackedClient -TimeoutSeconds 1;Assert-True ($ok -and $script:Clicks-eq 1) 'Tracked omission contract was lost' })
$cases.Add('untracked target omission stays refused', { $script:Window.Text='The publisher of this remote connection cannot be identified.';$null=Confirm-BetterGIRdpFileSecurityWarning -ProcessId 44 -TargetAddress '127.0.0.2' -RdpUserName 'FixtureUser' -TimeoutSeconds 1;Assert-True ($script:Clicks-eq 0) 'Untracked omission clicked' })
function New-FakeProbe {
    param([string]$Mode='normal',[string]$Output='rdp-tcp#44 FixtureUser 44 Active',[bool]$KillThrows=$false,[bool]$KillLeavesRunning=$false,[bool]$ConfirmExit=$false)
    $read=[pscustomobject]@{Result=$Output;Mode=$Mode;Waits=@()}
    $read|Add-Member ScriptMethod Wait {param($Milliseconds) $this.Waits+= $Milliseconds;if($this.Mode-eq 'pipe-hang'){$script:Elapsed+=$Milliseconds;return $false};$true}
    $reader=[pscustomobject]@{Read=$read;Calls=0}
    $reader|Add-Member ScriptMethod ReadToEndAsync {$this.Calls++;$this.Read}
    $process=[pscustomobject]@{Id=4242;Mode=$Mode;KillThrows=$KillThrows;KillLeavesRunning=$KillLeavesRunning;ConfirmExit=$ConfirmExit;HasExited=$false;ExitCode=0;Started=0;Killed=0;Disposed=$false;Waits=@();StandardOutput=$reader;StandardError=$reader}
    $process|Add-Member ScriptMethod Start {$this.Started++;$true}
    $process|Add-Member ScriptMethod WaitForExit {param($Milliseconds) $this.Waits+=$Milliseconds;if($this.Mode-eq 'early-failure' -and $this.Killed-eq 0){throw 'original fake probe failure'};if($this.Killed-gt 0 -and $this.KillLeavesRunning){if($this.ConfirmExit){$script:Elapsed+=[Math]::Min(10,$Milliseconds);$this.HasExited=$true;return $true};$script:Elapsed+=$Milliseconds;return $false};if($this.Mode-eq 'hang'){$script:Elapsed+=$Milliseconds;return $false};$this.HasExited=$true;if($this.Mode-eq 'late'){$script:Elapsed+=$Milliseconds+1};$true}
    $process|Add-Member ScriptMethod Kill {$this.Killed++;if($this.KillThrows){throw 'fake probe termination failed'};if(-not $this.KillLeavesRunning){$this.HasExited=$true}}
    $process|Add-Member ScriptMethod Dispose {$this.Disposed=$true}
    $process
}
function New-BetterGIRdpSessionProbeProcess { if(-not $script:Probe){throw 'Forbidden live probe factory'};$script:Probe }
$cases.Add('read-only probe gets remaining and kills only owned child', {
    $script:Probe=New-FakeProbe -Mode hang;$budget=New-BetterGIRdpReadinessBudget -Milliseconds 100;$script:Elapsed=70
    Expect-Timeout {$null=Invoke-BetterGIRdpSessionProbe -Budget $budget}
    Assert-True ($script:Probe.Waits[0]-eq 30 -and $script:Probe.Killed-eq 1 -and $script:Probe.Disposed) 'Probe was not bounded and retired'
    Assert-True ($script:Probe.StandardOutput.Calls-eq 2) 'Both pipes must start asynchronous drains'
})
$cases.Add('late completed probe cannot return Active data', {
    $script:Probe=New-FakeProbe -Mode late;$budget=New-BetterGIRdpReadinessBudget -Milliseconds 100
    Expect-Timeout {$null=Invoke-BetterGIRdpSessionProbe -Budget $budget}
    Assert-True ($script:Probe.Killed-eq 0 -and $script:Probe.Disposed) 'Completed probe must not kill unrelated processes'
})
$cases.Add('pipe drain is bounded after process exit', {
    $script:Probe=New-FakeProbe -Mode pipe-hang;$budget=New-BetterGIRdpReadinessBudget -Milliseconds 100
    Expect-Timeout {$null=Invoke-BetterGIRdpSessionProbe -Budget $budget}
    Assert-True $script:Probe.Disposed 'Output timeout leaked process handle'
})
$cases.Add('OEM Chinese session status keeps original parser', {
    $script:Probe=New-FakeProbe -Output 'rdp-tcp#44 FixtureUser 44 运行中';$budget=New-BetterGIRdpReadinessBudget -Milliseconds 100;$raw=@()
    $sessions=@(&$script:RealSessionParser -UserName 'FixtureUser' -Budget $budget -RawSessionLines ([ref]$raw))
    Assert-True ($sessions.Count-eq 1 -and $sessions[0].state-eq '运行中' -and $raw[0]-match '运行中') 'Chinese Active output was lost'
})
$cases.Add('probe factory configures OEM async pipes without starting', {
    $process=&$script:RealProbeFactory
    try {Assert-True ($process.StartInfo.StandardOutputEncoding.CodePage-eq 936 -and $process.StartInfo.StandardErrorEncoding.CodePage-eq 936 -and $process.StartInfo.RedirectStandardOutput -and $process.StartInfo.RedirectStandardError -and -not $process.StartInfo.UseShellExecute) 'Probe encoding or redirection changed'}finally{$process.Dispose()}
})
$cases.Add('failed probe termination is not hidden', {
    $script:Probe=New-FakeProbe -Mode hang -KillThrows $true;$budget=New-BetterGIRdpReadinessBudget -Milliseconds 100;$failure=$null
    try{$null=Invoke-BetterGIRdpSessionProbe -Budget $budget}catch{$failure=$_.Exception}
    Assert-True ($failure -is [AggregateException] -and $failure.Message-match 'termination') 'Probe termination uncertainty was hidden'
    Assert-True $script:Probe.Disposed 'Failed cleanup leaked process object'
})
$cases.Add('incomplete native snapshot never clicks', { $script:Window.Complete=$false;$null=Confirm-BetterGIRdpFileSecurityWarning -ProcessId 44 -TargetAddress '127.0.0.2' -RdpUserName 'FixtureUser' -TimeoutSeconds 1;Assert-True ($script:Clicks-eq 0) 'Incomplete snapshot clicked' })
$cases.Add('expired budget never starts a probe', { $script:Probe=New-FakeProbe;$budget=New-BetterGIRdpReadinessBudget -Milliseconds 100;$script:Elapsed=101;Expect-Timeout {$null=Invoke-BetterGIRdpSessionProbe -Budget $budget};Assert-True ($script:Probe.Started-eq 0 -and $script:Probe.Killed-eq 0) 'Expired probe was started' })
$cases.Add('logging late Ready still cannot return success', { $script:SessionMode='active';Expect-Timeout {$null=Wait-BetterGIRdpSessionReadyForUser -UserName 'FixtureUser' -TimeoutSeconds 2 -LogAction {$script:Elapsed+=3000}} })
$cases.Add('UTC rollback cannot extend monotonic budget', {
    $budget=New-BetterGIRdpReadinessBudget -Milliseconds 1000;$script:Elapsed=900;$script:UtcSkew=-60000
    Assert-True ((Get-BetterGIRdpRemainingMilliseconds $budget)-eq 100) 'Clock rollback extended budget'
    $script:Elapsed=1001;Assert-True ((Get-BetterGIRdpRemainingMilliseconds $budget)-eq 0) 'Monotonic expiry was ignored'
})
$cases.Add('UTC forward expiry cannot dispatch', {
    $parent=New-BetterGIRdpReadinessBudget -Milliseconds 1000;$script:UtcSkew=2000
    $null=Confirm-BetterGIRdpFileSecurityWarning -ProcessId 44 -TargetAddress '127.0.0.2' -RdpUserName 'FixtureUser' -ParentBudget $parent
    Assert-True ($script:Clicks-eq 0) 'Expired absolute budget dispatched input'
})
$cases.Add('late native acknowledgement is not confirmation success', {
    $script:SendDelay=2000;$caught=$false
    try{$null=Confirm-BetterGIRdpFileSecurityWarning -ProcessId 44 -TargetAddress '127.0.0.2' -RdpUserName 'FixtureUser' -TimeoutSeconds 1}catch{if($_.Exception.Message-notmatch 'deadline'){throw};$caught=$true}
    Assert-True ($caught -and $script:Clicks-eq 1) 'Late native return was reported as successful confirmation'
})
$cases.Add('Kill accepted but still running at deadline is uncertain', {
    $script:Probe=New-FakeProbe -Mode hang -KillLeavesRunning $true;$budget=New-BetterGIRdpReadinessBudget -Milliseconds 100;$failure=$null
    try{$null=Invoke-BetterGIRdpSessionProbe -Budget $budget}catch{$failure=$_.Exception}
    Assert-True ($failure -is [AggregateException] -and $failure.Message-match 'termination.*uncertain' -and $failure.Message-match '4242') 'Kill acceptance was mistaken for owned probe exit'
    Assert-True ($failure.InnerExceptions[0].Message-match 'budget') 'Original timeout was lost'
    Assert-True ($script:Probe.Killed-eq 1 -and -not $script:Probe.HasExited -and $script:Probe.Waits.Count-eq 1 -and $script:Elapsed-eq 100 -and $script:Probe.Disposed) 'Cleanup opened a new wait window'
})
$cases.Add('pending Kill is confirmed only within original remaining', {
    $script:Probe=New-FakeProbe -Mode early-failure -KillLeavesRunning $true -ConfirmExit $true;$budget=New-BetterGIRdpReadinessBudget -Milliseconds 100;$failure=$null
    try{$null=Invoke-BetterGIRdpSessionProbe -Budget $budget}catch{$failure=$_.Exception}
    Assert-True ($failure -and $failure -isnot [AggregateException] -and $failure.Message-match 'original fake probe failure') 'Confirmed cleanup changed original failure'
    Assert-True ($script:Probe.HasExited -and $script:Probe.Waits.Count-eq 2 -and $script:Probe.Waits[1]-eq 100 -and $script:Elapsed-eq 10 -and $script:Probe.Disposed) 'Pending Kill was not confirmed in remaining budget'
})
$cases.Add('pending Kill that exhausts remaining preserves both faults', {
    $script:Probe=New-FakeProbe -Mode early-failure -KillLeavesRunning $true;$budget=New-BetterGIRdpReadinessBudget -Milliseconds 100;$failure=$null
    try{$null=Invoke-BetterGIRdpSessionProbe -Budget $budget}catch{$failure=$_.Exception}
    Assert-True ($failure -is [AggregateException] -and $failure.Message-match '4242' -and $failure.InnerExceptions[0].Message-match 'original fake probe failure') 'Unconfirmed termination or original failure was hidden'
    Assert-True ($script:Probe.Waits.Count-eq 2 -and $script:Elapsed-eq 100 -and $script:Probe.Disposed) 'Termination confirmation exceeded original budget'
})
$cases.Add('Ready timeout handler does not swallow pending termination', {
    $script:SessionMode='probe';$script:Probe=New-FakeProbe -Mode hang -KillLeavesRunning $true;$failure=$null
    try{$null=Wait-BetterGIRdpSessionReadyForUser -UserName 'FixtureUser' -TimeoutSeconds 1}catch{$failure=$_.Exception}
    Assert-True ($failure -is [AggregateException] -and $failure.Message-match '4242' -and $failure.Message-match 'termination.*uncertain') 'Ordinary timeout catch swallowed cleanup uncertainty'
    Assert-True ($script:Elapsed-eq 1000 -and $script:Probe.Waits.Count-eq 1) 'Ready opened another cleanup window'
})
$failures=0
foreach($case in $cases.GetEnumerator()) { Reset-Fixture;try{&$case.Value;Write-Output ('PASS '+$case.Key)}catch{$failures++;Write-Output ('FAIL '+$case.Key+': '+$_.Exception.Message)} }
if($failures){throw ('Offline deadline regressions failed: '+$failures)}
Write-Output ('PASS total='+$cases.Count)
