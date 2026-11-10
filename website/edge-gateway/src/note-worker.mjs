export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    // Wrangler may forward a production Host during local development. This
    // flag is supplied only by the npm dev command, never by deployed config.
    const localPreview = env.LOCAL_PREVIEW === 'true';
    const publicHost = new URL(env.PUBLIC_ORIGIN || 'https://note.kimlulu.com').hostname;
    if (!localPreview && (url.hostname !== publicHost || url.protocol !== 'https:')) {
      url.hostname = publicHost;
      url.protocol = 'https:';
      url.port = '';
      return Response.redirect(url.toString(), 301);
    }
    if (url.pathname.startsWith('/api/')) return proxyAPI(request, env, localPreview);
    const response = await env.ASSETS.fetch(request);
    const accountPage = /^\/(?:login|notes)(?:\/|$)/.test(url.pathname);
    const updateManifest = url.pathname === '/update.json' && response.ok;
    const sharedFrontend = /^\/assets\/(?:site-v2\.(?:css|js)|session-client\.js|notes-time\.(?:css|js)|refined\.css)$/.test(url.pathname);
    if (!localPreview && !accountPage && !sharedFrontend && !updateManifest) return response;
    const headers = new Headers(response.headers);
    // Versioned public assets can be reused across visits without a network
    // round trip. Unversioned assets retain the shorter deployment refresh.
    const versionedAsset = sharedFrontend && url.searchParams.has('v');
    headers.set('Cache-Control', localPreview || accountPage ? 'private, no-store' : versionedAsset ? 'public, max-age=3600, must-revalidate' : 'public, max-age=300, must-revalidate');
    if (accountPage) headers.set('X-Robots-Tag', 'noindex, nofollow');
    return new Response(response.body, { status: response.status, statusText: response.statusText, headers });
  },
};

async function proxyAPI(request, env, localPreview) {
  const unavailable = () => Response.json({code:'backend_unavailable',message:'云端服务暂时不可用，请稍后重试。'}, {status:503,headers:{'Cache-Control':'private, no-store'}});
  if (!env.BACKEND_ORIGIN || !env.BACKEND_GATEWAY_SECRET) return unavailable();
  const source = new URL(request.url), target = new URL(env.BACKEND_ORIGIN);
  if (target.protocol !== 'https:' || target.username || target.password || target.pathname !== '/') return unavailable();
  let pathname;
  try { pathname = decodeURIComponent(source.pathname); } catch { return apiError(400,'invalid_path','请求地址无效。'); }
  target.pathname = source.pathname; target.search = source.search;
  const headers = new Headers();
  for (const name of ['content-type','accept','authorization','cookie','origin','x-kim-client','x-kim-auth-version','x-kim-inline-images','x-kim-expected-user','if-none-match']) {
    const value = request.headers.get(name); if (value) headers.set(name,value);
  }
  headers.set('X-Kim-Gateway',env.BACKEND_GATEWAY_SECRET);
  headers.set('X-Kim-Client-IP',request.headers.get('CF-Connecting-IP') || 'unknown');
  if (localPreview) {
    const origin = headers.get('Origin');
    // Wrangler rewrites localhost Origin to the configured route hostname.
    const allowed = /^http:\/\/(?:localhost|127\.0\.0\.1):(?:8787|8790)$/.test(origin || '') || origin === source.origin;
    if (origin && !allowed) return Response.json({message:'请求来源无效。'}, {status:403});
    if (origin) headers.set('Origin','https://note.kimlulu.com');
    const cookie = headers.get('Cookie');
    if (cookie) headers.set('Cookie',cookie.replace(/(?:^|;\s*)kim_preview_session_kimnotes=/g,'; __Host-kim_session_kimnotes=').replace(/^;\s*/,''));
  }
  try {
    let body;
    if (!['GET','HEAD'].includes(request.method)) {
      // A fixed-size body also works with upstream routers that do not accept
      // chunked image uploads. Bound memory even when Content-Length is absent.
      const limit = 10 * 1024 * 1024;
      const reader = request.body?.getReader(); const parts = []; let size = 0;
      if (reader) while (true) {
        const {done,value} = await reader.read(); if (done) break;
        size += value.byteLength;
        if (size > limit) { await reader.cancel(); return Response.json({code:'body_too_large',message:'上传内容需要小于 10 MB。'}, {status:413,headers:{'Cache-Control':'private, no-store'}}); }
        parts.push(value);
      }
      body = new Uint8Array(size); let at = 0;
      for (const part of parts) { body.set(part,at); at += part.byteLength; }
      headers.set('Content-Length',String(size));
    }
    const upstream = await fetch(target, {method:request.method,headers,body,redirect:'manual',signal:AbortSignal.timeout(55000)});
    // Never forward credentials or gateway secrets through an upstream redirect.
    if (upstream.status >= 300 && upstream.status < 400 && upstream.status !== 304) return unavailable();
    const responseHeaders = new Headers();
    for (const name of ['content-type','content-disposition','content-length','etag','retry-after']) {
      // Node fetch decompresses encoded responses but retains their original
      // Content-Length. Do not forward the compressed size for decoded bytes.
      if (name === 'content-length' && upstream.headers.get('content-encoding') && upstream.headers.get('content-encoding').toLowerCase() !== 'identity') continue;
      const value = upstream.headers.get(name); if (value) responseHeaders.set(name,value);
    }
    const cookies = upstream.headers.getSetCookie?.() || [upstream.headers.get('set-cookie')].filter(Boolean);
    for (let value of cookies) {
      if (localPreview) value = value.replace('__Host-kim_session_kimnotes=','kim_preview_session_kimnotes=').replace(/;\s*Secure/ig,'');
      responseHeaders.append('Set-Cookie',value);
    }
    responseHeaders.set('Cache-Control','private, no-store');
    responseHeaders.set('X-Content-Type-Options','nosniff');
    responseHeaders.set('X-Robots-Tag','noindex, nofollow');
    if (upstream.headers.get('content-security-policy')) responseHeaders.set('Content-Security-Policy',upstream.headers.get('content-security-policy'));
    if (request.method === 'GET' && /^\/api\/v1\/apps\/[^/]+\/config$/.test(pathname) && upstream.ok) {
      const config = await upstream.json();
      responseHeaders.delete('Content-Length');
      return Response.json({...config,humanVerification:null,webSessionIdleSeconds:config.webSessionIdleSeconds ?? 1800,webSessionAbsoluteSeconds:config.webSessionAbsoluteSeconds ?? 28800},{headers:responseHeaders});
    }
    return new Response(upstream.body,{status:upstream.status,headers:responseHeaders});
  } catch { return unavailable(); }
}

function apiError(status,code,message) {
  return Response.json({code,message},{status,headers:{'Cache-Control':'private, no-store','X-Content-Type-Options':'nosniff','X-Robots-Tag':'noindex, nofollow'}});
}
