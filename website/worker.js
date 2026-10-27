export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    // Wrangler may forward a production Host during local development. This
    // flag is supplied only by the npm dev command, never by deployed config.
    const localPreview = env.LOCAL_PREVIEW === 'true';
    if (!localPreview && (url.hostname !== 'note.kimlulu.com' || url.protocol !== 'https:')) {
      url.hostname = 'note.kimlulu.com';
      url.protocol = 'https:';
      url.port = '';
      return Response.redirect(url.toString(), 301);
    }
    if (url.pathname.startsWith('/api/')) return proxyAPI(request, env, localPreview);
    const response = await env.ASSETS.fetch(request);
    const accountPage = /^\/(?:login|notes)(?:\/|$)/.test(url.pathname);
    const sharedFrontend = /^\/assets\/(?:site-v2\.(?:css|js)|session-client\.js)$/.test(url.pathname);
    if (!localPreview && !accountPage && !sharedFrontend) return response;
    const headers = new Headers(response.headers);
    headers.set('Cache-Control', localPreview || accountPage ? 'private, no-store' : 'public, max-age=300, must-revalidate');
    if (accountPage) headers.set('X-Robots-Tag', 'noindex, nofollow');
    return new Response(response.body, { status: response.status, statusText: response.statusText, headers });
  },
};

async function proxyAPI(request, env, localPreview) {
  const unavailable = () => Response.json({code:'backend_unavailable',message:'云端服务暂时不可用，请稍后重试。'}, {status:503,headers:{'Cache-Control':'private, no-store'}});
  if (!env.BACKEND_ORIGIN || !env.BACKEND_GATEWAY_SECRET) return unavailable();
  const source = new URL(request.url), target = new URL(env.BACKEND_ORIGIN);
  if (target.protocol !== 'https:' || target.username || target.password || target.pathname !== '/') return unavailable();
  const throughGateway = localPreview && target.origin === 'https://note.kimlulu.com';
  let pathname;
  try { pathname = decodeURIComponent(source.pathname); } catch { return apiError(400,'invalid_path','请求地址无效。'); }
  const authAction = request.method === 'POST' && pathname.match(/^\/api\/v1\/apps\/[^/]+\/auth\/(login|register|reset(?:\/complete)?)$/)?.[1]?.split('/')[0];
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
  if (authAction) {
    if (throughGateway) {
      // A local proxy through the deployed gateway must not spend the token twice.
      const token = request.headers.get('X-Kim-Turnstile');
      if (token) headers.set('X-Kim-Turnstile',token);
    } else {
      const failure = await verifyHuman(request,env,authAction,localPreview);
      if (failure) return failure;
    }
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
    if (request.method === 'GET' && /^\/api\/v1\/apps\/[^/]+\/config$/.test(pathname) && upstream.ok && !throughGateway) {
      if (!turnstileReady(env,localPreview)) return apiError(503,'verification_unavailable','人机验证暂时不可用，请稍后重试。');
      const config = await upstream.json();
      responseHeaders.delete('Content-Length');
      return Response.json({...config,humanVerification:{provider:'turnstile',siteKey:env.TURNSTILE_SITE_KEY},webSessionIdleSeconds:config.webSessionIdleSeconds ?? 1800,webSessionAbsoluteSeconds:config.webSessionAbsoluteSeconds ?? 28800},{headers:responseHeaders});
    }
    return new Response(upstream.body,{status:upstream.status,headers:responseHeaders});
  } catch { return unavailable(); }
}

function apiError(status,code,message) {
  return Response.json({code,message},{status,headers:{'Cache-Control':'private, no-store','X-Content-Type-Options':'nosniff','X-Robots-Tag':'noindex, nofollow'}});
}
function turnstileReady(env,localPreview) {
  return !!(env.TURNSTILE_SITE_KEY && env.TURNSTILE_SECRET_KEY && (localPreview || /^0x/.test(env.TURNSTILE_SITE_KEY) && !/^[123]x0000/.test(env.TURNSTILE_SECRET_KEY)));
}
async function verifyHuman(request,env,action,localPreview) {
  if (!turnstileReady(env,localPreview)) return apiError(503,'verification_unavailable','人机验证暂时不可用，请稍后重试。');
  const token = request.headers.get('X-Kim-Turnstile');
  if (!token || token.length > 2048) return apiError(403,'verification_required','请先完成人机验证。');
  const values = new URLSearchParams({secret:env.TURNSTILE_SECRET_KEY,response:token});
  const ip = request.headers.get('CF-Connecting-IP'); if (ip) values.set('remoteip',ip);
  try {
    const response = await fetch('https://challenges.cloudflare.com/turnstile/v0/siteverify',{method:'POST',headers:{'Content-Type':'application/x-www-form-urlencoded'},body:values.toString(),redirect:'manual',signal:AbortSignal.timeout(10000)});
    if (!response.ok) return apiError(503,'verification_unavailable','人机验证暂时不可用，请稍后重试。');
    const result = await response.json();
    // Official dummy keys are allowed only by the local CLI flag, never in production.
    const dummy = localPreview && env.TURNSTILE_SITE_KEY === '1x00000000000000000000AA' && env.TURNSTILE_SECRET_KEY === '1x0000000000000000000000000000000AA';
    if (result.success !== true || !dummy && (result.hostname !== 'note.kimlulu.com' || result.action !== action)) return apiError(403,'verification_failed','验证已过期或未通过，请重新验证。');
    return null;
  } catch (error) {
    // Do not log tokens, secrets, request bodies or provider response payloads.
    console.warn('turnstile_siteverify_unavailable',error?.name || 'unknown');
    return apiError(503,'verification_unavailable','人机验证连接失败，请稍后重试。');
  }
}
