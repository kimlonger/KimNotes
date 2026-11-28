using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KimNotes.utils;
using KimNotes.Windows.Notes;
using KimNotes.Windows.Settings;
using Newtonsoft.Json.Linq;
using Forms = System.Windows.Forms;

[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]
internal static class WpfAccountSwitchRegression
{
    private sealed class AccountServer : HttpMessageHandler
    {
        internal readonly List<string> Titles = new List<string>();
        internal int Images;
        internal byte[] LastImage;
        internal string ImageId;
        internal JObject Uploaded;
        internal JObject RemoteChange;
        internal CloudSession Session;
        internal bool Revoked;
        internal int BrowserCodes;
        internal string Challenge;
        internal int RegistrationExchanges;
        internal string RegistrationChallenge;
        internal bool RejectRegistration;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            string path = request.RequestUri.AbsolutePath.Substring(CloudApi.Prefix.Length);
            object reply;
            if (path == "/auth/desktop/logout") reply = new { ok = true };
            else if (path == "/config") reply = new { inlineImageAttachmentsVersion = 1 };
            else if (path == "/auth/desktop-registration/consume")
            {
                var body = JObject.Parse(await request.Content.ReadAsStringAsync());
                string verifier = body.Value<string>("verifier");
                Guid deviceId;
                if (request.Method != HttpMethod.Post || request.RequestUri.Scheme != "https" ||
                    request.Headers.Authorization != null || body.Value<string>("code") != new string('R', 43) ||
                    verifier == null || verifier.Length != 43 || RegistrationChallenge != ComputeChallenge(verifier) ||
                    !Guid.TryParse(body.Value<string>("deviceId"), out deviceId) ||
                    string.IsNullOrWhiteSpace(body.Value<string>("deviceName")))
                    throw new Exception("Unexpected desktop registration exchange.");
                RegistrationExchanges++;
                if (RejectRegistration)
                    return new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        Content = new StringContent("{\"code\":\"expired\",\"message\":\"Expired fixture\"}", Encoding.UTF8, "application/json")
                    };
                reply = new
                {
                    user = new { id = Session.UserId, email = Session.Email },
                    token = Session.Token, refreshToken = Session.RefreshToken, sessionId = Session.SessionId,
                    protocolVersion = 2, expiresIn = 300, refreshExpiresIn = 86400
                };
            }
            else
            {
                if (request.Headers.Authorization == null || request.Headers.Authorization.Parameter != Session.Token)
                    throw new Exception("Unexpected credential at " + path);
                switch (path)
                {
                    case "/auth/browser-handoff":
                        if (request.Method != HttpMethod.Post) throw new Exception("A browser code must be requested with POST.");
                        Challenge = JObject.Parse(await request.Content.ReadAsStringAsync()).Value<string>("challenge");
                        BrowserCodes++;
                        reply = new { code = new string('C', 43) }; break;
                    case "/auth/me":
                        if (Revoked) return new HttpResponseMessage(HttpStatusCode.Unauthorized)
                            { Content = new StringContent("{\"code\":\"session_revoked\",\"message\":\"test session revoked\"}", Encoding.UTF8, "application/json") };
                        reply = new { user = new { id = Session.UserId } }; break;
                    case "/sync/pull":
                        var changes = new JArray();
                        if (RemoteChange != null) { changes.Add(RemoteChange); RemoteChange = null; }
                        reply = new { changes, cursor = "0", hasMore = false }; break;
                    case "/attachments":
                        Images++;
                        LastImage = await request.Content.ReadAsByteArrayAsync();
                        ImageId = Guid.NewGuid().ToString();
                        reply = new { id = ImageId }; break;
                    case "/sync/push":
                        var body = JObject.Parse(await request.Content.ReadAsStringAsync());
                        var op = body["operations"].Single();
                        Titles.Add((string)op["document"]["title"]);
                        if (op["document"]["attachments"].Any()) Uploaded = (JObject)op.DeepClone();
                        reply = new { results = new[] { new { status = "applied", note = new { id = (string)op["noteId"], revision = 1 } } } };
                        break;
                    default:
                        if (path == "/attachments/" + ImageId)
                            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(LastImage) };
                        throw new Exception("Unexpected request to " + path);
                }
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JObject.FromObject(reply).ToString(), Encoding.UTF8, "application/json") };
        }
    }

    private static CloudSession Identity(char key)
    {
        return new CloudSession { UserId = Guid.NewGuid().ToString(), Email = key + "@example.invalid",
            Token = new string(key, 43), ExpiresUtc = DateTime.UtcNow.AddHours(1),
            RefreshToken = new string(char.ToLowerInvariant(key), 43), RefreshExpiresUtc = DateTime.UtcNow.AddDays(5),
            SessionId = Guid.NewGuid().ToString(), ProtocolVersion = 2 };
    }

    private static void Wait(Task task)
    {
        var until = DateTime.UtcNow.AddSeconds(30);
        while (!task.IsCompleted && DateTime.UtcNow < until) { Forms.Application.DoEvents(); Thread.Sleep(2); }
        if (!task.IsCompleted) throw new TimeoutException("Account switch timed out.");
        task.GetAwaiter().GetResult();
    }

    private static string Save(NoteRepository repo, string title, bool picture)
    {
        var paragraph = new Paragraph(new Run(title));
        if (picture)
        {
            var pixels = new byte[] { 100, 20, 70, 255 };
            var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, pixels, 4);
            paragraph.Inlines.Add(new InlineUIContainer(new Image { Source = bitmap, Width = 8, Height = 8 }));
        }
        string path = repo.NewPath();
        repo.Save(path, new FlowDocument(paragraph), title, new NoteMetadata());
        return path;
    }

    private static void Check(bool passed, string message)
    {
        if (!passed) throw new Exception(message);
        Console.WriteLine("PASS " + message);
    }

    private static async Task CheckProfileHandoff(CloudSync sync, AccountServer wire)
    {
        var website = new Uri("https://notes.example.invalid/");
        Uri entry = null;
        Task handoff = ProfileBrowserHandoff.Open(website, path => { entry = new Uri(website, path); return null; },
            sync.IssueBrowserProfileCode, sync.AccountLifetimeToken);
        Check(entry != null && entry.AbsolutePath == "/login/" && entry.Query == "?desktop=profile" &&
            !entry.Fragment.Contains(wire.Session.Token) && !entry.Fragment.Contains(wire.Session.RefreshToken),
            "The profile pencil opens the browser handshake without either desktop credential.");
        var fields = entry.Fragment.TrimStart('#').Split('&').Select(item => item.Split('=')).ToDictionary(item => item[0], item => item[1]);
        var host = "127.0.0.1:" + fields["port"];
        var challenge = new string('D', 43);
        var callback = "http://" + host + "/profile/" + fields["state"] + "?challenge=" + challenge;
        using (var browser = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }))
        {
            using (var invalid = await browser.GetAsync(callback.Replace(fields["state"], "other-state")))
                Check(invalid.StatusCode == HttpStatusCode.BadRequest && wire.BrowserCodes == 0, "A mismatched callback state leaves the legitimate attempt open.");
            using (var request = new HttpRequestMessage(HttpMethod.Get, callback))
            {
                request.Headers.Host = "localhost:" + fields["port"];
                using (var invalid = await browser.SendAsync(request))
                    Check(invalid.StatusCode == HttpStatusCode.BadRequest && wire.BrowserCodes == 0, "Only the expected loopback Host can complete the request.");
            }
            using (var redirect = await browser.GetAsync(callback))
                Check(redirect.StatusCode == HttpStatusCode.Redirect && redirect.Headers.CacheControl.NoStore &&
                    redirect.Headers.Location == new Uri(website, "login/?desktop=profile#code=" + new string('C', 43)) &&
                    wire.Challenge == challenge && wire.BrowserCodes == 1,
                    "The valid callback requests one browser-bound code and redirects to the profile sign-in page.");
        }
        await handoff;
    }

    private static string ComputeChallenge(string verifier)
    {
        using (var hash = SHA256.Create())
            return Convert.ToBase64String(hash.ComputeHash(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static async Task CheckRegistrationHandoff(CloudSync sync, AccountServer wire)
    {
        var website = new Uri("https://notes.example.invalid/");
        using (var api = new CloudApi(wire))
        {
            using (var cancellation = new CancellationTokenSource())
            {
                Task abandoned = DesktopRegistrationHandoff.Open(website, path => null, api, sync, cancellation.Token);
                cancellation.Cancel();
                bool cancelled = false;
                try { await abandoned; } catch (OperationCanceledException) { cancelled = true; }
                Check(cancelled && sync.Session == null && wire.RegistrationExchanges == 0,
                    "Cancelling registration closes the pending callback without signing in.");
            }

            var rejectedWire = new AccountServer { Session = wire.Session, RejectRegistration = true };
            using (var rejectedApi = new CloudApi(rejectedWire))
            {
                Uri rejectedEntry = null;
                Task rejected = DesktopRegistrationHandoff.Open(website, path => { rejectedEntry = new Uri(website, path); return null; },
                    rejectedApi, sync, CancellationToken.None);
                var rejectedFields = rejectedEntry.Fragment.TrimStart('#').Split('&')
                    .Select(item => item.Split('=')).ToDictionary(item => item[0], item => item[1]);
                rejectedWire.RegistrationChallenge = rejectedFields["challenge"];
                var rejectedCallback = "http://127.0.0.1:" + rejectedFields["port"] + "/register/" + rejectedFields["state"] +
                    "?code=" + new string('R', 43);
                using (var browser = new HttpClient(new HttpClientHandler { UseProxy = false }))
                using (var fallback = await browser.GetAsync(rejectedCallback))
                {
                    var page = await fallback.Content.ReadAsStringAsync();
                    Check(fallback.StatusCode == HttpStatusCode.ServiceUnavailable && fallback.Headers.CacheControl.NoStore &&
                        page.Contains("注册成功") && page.Contains("用刚注册的邮箱和密码登录") && page.Contains("打开网页版便签") &&
                        page.Contains(new Uri(website, "notes/").AbsoluteUri) && !page.Contains("Expired fixture"),
                        "The failed automatic login provides a usable post-registration fallback.");
                }
                bool exchangeFailed = false;
                try { await rejected; } catch (CloudApiException) { exchangeFailed = true; }
                Check(exchangeFailed && rejectedWire.RegistrationExchanges == 1 && sync.Session == null,
                    "A rejected registration exchange leaves the desktop signed out.");
            }

            var siteFallbackWire = new AccountServer { Session = wire.Session, RejectRegistration = true };
            using (var siteFallbackApi = new CloudApi(siteFallbackWire))
            {
                Uri fallbackEntry = null;
                Task fallbackRegistration = DesktopRegistrationHandoff.Open(website,
                    path => { fallbackEntry = new Uri(website, path); return null; }, siteFallbackApi, sync, CancellationToken.None);
                var fallbackFields = fallbackEntry.Fragment.TrimStart('#').Split('&')
                    .Select(item => item.Split('=')).ToDictionary(item => item[0], item => item[1]);
                siteFallbackWire.RegistrationChallenge = fallbackFields["challenge"];
                var fallbackUrl = "http://127.0.0.1:" + fallbackFields["port"] + "/register/" + fallbackFields["state"] +
                    "?code=" + new string('R', 43) + "&result=site";
                using (var browser = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }))
                using (var fallback = await browser.GetAsync(fallbackUrl))
                {
                    Check(fallback.StatusCode == HttpStatusCode.SeeOther && fallback.Headers.CacheControl.NoStore &&
                        fallback.Headers.Location == new Uri(website, "login/?desktop=result&status=manual"),
                        "A site callback with a failed exchange returns to the website's manual-login result.");
                }
                try { await fallbackRegistration; } catch (CloudApiException) { }
            }

            Uri entry = null;
            Task registration = DesktopRegistrationHandoff.Open(website, path => { entry = new Uri(website, path); return null; },
                api, sync, CancellationToken.None);
            Check(entry != null && entry.AbsolutePath == "/login/" && entry.Query == "?mode=register&desktop=register&result=site",
                "Register opens the dedicated website entry.");
            var fields = entry.Fragment.TrimStart('#').Split('&').Select(item => item.Split('=')).ToDictionary(item => item[0], item => item[1]);
            Check(fields.Count == 3 && int.Parse(fields["port"]) >= 1024 && fields["state"].Length == 43 && fields["challenge"].Length == 43,
                "The registration entry only carries the loopback port, state and S256 challenge.");
            wire.RegistrationChallenge = fields["challenge"];
            var callback = "http://127.0.0.1:" + fields["port"] + "/register/" + fields["state"] + "?code=" + new string('R', 43);
            using (var browser = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }))
            {
                using (var wrong = await browser.GetAsync(callback.Replace(fields["state"], "other-state")))
                    Check(wrong.StatusCode == HttpStatusCode.BadRequest && wire.RegistrationExchanges == 0,
                        "An unrelated callback cannot start the desktop exchange.");
                using (var request = new HttpRequestMessage(HttpMethod.Get, callback))
                {
                    request.Headers.Host = "localhost:" + fields["port"];
                    using (var wrong = await browser.SendAsync(request))
                        Check(wrong.StatusCode == HttpStatusCode.BadRequest && wire.RegistrationExchanges == 0,
                            "Registration requires the exact loopback Host.");
                }
                using (var result = await browser.GetAsync(callback + "&result=site"))
                {
                    var confirmation = await result.Content.ReadAsStringAsync();
                    Check(result.StatusCode == HttpStatusCode.SeeOther && result.Headers.CacheControl.NoStore &&
                        result.Headers.Location == new Uri(website, "login/?desktop=result&status=success") &&
                        confirmation.Length == 0 && wire.RegistrationExchanges == 1,
                        "The completed callback returns to the website without exposing credentials.");
                }
            }
            await registration;
            Check(sync.Session.UserId == wire.Session.UserId && CloudStore.ReadSession().SessionId == wire.Session.SessionId,
                "Register establishes and persists the registered account's normal desktop session.");
            bool openedWhileSignedIn = false, rejectedWhileSignedIn = false;
            try
            {
                await DesktopRegistrationHandoff.Open(website, path => { openedWhileSignedIn = true; return null; },
                    api, sync, CancellationToken.None);
            }
            catch (OperationCanceledException) { rejectedWhileSignedIn = true; }
            Check(rejectedWhileSignedIn && !openedWhileSignedIn && wire.RegistrationExchanges == 1,
                "A signed-in instance cannot be switched by starting another registration handoff.");
        }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            Environment.SetEnvironmentVariable("KIMNOTES_DATA_DIR", args[0]);
            SynchronizationContext.SetSynchronizationContext(new Forms.WindowsFormsSynchronizationContext());
            InitConfig.InitSettings();
            var a = Identity('A');
            var b = Identity('B');
            var wire = new AccountServer { Session = a };
            var repo = new NoteRepository();
            using (var sync = new CloudSync(new CloudApi(wire), false))
            {
                Wait(CheckRegistrationHandoff(sync, new AccountServer { Session = a }));
                Wait(CheckProfileHandoff(sync, wire));
                string ledger = Path.Combine(CloudStore.Root, "owners.json");
                Directory.CreateDirectory(ledger);
                string unsaved = repo.NewPath();
                try
                {
                    bool refused = false;
                    try { repo.Save(unsaved, new FlowDocument(new Paragraph(new Run("must not leak"))), "must not leak", new NoteMetadata()); }
                    catch (IOException) { refused = true; }
                    catch (UnauthorizedAccessException) { refused = true; }
                    Check(refused && !File.Exists(unsaved), "A failed ownership write cannot leave an unowned local document.");
                }
                finally { Directory.Delete(ledger); }
                string typed = Save(repo, "Account A offline text", false);
                string pending = Save(repo, "Account A offline picture", true);
                var owners = JObject.Parse(File.ReadAllText(ledger));
                Check((string)owners[Path.GetFileName(typed)] == a.UserId && (string)owners[Path.GetFileName(pending)] == a.UserId,
                    "A's ownership is already durable before any network sync.");
                Task waitingProfile = ProfileBrowserHandoff.Open(new Uri("https://notes.example.invalid/"),
                    path => null, sync.IssueBrowserProfileCode, sync.AccountLifetimeToken);
                Wait(sync.SignOut());
                bool closedProfile = false;
                try { Wait(waitingProfile); } catch (OperationCanceledException) { closedProfile = true; }
                Check(closedProfile && wire.BrowserCodes == 1, "Signing out cancels a waiting browser handoff without issuing another code.");
                string guest = Save(repo, "Guest note", false);
                wire.Session = b;
                Wait(sync.SignIn(b));
                Console.WriteLine("B note pushes: " + string.Join(", ", wire.Titles) + " | B attachment uploads: " + wire.Images);
                Check(wire.Titles.SequenceEqual(new[] { "Guest note" }) && wire.Images == 0,
                    "B uploads the guest note and receives no text or pictures from A's unsynced note.");
                Check(!repo.VisibleFiles().Contains(typed) && !repo.VisibleFiles().Contains(pending) && repo.VisibleFiles().Contains(guest) && File.Exists(pending),
                    "A's pending note remains on disk and is absent from B's catalog.");
                wire.Session = a;
                wire.Titles.Clear();
                Wait(sync.SignIn(a));
                Console.WriteLine("A note pushes: " + string.Join(", ", wire.Titles) + " | A attachment uploads: " + wire.Images + " | " + sync.Status);
                Check(wire.Titles.Count == 2 && wire.Titles.Contains("Account A offline picture") && wire.Titles.Contains("Account A offline text") && wire.Images == 1 &&
                    repo.VisibleFiles().Contains(pending) && !repo.VisibleFiles().Contains(guest),
                    "Switching back to A uploads its pending note and exactly one attachment.");
                var webId = Guid.NewGuid().ToString();
                wire.RemoteChange = JObject.FromObject(new { id = webId, revision = 1,
                    document = new { title = "Browser note", text = "Web content", themeId = 1, attachments = new[] { wire.ImageId } } });
                Wait(sync.SyncNow());
                string downloaded = Path.Combine(repo.DirectoryPath, "cloud-" + webId + ".rtf");
                var webNote = repo.Load(downloaded);
                Check(NoteDocumentRtf.PreviewText(webNote).Contains("Web content") && NoteDocumentRtf.PreviewText(webNote).Contains("[图片]"),
                    "A text-and-image web note opens with its text and embedded picture in the WPF editor.");
                string copyId = Guid.NewGuid().ToString();
                wire.RemoteChange = JObject.FromObject(new { id = copyId, revision = 1, document = wire.Uploaded["document"].DeepClone() });
                Wait(sync.SyncNow());
                var cloudCopy = repo.Load(Path.Combine(repo.DirectoryPath, "cloud-" + copyId + ".rtf"));
                Check(NoteDocumentRtf.PreviewText(cloudCopy).Contains("[图片]"),
                    "The native inline attachment also survives a full download into a new WPF document.");
                wire.Revoked = true;
                Wait(sync.SyncNow());
                Check(sync.NeedsLogin && sync.Session.UserId == a.UserId, "The revoked session retains A's offline identity.");
                var expired = Save(repo, "Offline after expiry", false);
                Check(!CloudStore.BelongsTo(expired, b.UserId), "Offline saves under an expired identity retain that identity.");
            }
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
