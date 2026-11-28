using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace KimNotes.Windows.Settings
{
    internal static class ProfileBrowserHandoff
    {
        private const string LoginPath = "login/?desktop=profile";
        private const string ExpiredMessage = "网页登录已超时，请再点一次编辑资料。";
        private static readonly TimeSpan Limit = TimeSpan.FromMinutes(2);

        internal static async Task Open(Uri website, Func<string, string> openWebsite,
            Func<string, CancellationToken, Task<string>> issueCode, CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested();
            if (website == null) throw new InvalidOperationException(utils.DesktopEndpoints.MissingMessage);
            string state = BrowserLoopbackCallback.NewToken();
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel))
            {
                timeout.CancelAfter(Limit);
                try
                {
                    using (var callback = await BrowserLoopbackCallback.Wait("/profile/" + state, "challenge",
                        port => openWebsite(LoginPath + "#port=" + port + "&state=" + state), Limit,
                        "无效的资料编辑请求。", ExpiredMessage, timeout.Token))
                    {
                        string code;
                        try
                        {
                            code = await issueCode(callback.Value, timeout.Token);
                            timeout.Token.ThrowIfCancellationRequested();
                            if (!BrowserLoopbackCallback.IsToken(code)) throw new InvalidDataException("网页登录响应无效。");
                        }
                        catch
                        {
                            await callback.Text("503 Service Unavailable", "暂时无法打开资料页，请回到小羊便签重试。");
                            throw;
                        }
                        await callback.Redirect(new Uri(website, LoginPath + "#code=" + code));
                    }
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                {
                    cancel.ThrowIfCancellationRequested();
                    throw new TimeoutException(ExpiredMessage);
                }
            }
        }
    }
}
