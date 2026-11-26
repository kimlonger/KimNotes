using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

// Validate the shipped EXE's compatibility metadata, not the test host's target.
// A fresh AppDomain exercises its real HTTP clients without opening any windows.
internal static class ReleaseNetworkRegression
{
    static int Main(string[] args)
    {
        AppDomain domain = null;
        try
        {
            string executable = Path.GetFullPath(args[0]);
            var assembly = Assembly.ReflectionOnlyLoadFrom(executable);
            var metadata = CustomAttributeData.GetCustomAttributes(assembly).SingleOrDefault(a =>
                a.Constructor.DeclaringType.FullName == typeof(TargetFrameworkAttribute).FullName);
            if (metadata == null) throw new Exception("Release EXE is missing TargetFrameworkAttribute; HTTPS can fall back to SSL3/TLS 1.0.");
            string target = (string)metadata.ConstructorArguments[0].Value;
            if (target != ".NETFramework,Version=v4.8") throw new Exception("Unexpected release target: " + target);
            Console.WriteLine("PASS: packaged EXE declares " + target);
            var config = XDocument.Load(executable + ".config");
            if ((string)config.Root.Element("startup").Element("supportedRuntime").Attribute("sku") != target)
                throw new Exception("Packaged EXE and runtime config have different framework targets.");
            Console.WriteLine("PASS: packaged runtime config matches the EXE");
            domain = AppDomain.CreateDomain("KimNotes release HTTPS check", null, new AppDomainSetup {
                ApplicationBase = Path.GetDirectoryName(typeof(ReleaseNetworkRegression).Assembly.Location),
                ConfigurationFile = executable + ".config", TargetFrameworkName = target
            });
            var probe = (ReleaseNetworkProbe)domain.CreateInstanceFromAndUnwrap(
                typeof(ReleaseNetworkRegression).Assembly.Location, typeof(ReleaseNetworkProbe).FullName);
            probe.Run(executable, args.Contains("--online"));
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine("FAIL: " + error.GetBaseException().Message); return 1; }
        finally { if (domain != null) AppDomain.Unload(domain); }
    }
}

public sealed class ReleaseNetworkProbe : MarshalByRefObject
{
    public override object InitializeLifetimeService() { return null; }

    public void Run(string executable, bool online)
    {
        if (ServicePointManager.SecurityProtocol != SecurityProtocolType.SystemDefault)
            throw new Exception("Fresh release runtime must negotiate HTTPS using system defaults, got " + ServicePointManager.SecurityProtocol);
        Console.WriteLine("PASS: fresh release runtime uses system HTTPS defaults");
        if (!online) return;
        var app = Assembly.LoadFrom(executable);
        var update = app.GetType("KimNotes.utils.UpdateNotice").GetMethod("FetchVersionAsync", BindingFlags.Static | BindingFlags.NonPublic);
        string version = ((Task<string>)update.Invoke(null, null)).GetAwaiter().GetResult();
        Version parsed;
        if (!Version.TryParse(version, out parsed)) throw new Exception("Invalid public update version");
        Console.WriteLine("PASS: packaged update client fetched public version " + version);
        var apiType = app.GetType("KimNotes.utils.CloudApi");
        using (var api = (IDisposable)Activator.CreateInstance(apiType, BindingFlags.Instance | BindingFlags.NonPublic,
            null, new object[] { null }, null))
        {
            var json = apiType.GetMethod("Json", BindingFlags.Instance | BindingFlags.NonPublic);
            var config = (Task)json.Invoke(api, new object[] { "/config", null, null, null, CancellationToken.None });
            config.GetAwaiter().GetResult();
            Console.WriteLine("PASS: packaged cloud client connected to public app config");
            // Empty fields stop at input validation; no real account or password is sent.
            try
            {
                var login = (Task)json.Invoke(api, new object[] { "/auth/desktop/login", new { email = "", password = "" }, null, null, CancellationToken.None });
                login.GetAwaiter().GetResult();
                throw new Exception("Empty login unexpectedly succeeded");
            }
            catch (Exception error)
            {
                var cause = error.GetBaseException();
                if (cause.GetType().FullName != "KimNotes.utils.CloudApiException") throw;
                int status = (int)cause.GetType().GetProperty("Status", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(cause, null);
                if (status != 400 && status != 422) throw new Exception("Unexpected empty login validation status " + status);
                Console.WriteLine("PASS: packaged desktop login reached server validation (HTTP " + status + ")");
            }
        }
        if (ServicePointManager.SecurityProtocol != SecurityProtocolType.SystemDefault)
            throw new Exception("A packaged client changed process HTTPS policy");
        Console.WriteLine("PASS: update and account clients preserve HTTPS defaults");
    }
}
