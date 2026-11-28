using System;
using System.Net;

namespace KimNotes.Windows.Settings
{
    internal static class RegistrationResultPage
    {
        internal static string Render(Uri website, bool signedIn)
        {
            string title = signedIn ? "小羊便签已登录" : "注册成功";
            string message = signedIn ? "可以关闭此页，回到小羊便签继续使用。"
                : "桌面端未能自动登录。请回到小羊便签，用刚注册的邮箱和密码登录。";
            string action = signedIn ? "" :
                "<a href=\"" + WebUtility.HtmlEncode(new Uri(website, "notes/").AbsoluteUri) + "\" rel=\"noreferrer\">打开网页版便签</a>";
            return $$"""
                <!doctype html>
                <html lang="zh-CN">
                <head>
                <meta charset="utf-8">
                <meta name="viewport" content="width=device-width, initial-scale=1">
                <title>{{title}} · 小羊便签</title>
                <style>
                  :root {
                    color-scheme: light dark;
                    --canvas: #edf3f8;
                    --card: #fff;
                    --line: #d9e4ee;
                    --ink: #203247;
                    --muted: #4c6277;
                    --accent: #245c96;
                    --positive: #20664e;
                    --positive-bg: #e3f4eb;
                  }
                  @media (prefers-color-scheme: dark) {
                    :root {
                      --canvas: #141e2b;
                      --card: #1d2a3a;
                      --line: #35475c;
                      --ink: #f0f5fc;
                      --muted: #bccde0;
                      --accent: #a7cbfc;
                      --positive: #a5dfc8;
                      --positive-bg: #24483f;
                    }
                  }
                  * { box-sizing: border-box; }
                  body {
                    margin: 0;
                    min-height: 100vh;
                    display: grid;
                    place-items: center;
                    padding: 24px;
                    background: var(--canvas);
                    color: var(--ink);
                    font: 16px/1.65 system-ui, "Microsoft YaHei UI", sans-serif;
                  }
                  main {
                    width: min(100%, 27rem);
                    padding: clamp(24px, 6vw, 36px);
                    text-align: center;
                    background: var(--card);
                    border: 1px solid var(--line);
                    border-radius: 20px;
                    box-shadow: 0 16px 44px rgb(16 39 63 / 10%);
                  }
                  .check {
                    display: grid;
                    place-items: center;
                    width: 52px;
                    height: 52px;
                    margin: 0 auto 14px;
                    border-radius: 50%;
                    background: var(--positive-bg);
                    color: var(--positive);
                    font-size: 25px;
                  }
                  h1 { margin: 0; font-size: 1.45rem; line-height: 1.4; }
                  p { margin: 9px 0 0; color: var(--muted); font-size: 0.9375rem; }
                  a { display: inline-block; margin-top: 16px; color: var(--accent); text-underline-offset: 3px; }
                  a:focus-visible { outline: 2px solid currentColor; outline-offset: 4px; }
                </style>
                </head>
                <body>
                  <main aria-labelledby="result-title">
                    <div class="check" aria-hidden="true">✓</div>
                    <h1 id="result-title">{{title}}</h1>
                    <p>{{message}}</p>
                    {{action}}
                  </main>
                </body>
                </html>
                """;
        }
    }
}
