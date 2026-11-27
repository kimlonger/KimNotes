param([Parameter(Mandatory = $true)][string]$Executable)

$ErrorActionPreference = 'Stop'
if ([Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { throw 'Run with Windows PowerShell -STA.' }
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase, System.Windows.Forms
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $Executable).Path)
$type = $assembly.GetType('KimNotes.Windows.Runtime.GlobalHotkeys', $true)
$instance = [Reflection.BindingFlags]'Instance, NonPublic'
$static = [Reflection.BindingFlags]'Static, NonPublic'
$restore = $type.GetMethod('Restore', $instance)
$apply = $type.GetMethod('Apply', $instance)
$suspend = $type.GetMethod('SetSuspended', $instance)
$active = $type.GetField('active', $instance)
$profile = Join-Path ([IO.Path]::GetTempPath()) ('KimNotes-hotkeys-' + [guid]::NewGuid().ToString('N'))
$originalProfile = $env:KIMNOTES_DATA_DIR
$env:KIMNOTES_DATA_DIR = $profile

function KeyMap($First, $Second, $Third) {
    $map = [Collections.Generic.Dictionary[string,string]]::new()
    $map['shortcutKey'] = $First; $map['recallKey'] = $Second; $map['previousImageKey'] = $Third
    $map['nextImageKey'] = 'None'; $map['latestNoteKey'] = 'None'
    return $map
}
function Assert-Keys([bool]$Condition, [string]$Description) {
    if (-not $Condition) { throw $Description }
    Write-Output $Description
}
function Reports-Conflict([scriptblock]$Action) {
    try { $null = & $Action; return $false }
    catch { return $_.Exception.GetBaseException() -is [InvalidOperationException] }
}

try {
    $emptyActions = [Action[]]@()
    $hotkeys = [Activator]::CreateInstance($type, $instance, $null, (,$emptyActions), $null)
    $other = [Activator]::CreateInstance($type, $instance, $null, (,$emptyActions), $null)
    $owner = $type.GetField('sink', $instance).GetValue($other).Handle
    $register = $type.GetMethod('RegisterHotKey', $static)
    $unregister = $type.GetMethod('UnregisterHotKey', $static)
    function CanReserve([Windows.Forms.Keys]$Key) {
        if (-not $register.Invoke($null, @($owner, 40, [uint32]0x4002, [uint32]$Key))) { return $false }
        $null = $unregister.Invoke($null, @($owner, 40))
        return $true
    }
    # Only use otherwise available extended function keys and never send keyboard input.
    $free = @(13..24 | ForEach-Object { [Windows.Forms.Keys]("F$_") } | Where-Object { CanReserve $_ } | Select-Object -First 4)
    if ($free.Count -lt 4) { throw 'Four unused extended function keys are required.' }
    $apply.Invoke($other, (,(KeyMap $free[1] 'None' 'None')))
    $configured = KeyMap $free[0] $free[1] $free[2]
    Assert-Keys ((Reports-Conflict { $restore.Invoke($hotkeys, (,$configured)) }) -and
        ($active.GetValue($hotkeys) -join ',') -eq '1,3') 'Startup reports the occupied binding while retaining available bindings on both sides.'
    $suspend.Invoke($hotkeys, @($true))
    Assert-Keys ((CanReserve $free[0]) -and (CanReserve $free[2])) 'Capturing a replacement key releases active bindings.'
    Assert-Keys ((Reports-Conflict { $suspend.Invoke($hotkeys, @($false)) }) -and
        ($active.GetValue($hotkeys) -join ',') -eq '1,3') 'Resuming after capture restores every available shortcut even with a conflict.'
    $proposed = KeyMap $free[3] $free[1] $free[2]
    Assert-Keys ((Reports-Conflict { $apply.Invoke($hotkeys, (,$proposed)) }) -and (CanReserve $free[3]) -and
        -not (CanReserve $free[0]) -and -not (CanReserve $free[2])) 'An invalid save releases the proposed bindings and restores the working subset of the previous configuration.'
    $other.Dispose(); $other = $null
    $suspend.Invoke($hotkeys, @($true)); $suspend.Invoke($hotkeys, @($false))
    Assert-Keys (($active.GetValue($hotkeys) -join ',') -eq '1,2,3') 'Once the conflicting registration is released the missing shortcut can be restored.'
} finally {
    if ($other) { $other.Dispose() }
    if ($hotkeys) { $hotkeys.Dispose() }
    $env:KIMNOTES_DATA_DIR = $originalProfile
    if ([IO.File]::Exists((Join-Path $profile 'error.log'))) { [IO.File]::Delete((Join-Path $profile 'error.log')) }
    if ([IO.Directory]::Exists($profile)) { [IO.Directory]::Delete($profile) }
}
