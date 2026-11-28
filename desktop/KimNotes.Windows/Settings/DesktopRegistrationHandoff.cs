using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KimNotes.utils;

namespace KimNotes.Windows.Settings
{
    internal static class DesktopRegistrationHandoff
    {
        private const string SiteResultPreference = "&result=site";
        private const string RegisterPath = "login/?mode=register&desktop=register" + SiteResultPreference;
        private const string ConsumePath = "/auth/desktop-registration/consume";
        private static readonly TimeSpan Limit = TimeSpan.FromMinutes(12);

        internal static async Task Open(Uri website, Func<string, string> openWebsite, CloudApi api, CloudSync sync,
            CancellationToken cancel)
        {
            CheckAccount(sync, cancel);
            if (website == null || !api.IsConfigured) throw new InvalidOperationException(DesktopEndpoints.MissingMessage);
            string state = BrowserLoopbackCallback.NewToken();
            string verifier = BrowserLoopbackCallback.NewToken();
            string challenge;
            using (var sha = SHA256.Create())
                challenge = BrowserLoopbackCallback.Encode(sha.ComputeHash(Encoding.ASCII.GetBytes(verifier)));
            using (var callback = await BrowserLoopbackCallback.Wait("/register/" + state, "code",
                port => openWebsite(RegisterPath + "#port=" + port + "&state=" + state + "&challenge=" + challenge),
                Limit, "此登录链接已失效。", "等待已超时。注册成功后，可在这里用新账号登录。", cancel, SiteResultPreference))
            {
                try
                {
                    CheckAccount(sync, cancel);
                    string deviceId = Guid.NewGuid().ToString();
                    var response = await api.Json(ConsumePath,
                        new { code = callback.Value, verifier, deviceName = Environment.MachineName, deviceId }, cancel: cancel);
                    CheckAccount(sync, cancel);
                    await sync.SignIn(CloudSession.FromResponse(response, deviceId), cancel, waitForSync: false);
                }
                catch
                {
                    await CompleteBrowser(callback, website, signedIn: false);
                    throw;
                }
                await CompleteBrowser(callback, website, signedIn: true);
            }
        }

        private static Task CompleteBrowser(BrowserLoopbackCallback callback, Uri website, bool signedIn)
        {
            if (callback.HasResultPreference)
                return callback.RedirectResult(new Uri(website, "login/?desktop=result&status=" + (signedIn ? "success" : "manual")));

            return callback.Html(signedIn ? "200 OK" : "503 Service Unavailable", RegistrationResultPage.Render(website, signedIn));
        }

        private static void CheckAccount(CloudSync sync, CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested();
            if (Storage.Incognito || (sync.Session != null && !sync.NeedsLogin))
                throw new OperationCanceledException();
        }

    }
}
