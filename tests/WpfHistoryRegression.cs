using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using KimNotes;
using KimNotes.utils;
using KimNotes.Windows.Notes;
using Newtonsoft.Json.Linq;

internal static class WpfHistoryRegression
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int failures;

    private sealed class Server : HttpMessageHandler
    {
        internal string VerifiedUser;
        internal Func<HttpRequestMessage, Task<HttpResponseMessage>> Respond;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken cancel)
        {
            if (req.RequestUri.AbsolutePath.EndsWith("/auth/me"))
                return Task.FromResult(VerifiedUser == null ? Json(new JObject(), HttpStatusCode.ServiceUnavailable)
                    : Json(new JObject { { "user", new JObject { { "id", VerifiedUser } } } }));
            return Respond(req);
        }
    }

    private static HttpResponseMessage Json(JObject body, HttpStatusCode status = HttpStatusCode.OK)
    {
        return new HttpResponseMessage(status) { Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json") };
    }
    private static Task<HttpResponseMessage> Timeout()
    {
        var request = new TaskCompletionSource<HttpResponseMessage>();
        request.SetCanceled();
        return request.Task;
    }
    private static T Field<T>(object owner, string name)
    {
        return (T)owner.GetType().GetField(name, Fields).GetValue(owner);
    }
    private static Task Call(object owner, string name)
    {
        return (Task)owner.GetType().GetMethod(name, Fields).Invoke(owner, null);
    }
    private static void Until(Func<bool> ready)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!ready())
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            if (DateTime.UtcNow > deadline) throw new TimeoutException("History fixture timed out.");
            Thread.Sleep(1);
        }
    }
    private static void Wait(Task task) { Until(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static void Check(bool passed, string text)
    {
        Console.WriteLine((passed ? "PASS " : "FAIL ") + text);
        if (!passed) failures++;
    }
    private static CloudSession Account(char token)
    {
        return new CloudSession {
            UserId = Guid.NewGuid().ToString(), Token = new string(token, 43), SessionId = Guid.NewGuid().ToString(),
            ProtocolVersion = 2, RefreshToken = new string('r', 43), ExpiresUtc = DateTime.UtcNow.AddHours(1),
            RefreshExpiresUtc = DateTime.UtcNow.AddDays(1)
        };
    }
    private static JObject Snapshot(string id, int revision, string text)
    {
        return new JObject { { "id", id }, { "revision", revision }, { "document", new JObject { { "text", text } } } };
    }

    [STAThread] private static int Main(string[] args)
    {
        Environment.SetEnvironmentVariable("KIMNOTES_DATA_DIR", args[0]);
        InitConfig.InitSettings();
        var application = new System.Windows.Application();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var first = Account('a');
        var second = Account('b');
        const string filename = "history-test.rtf";
        string path = Path.Combine(InitConfig.GetConfigValue("notesPath"), filename);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, @"{\rtf1\ansi Original}");
        string id = Guid.NewGuid().ToString();
        var state = new CloudState();
        state.Notes[filename] = new CloudRecord { RemoteId = id, Revision = 1, LocalHash = CloudStore.FileHash(path) };
        CloudStore.SaveState(first.UserId, state);
        CloudStore.Claim(filename, first.UserId);
        var server = new Server();
        using (var sync = new CloudSync(new CloudApi(server), false))
        {
            Wait(sync.SignIn(first));
            // Start with the desktop copy from before another client restored an older version.
            // Deliver the server's resulting current snapshot via normal incremental pull.
            server.VerifiedUser = first.UserId;
            server.Respond = req => {
                if (req.RequestUri.AbsolutePath.EndsWith("/config")) return Task.FromResult(Json(new JObject()));
                if (!req.RequestUri.AbsolutePath.EndsWith("/sync/pull")) throw new InvalidOperationException("Unexpected sync route.");
                return Task.FromResult(Json(new JObject { { "changes", new JArray(Snapshot(id, 2, "Restored in browser")) },
                    { "cursor", "2" }, { "hasMore", false } }));
            };
            Wait(sync.SyncNow());
            server.VerifiedUser = null;
            Check(File.ReadAllText(path).Contains("Restored in browser") && CloudStore.ReadState(first.UserId).Notes[filename].Revision == 2,
                "normal desktop pull installs a version restored by another client into the existing local file");
            var older = Snapshot(id, 1, "old");
            older["document"]["attachments"] = new JArray(Guid.NewGuid().ToString(), Guid.NewGuid().ToString());
            var current = Snapshot(id, 2, "current");
            using (var panel = new NoteVersionsPanel(sync, filename, "current", NoteTheme.All[0], delegate { }))
            {
                var status = Field<TextBlock>(panel, "status");
                var versions = Field<ListBox>(panel, "versions");
                server.Respond = req => Timeout();
                Wait(Call(panel, "LoadMore"));
                Check(status.Text.Contains("超时") && Field<Button>(panel, "more").IsEnabled &&
                    (string)Field<Button>(panel, "more").Content == "重试加载", "history timeout offers a working retry");
                server.Respond = req => req.RequestUri.AbsolutePath.EndsWith("/versions")
                    ? Task.FromResult(Json(new JObject { { "versions", new JArray(current, older) } })) : Timeout();
                Wait(Call(panel, "LoadMore"));
                Check(versions.Items.Count == 2, "retry loads both versions");
                versions.SelectedIndex = 1;
                Until(() => Field<CancellationTokenSource>(panel, "loading") == null);
                Check(status.Text.Contains("超时"), "an attachment timeout is visible alongside its text preview");
                versions.SelectedIndex = 0;
                Check(string.IsNullOrEmpty(status.Text), "selecting a healthy version clears the earlier preview error");
                versions.SelectedIndex = -1;
                var preview = Field<RichTextBox>(panel, "preview").Document;
                Check(string.IsNullOrWhiteSpace(new TextRange(preview.ContentStart, preview.ContentEnd).Text),
                    "clearing selection also clears the previous version's contents");
                versions.SelectedIndex = 1;
                Until(() => Field<CancellationTokenSource>(panel, "loading") == null);
                // An already confirmed operation bypasses the Yes/No dialog and is safe to retry.
                string operation = Guid.NewGuid().ToString();
                panel.GetType().GetField("operation", Fields).SetValue(panel, operation);
                server.Respond = req => Timeout();
                Wait(Call(panel, "RestoreVersion"));
                Check(status.Text.Contains("超时") && Field<string>(panel, "operation") == operation &&
                    (string)Field<Button>(panel, "restore").Content == "重试恢复", "restore timeout preserves its retry identity");
            }

            var restoreReply = new TaskCompletionSource<HttpResponseMessage>();
            bool sent = false;
            server.Respond = req => { sent = true; return restoreReply.Task; };
            var restore = sync.RestoreNoteVersion(filename, id, 2, 1, Guid.NewGuid().ToString(), CancellationToken.None);
            Until(() => sent);
            File.WriteAllText(path, @"{\rtf1\ansi Typed while restoring}");
            restoreReply.SetResult(Json(new JObject { { "results", new JArray(new JObject {
                { "status", "applied" }, { "note", Snapshot(id, 3, "old") } }) } }));
            Wait(restore);
            Check(restore.Result && File.ReadAllText(path).Contains("Typed while restoring") &&
                CloudStore.ReadState(first.UserId).Deferred.ContainsKey(id), "remote restore preserves edits made while waiting");

            var listReply = new TaskCompletionSource<HttpResponseMessage>();
            var imageReply = new TaskCompletionSource<HttpResponseMessage>();
            var secondAccountRequests = new List<string>();
            server.Respond = req => {
                if (req.Headers.Authorization.Parameter == second.Token)
                {
                    secondAccountRequests.Add(req.RequestUri.AbsolutePath);
                    return Task.FromResult(Json(new JObject(), HttpStatusCode.NotFound));
                }
                return req.RequestUri.AbsolutePath.EndsWith("/versions") ? listReply.Task : imageReply.Task;
            };
            var read = sync.ReadNoteVersions(filename, null, CancellationToken.None);
            var rich = sync.PreviewNoteVersion(filename, older, CancellationToken.None);
            Wait(sync.SignIn(second));
            listReply.SetResult(Json(new JObject { { "code", "access_expired" } }, HttpStatusCode.Unauthorized));
            imageReply.SetResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 0 }) });
            Until(() => read.IsCompleted && rich.IsCompleted);
            Check(read.Status != TaskStatus.RanToCompletion && rich.Status != TaskStatus.RanToCompletion && secondAccountRequests.Count == 0,
                "switching accounts discards the prior history and sends no version or attachment retries as the new user");
        }
        GC.KeepAlive(application);
        return failures == 0 ? 0 : 1;
    }

}
