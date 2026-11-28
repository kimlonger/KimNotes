param([Parameter(Mandatory = $true)][string]$Executable)

$ErrorActionPreference = 'Stop'
if ([Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { throw 'Run with Windows PowerShell -STA.' }
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
Add-Type -ReferencedAssemblies System.Net.Http -TypeDefinition @'
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
public sealed class TodoScopeOfflineHandler : HttpMessageHandler
{
    public readonly TaskCompletionSource<HttpResponseMessage> Login = new TaskCompletionSource<HttpResponseMessage>();
    public bool LoginRequested;
    public CancellationToken LoginToken;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
    {
        if (request.RequestUri.AbsolutePath.EndsWith("/auth/desktop/login")) {
            LoginRequested = true;
            LoginToken = cancel;
            return Login.Task;
        }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) {
            Content = new StringContent("{}")
        });
    }
}
'@
$original = $env:KIMNOTES_DATA_DIR
$profilePath = Join-Path ([IO.Path]::GetTempPath()) ('KimNotes-todo-scope-' + [Guid]::NewGuid().ToString('N'))
$env:KIMNOTES_DATA_DIR = $profilePath
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $Executable).Path)
$instance = [Reflection.BindingFlags]'Instance, Public, NonPublic'
$static = [Reflection.BindingFlags]'Static, Public, NonPublic'
$config = $assembly.GetType('KimNotes.utils.InitConfig', $true)
$store = $assembly.GetType('KimNotes.utils.TodoStore', $true)
$owner = $assembly.GetType('KimNotes.utils.CloudStore', $true).GetField('UserId', $static)
$editor = $assembly.GetType('KimNotes.Windows.Todos.TodoEditor', $true)
$repeat = [enum]::Parse($assembly.GetType('KimNotes.utils.RepeatRule'), 'None')
$failures = [Collections.Generic.List[string]]::new()

function Check([bool]$Condition, [string]$Message) {
    if ($Condition) { Write-Output "PASS $Message" }
    else { $failures.Add($Message); Write-Output "FAIL $Message" }
}
function Set-Owner($Id) {
    $owner.SetValue($null, $Id)
    $store.GetMethod('AccountChanged', $static).Invoke($null, @())
}
function Set-Privacy([bool]$Enabled) {
    $config.GetMethod('SetConfigValue', $static).Invoke($null, @('checkBox3', $Enabled.ToString()))
    $store.GetMethod('ResetForPrivacyChange', $static).Invoke($null, @())
}
function Create-Editor($Text) {
    $ctor = $editor.GetConstructors($instance) | Where-Object { $_.GetParameters().Count -eq 5 }
    $window = $ctor.Invoke(@($Text, $null, $repeat, $false, $false))
    $window.WindowStartupLocation = 'Manual'
    $window.Left = $window.Top = -32000
    $window.ShowActivated = $window.ShowInTaskbar = $false
    $window.Show()
    return $window
}
function Drain {
    [Windows.Threading.Dispatcher]::CurrentDispatcher.Invoke([Windows.Threading.DispatcherPriority]::ApplicationIdle, [Action]{})
}
function Wait-ForTask($Task) {
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    while (!$Task.IsCompleted -and [DateTime]::UtcNow -lt $deadline) { Drain; Start-Sleep -Milliseconds 10 }
    if (!$Task.IsCompleted) { throw 'The mock account request did not complete.' }
}

try {
    $app = [Activator]::CreateInstance($assembly.GetType('KimNotes.Windows.App'), $true)
    $app.ShutdownMode = 'OnExplicitShutdown'
    $previousContext = [Threading.SynchronizationContext]::Current
    [Threading.SynchronizationContext]::SetSynchronizationContext([Windows.Threading.DispatcherSynchronizationContext]::new())
    $config.GetMethod('InitSettings', $static).Invoke($null, @())
    $A = [Guid]::NewGuid().ToString()
    $B = [Guid]::NewGuid().ToString()
    $add = $store.GetMethod('Add', $static)
    $find = $store.GetMethod('Find', $static)
    $prepare = $store.GetMethod('PrepareCloudChanges', $static)
    $guest = $add.Invoke($null, @('Guest before sign-in', $null, $null, $null))
    Set-Owner $A
    $entryType = $assembly.GetType('KimNotes.utils.TodoCloudEntry', $true)
    $remote = [Activator]::CreateInstance($entryType, $true)
    $remote.Id = [Guid]::NewGuid().ToString()
    $remote.Text = 'A from a second device'
    $remote.CreatedAt = [DateTime]::UtcNow
    $remote.Revision = 1
    $remote.Notes = 'remote details'
    $remote.Priority = [enum]::Parse($assembly.GetType('KimNotes.utils.TodoPriority'), 'Important')
    $remote.Pinned = $true
    $page = [Array]::CreateInstance($entryType, 1)
    $page.SetValue($remote, 0)
    $merge = $store.GetMethod('MergeDownloaded', $static)
    $merge.Invoke($null, @($A, $page))
    $all = $store.GetProperty('All', $static)
    $download = $all.GetValue($null) | Where-Object Text -eq $remote.Text
    Check ($download.Pinned -and $download.Notes -eq $remote.Notes -and $download.Priority -eq $remote.Priority) 'Desktop imports priority, notes and pin from a versioned cloud task.'
    $synced = $add.Invoke($null, @('A acknowledged', $null, $null, $null))
    $batch = $prepare.Invoke($null, @($A, $true))
    $store.GetMethod('AcknowledgeCloudChanges', $static).Invoke($null, @($A, $batch, $true))
    $pending = $add.Invoke($null, @('A pending', $null, $null, $null))
    $reminderType = $assembly.GetType('KimNotes.Windows.Todos.TodoReminderService+ReminderWindow', $true)
    $due = [Array]::CreateInstance($pending.GetType(), 1)
    $due.SetValue($pending, 0)
    $reminder = $reminderType.GetConstructors($instance)[0].Invoke(@($due, [Action[int]]{}))
    $oldEditor = Create-Editor 'A draft'
    $listType = $assembly.GetType('KimNotes.Windows.Todos.TodoWindow', $true)
    $list = $listType.GetConstructors($instance)[0].Invoke(@($null))
    $search = $listType.GetField('search', $instance).GetValue($list)
    $search.Text = 'A confidential search'
    Set-Owner $A
    Drain
    Check ($oldEditor.IsVisible) 'A notification for the same account keeps the open draft.'
    Set-Owner $B
    Drain
    Check (!$oldEditor.IsVisible) 'Switching to B dismisses the open A draft.'
    Check (!$search.Text.Length) 'Switching to B clears the query from the A todo list.'
    Check ($null -eq $find.Invoke($null, @($download.Id))) 'The A download is hidden while B is signed in.'
    $newTask = $add.Invoke($null, @('B task', $null, $null, $null))
    Check (($prepare.Invoke($null, @($B, $true)).Text -join ',') -eq 'B task') 'B can upload only its own task after A claimed the guest task.'
    Check (($null -eq $find.Invoke($null, @( $synced.Id ))) -and ($null -eq $find.Invoke($null, @( $pending.Id ))) -and
        ($null -eq $find.Invoke($null, @( $guest.Id )))) 'B sees none of A acknowledged, pending, or claimed guest records.'
    Set-Owner $null
    Check ($null -ne $find.Invoke($null, @( $synced.Id ))) 'The signed-out device retains access to locally authored records.'
    Check ($null -eq $find.Invoke($null, @($download.Id))) 'The signed-out device hides A cloud downloads.'
    Set-Owner $A
    Check (($prepare.Invoke($null, @($A, $true)).Text -join ',') -eq 'A pending') 'Back on A, only A pending work is queued.'
    Check (!$editor.GetProperty('AppliesToCurrentScope', $instance).GetValue($oldEditor)) 'An editor from an earlier A session stays invalid after B then A.'
    Check (!$reminderType.GetProperty('AppliesToCurrentScope', $instance).GetValue($reminder)) 'A stale reminder action stays invalid on switching back to A.'
    $remote.Done = $true
    $remote.DoneAt = [DateTime]::UtcNow
    $remote.Revision = 2
    $merge.Invoke($null, @($A, $page))
    Check ($find.Invoke($null, @($download.Id)).Done) 'Desktop receives the newer completion state.'
    $remote.DeletedAt = [DateTime]::UtcNow
    $remote.Revision = 3
    $merge.Invoke($null, @($A, $page))
    Check ($null -eq $find.Invoke($null, @($download.Id))) 'A remote tombstone removes the downloaded task from the desktop list.'
    Set-Privacy $true
    $privateEditor = Create-Editor 'Discarded private draft'
    Set-Privacy $false
    Set-Privacy $true
    Drain
    Check (!$privateEditor.IsVisible -and !$editor.GetProperty('AppliesToCurrentScope', $instance).GetValue($privateEditor)) 'A private draft cannot reappear in a subsequent private session.'
    Set-Privacy $false
    Set-Owner $null
    $syncType = $assembly.GetType('KimNotes.utils.CloudSync', $true)
    $api = $assembly.GetType('KimNotes.utils.CloudApi', $true).GetConstructors($instance)[0].Invoke(@([TodoScopeOfflineHandler]::new()))
    $sync = $syncType.GetConstructors($instance)[0].Invoke(@($api, $false, $null))
    $accountType = $assembly.GetType('KimNotes.Windows.Settings.AccountWindow', $true)
    $account = $accountType.GetConstructors($instance)[0].Invoke(@($sync))
    $handler = [TodoScopeOfflineHandler]::new()
    $accountApi = $accountType.GetField('api', $instance)
    $accountApi.GetValue($account).Dispose()
    $accountApi.SetValue($account, $api.GetType().GetConstructors($instance)[0].Invoke(@($handler)))
    $secret = $accountType.GetField('password', $instance).GetValue($account)
    $visible = $accountType.GetField('visiblePassword', $instance).GetValue($account)
    $inputs = $visible.Parent.Parent
    $toggle = $inputs.Children | Where-Object { $_ -is [Windows.Controls.Button] }
    $secret.Password = 'synthetic test credential'
    $toggle.RaiseEvent([Windows.RoutedEventArgs]::new([Windows.Controls.Button]::ClickEvent))
    Set-Privacy $true
    $syncType.GetMethod('ApplyPrivacyMode', $instance).Invoke($sync, @())
    Drain
    Check (!$secret.Password.Length -and !$visible.Text.Length -and $visible.Visibility -eq 'Collapsed' -and
        !$secret.IsEnabled) 'Entering private mode clears revealed credentials and disables login input.'
    Set-Privacy $false
    $syncType.GetMethod('ApplyPrivacyMode', $instance).Invoke($sync, @())
    Drain
    Check ($secret.IsEnabled -and $visible.Visibility -eq 'Collapsed') 'Leaving private mode starts with a masked login form.'
    $accountType.GetField('email', $instance).GetValue($account).Text = 'pending@example.test'
    $secret.Password = $visible.Text = 'synthetic'
    $login = $accountType.GetMethod('Login', $instance).Invoke($account, @())
    if (!$handler.LoginRequested) { throw 'The login did not reach the isolated handler.' }
    foreach ($private in @($true,$false)) {
        Set-Privacy $private
        $syncType.GetMethod('ApplyPrivacyMode', $instance).Invoke($sync, @())
    }
    Check ($handler.LoginToken.IsCancellationRequested) 'Switching to private mode cancels the pending HTTP login request.'
    $payload = @{user = @{ id = $A; email = 'pending@example.test' }; token = 'x' * 43; refreshToken = 'y' * 43
        sessionId = [Guid]::NewGuid().ToString(); protocolVersion = 2; expiresIn = 600; refreshExpiresIn = 86400 } | ConvertTo-Json
    $response = [Net.Http.HttpResponseMessage]::new([Net.HttpStatusCode]::OK)
    $response.Content = [Net.Http.StringContent]::new($payload)
    $handler.Login.SetResult($response)
    Wait-ForTask $login
    Check (!$owner.GetValue($null) -and !$login.IsFaulted) 'A late HTTP login response cannot sign in across a privacy round trip.'
    if ($owner.GetValue($null)) { Wait-ForTask ($syncType.GetMethod('SignOut', $instance).Invoke($sync, @())) }
    $sessionType = $assembly.GetType('KimNotes.utils.CloudSession', $true)
    $queued = [Activator]::CreateInstance($sessionType, $true)
    $queued.UserId = $A
    $queued.Email = 'queued@example.test'
    $queued.Token = 'x' * 43
    $queued.ExpiresUtc = [DateTime]::UtcNow.AddHours(1)
    $gate = $syncType.GetField('gate', $instance).GetValue($sync)
    if (!$gate.Wait(0)) { throw 'The isolated sync instance unexpectedly held its gate.' }
    try {
        $signIn = $syncType.GetMethod('SignIn', $instance).Invoke($sync, @($queued, [Threading.CancellationToken]::None, $true))
        Set-Privacy $true
        $syncType.GetMethod('ApplyPrivacyMode', $instance).Invoke($sync, @())
        Set-Privacy $false
        $syncType.GetMethod('ApplyPrivacyMode', $instance).Invoke($sync, @())
    } finally { $null = $gate.Release() }
    Wait-ForTask $signIn
    $session = $syncType.GetProperty('Session', $instance)
    Check ($signIn.IsCanceled -and !$owner.GetValue($null) -and !$session.GetValue($sync)) 'A login waiting behind sync cannot install an obsolete session after a privacy round trip.'
    $signIn = $syncType.GetMethod('SignIn', $instance).Invoke($sync, @($queued, [Threading.CancellationToken]::None, $true))
    Wait-ForTask $signIn
    if ($signIn.IsFaulted) { throw $signIn.Exception }
    if ($owner.GetValue($null) -ne $A -or !$gate.Wait(0)) { throw 'Could not establish the test account for sign-out.' }
    try {
        $signOut = $syncType.GetMethod('SignOut', $instance).Invoke($sync, @())
        Set-Privacy $true
        $syncType.GetMethod('ApplyPrivacyMode', $instance).Invoke($sync, @())
        Set-Privacy $false
        $syncType.GetMethod('ApplyPrivacyMode', $instance).Invoke($sync, @())
    } finally { $null = $gate.Release() }
    Wait-ForTask $signOut
    Check ($signOut.IsCanceled -and $owner.GetValue($null) -eq $A -and $session.GetValue($sync).UserId -eq $A) 'A queued logout cannot clear the session after its scope changed.'
    if ($failures.Count) { throw ($failures -join [Environment]::NewLine) }
}
finally {
    if ($account) { $account.Close() }
    if ($sync) { $sync.Dispose() }
    if ($app) { foreach ($window in @($app.Windows)) { $window.Close() } }
    [Threading.SynchronizationContext]::SetSynchronizationContext($previousContext)
    $env:KIMNOTES_DATA_DIR = $original
    $resolved = [IO.Path]::GetFullPath($profilePath)
    if ((Test-Path -LiteralPath $resolved) -and [IO.Path]::GetDirectoryName($resolved) -eq [IO.Path]::GetTempPath().TrimEnd('\') -and
        [IO.Path]::GetFileName($resolved) -like 'KimNotes-todo-scope-*') { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
