using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using OpenQA.Selenium;

// Third-party consumer example. References the INSTALLED Selenium.WebDriver NuGet
// package (restored from a local feed — NOT the source tree) and proves the
// bundled engine .so (shipped under runtimes/linux-x64/native/) loads and drives
// the protocol, with SELENIUM_CORE_LIB unset so only the package's own bundled
// .so can satisfy the load.
//   ffi | discovery | live
internal static class Program
{
    private static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0] : "ffi";
        switch (mode)
        {
            case "ffi":
                ModeFfi();
                break;
            case "discovery":
                ModeDiscovery();
                break;
            case "live":
                ModeLive();
                break;
            default:
                Console.Error.WriteLine("unknown mode: " + mode);
                return 1;
        }
        return 0;
    }

    private static void Fail(string msg)
    {
        Console.Error.WriteLine("FAIL: " + msg);
        Environment.Exit(1);
    }

    private static void ModeFfi()
    {
        if (RemoteWebDriver.Route("get") != "POST /session/:sessionId/url") Fail("route mismatch");
        if (RemoteWebDriver.ErrorCode("no such element") != 17) Fail("errorCode mismatch");
        if (!RemoteWebDriver.Locator("id", "main").Contains("*[id=")) Fail("locator mismatch");
        bool threw = false;
        try
        {
            RemoteWebDriver.Chrome("http://127.0.0.1:1");
        }
        catch (WebDriverException e)
        {
            threw = e.Code == -1;
        }
        if (!threw) Fail("expected transport failure");
        Console.WriteLine("consumer(ffi): OK — installed package loaded its bundled .so and marshalled");
    }

    private static void ModeDiscovery()
    {
        string? env = Environment.GetEnvironmentVariable("SELENIUM_CORE_LIB");
        if (!string.IsNullOrEmpty(env)) Fail("SELENIUM_CORE_LIB set; discovery must run without it");
        // Forcing a native call proves the bundled .so (runtimes/.../native) loaded.
        if (RemoteWebDriver.Route("newSession") != "POST /session") Fail("route mismatch (bundled .so did not load)");
        Console.WriteLine("consumer(discovery): OK — zero-config bundled-.so discovery works");
    }

    private static void ModeLive()
    {
        string? driver = Which("chromedriver");
        if (driver == null)
        {
            Console.WriteLine("consumer(live): SKIPPED — chromedriver not on PATH");
            return;
        }
        int webPort = FreePort();
        using var webCts = new System.Threading.CancellationTokenSource();
        StartHelloWebSite(webPort, webCts.Token);

        int cdPort = FreePort();
        var cd = Process.Start(new ProcessStartInfo(driver, $"--port={cdPort}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        try
        {
            if (!WaitUp(cdPort, 10000))
            {
                Console.WriteLine("consumer(live): SKIPPED — chromedriver did not come up");
                return;
            }
            var d = RemoteWebDriver.HeadlessChrome($"http://127.0.0.1:{cdPort}");
            try
            {
                d.Get($"http://127.0.0.1:{webPort}/");
                if (d.Title != "Hello World") Fail("title=" + d.Title);
                if (d.FindElement(By.Id("greeting")).Text != "Hello, World!") Fail("greeting mismatch");
                Console.WriteLine("consumer(live): OK — installed package drove real headless Chrome testing Hello World site");
            }
            finally
            {
                d.Quit();
            }
        }
        finally
        {
            try { cd.Kill(); } catch { /* gone */ }
            webCts.Cancel();
        }
    }

    private static void StartHelloWebSite(int port, System.Threading.CancellationToken ct)
    {
        const string html = "<!doctype html><title>Hello World</title><h1 id=\"greeting\">Hello, World!</h1>";
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var t = new System.Threading.Thread(() =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var ctx = listener.GetContext();
                    byte[] buf = Encoding.UTF8.GetBytes(html);
                    ctx.Response.ContentType = "text/html; charset=utf-8";
                    ctx.Response.OutputStream.Write(buf, 0, buf.Length);
                    ctx.Response.Close();
                }
            }
            catch { }
            finally { try { listener.Stop(); } catch { } }
        }) { IsBackground = true };
        t.Start();
    }

    private static string? Which(string cmd)
    {
        if (cmd == "chromedriver")
        {
            string? envDriver = Environment.GetEnvironmentVariable("SEL_CHROMEDRIVER_BINARY");
            if (!string.IsNullOrEmpty(envDriver) && File.Exists(envDriver)) return envDriver;
            string? resolved = RemoteWebDriver.ResolveDriver("chrome");
            if (!string.IsNullOrEmpty(resolved) && File.Exists(resolved)) return resolved;
        }
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (path == null) return null;
        foreach (string dir in path.Split(':'))
        {
            string full = Path.Combine(dir, cmd);
            if (File.Exists(full)) return full;
        }
        return null;
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static bool WaitUp(int port, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var c = new TcpClient();
                c.Connect(IPAddress.Loopback, port);
                return true;
            }
            catch
            {
                System.Threading.Thread.Sleep(100);
            }
        }
        return false;
    }
}
