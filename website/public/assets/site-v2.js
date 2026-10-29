(() => {
  'use strict';
  const $ = (selector, root = document) => root.querySelector(selector);
  const icon = (name) => {
    const image = document.createElement('img');
    image.className = 'icon'; image.src = `/assets/icons/${name}.svg`; image.alt = '';
    return image;
  };
  const text = (tag, content, className) => {
    const element = document.createElement(tag);
    element.textContent = content;
    if (className) element.className = className;
    return element;
  };

  const navToggle = $('[data-nav-toggle]');
  const nav = $('#site-nav');
  const closeNav = () => {
    if (!navToggle || !nav) return;
    nav.classList.remove('is-open');
    navToggle.setAttribute('aria-expanded', 'false');
    navToggle.setAttribute('aria-label', '展开导航');
  };
  navToggle?.addEventListener('click', () => {
    const open = nav.classList.toggle('is-open');
    navToggle.setAttribute('aria-expanded', String(open));
    navToggle.setAttribute('aria-label', open ? '收起导航' : '展开导航');
  });
  nav?.addEventListener('click', (event) => { if (event.target.closest('a')) closeNav(); });
  document.addEventListener('keydown', (event) => { if (event.key === 'Escape') closeNav(); });
  document.querySelectorAll('a[href="#guide"]').forEach(link => link.addEventListener('click',() => {
    const guide = $('#guide.start-guide'); if (guide) guide.open = true;
  }));

  const imageDialog = $('#image-preview');
  const imageViewer = imageDialog ? initializeImageViewer(imageDialog) : null;
  document.addEventListener('click', (event) => {
    const imageButton = event.target.closest('[data-lightbox]');
    if (imageButton && imageViewer) imageViewer.open(imageButton.dataset.lightbox, imageButton.dataset.imageTitle || '查看图片');
    const closeButton = event.target.closest('[data-close-dialog]');
    closeButton?.closest('dialog')?.close();
  });
  document.querySelectorAll('dialog').forEach((dialog) => {
    dialog.addEventListener('click', (event) => { if (event.target === dialog && dialog.id !== 'note-editor') dialog.close(); });
  });

  if (document.body.dataset.page === 'auth') initializeAuth();
  if (document.body.dataset.page === 'notes') initializeNotes();
  if ($('.site-nav .login-link')) initializeAccountEntry();

  function initializeImageViewer(dialog) {
    const image = $('#image-preview-content');
    const stage = $('.dialog-image-wrap', dialog);
    const download = $('[data-image-download]', dialog);
    const zoomLabel = $('[data-image-zoom]', dialog);
    const message = $('[data-image-status]', dialog);
    const buttons = [...dialog.querySelectorAll('[data-image-action]')];
    let zoom = 1;
    let autoFit = true;
    let ready = false;
    const fitScale = () => Math.min(1, Math.max(1, stage.clientWidth - 40) / image.naturalWidth, Math.max(1, stage.clientHeight - 40) / image.naturalHeight);
    const update = (next, center = true) => {
      if (!ready) return;
      const old = zoom;
      const x = (stage.scrollLeft + stage.clientWidth / 2) / old;
      const y = (stage.scrollTop + stage.clientHeight / 2) / old;
      zoom = Math.max(Math.min(0.1, fitScale()), Math.min(8, next));
      image.style.width = `${Math.round(image.naturalWidth * zoom)}px`;
      zoomLabel.textContent = `${Math.round(zoom * 100)}%`;
      if (center) {
        stage.scrollLeft = x * zoom - stage.clientWidth / 2;
        stage.scrollTop = y * zoom - stage.clientHeight / 2;
      }
      buttons.find(b => b.dataset.imageAction === 'out').disabled = zoom <= Math.min(0.1, fitScale()) + 0.001;
      buttons.find(b => b.dataset.imageAction === 'in').disabled = zoom >= 8;
    };
    const fit = () => { autoFit = true; update(fitScale(), false); stage.scrollLeft = stage.scrollTop = 0; };
    const action = (name) => {
      if (!ready) return;
      if (name === 'fit') { fit(); return; }
      autoFit = false;
      update(name === 'actual' ? 1 : zoom * (name === 'in' ? 1.25 : 0.8));
    };
    buttons.forEach(button => button.addEventListener('click', () => action(button.dataset.imageAction)));
    image.addEventListener('load', () => {
      ready = true; image.hidden = false; message.hidden = true; download.hidden = false;
      buttons.forEach(button => { button.disabled = false; }); fit();
    });
    image.addEventListener('error', () => {
      ready = false; image.hidden = true; download.hidden = true;
      buttons.forEach(button => { button.disabled = true; });
      zoomLabel.textContent = '—'; message.textContent = '图片暂时无法加载，请关闭后重试。'; message.hidden = false;
    });
    dialog.addEventListener('keydown', event => {
      if (event.ctrlKey || event.metaKey || event.altKey) return;
      const commands = {'+':'in', '=':'in', '-':'out', '0':'fit', '1':'actual'};
      if (commands[event.key]) { event.preventDefault(); action(commands[event.key]); }
    });
    stage.addEventListener('wheel', event => {
      if (!event.ctrlKey || !ready) return;
      event.preventDefault(); if (event.deltaY) action(event.deltaY < 0 ? 'in' : 'out');
    }, {passive:false});
    window.addEventListener('resize', () => { if (dialog.open && ready && autoFit) fit(); });
    return {open(source, title) {
      const url = new URL(source, location.href);
      if (url.origin !== location.origin || !/^https?:$/.test(url.protocol)) return;
      ready = false; autoFit = true; zoom = 1;
      buttons.forEach(button => { button.disabled = true; });
      image.hidden = true; image.style.width = ''; message.textContent = '正在加载图片…'; message.hidden = false;
      download.hidden = true; download.href = url.href; download.download = url.pathname.split('/').pop() || 'KimNotes-image.png';
      zoomLabel.textContent = '—'; image.alt = title; $('#image-preview-title').textContent = title;
      if (!dialog.open) dialog.showModal();
      image.src = url.href;
    }};
  }

  async function api(path, options = {}) {
    const headers = new Headers(options.headers || {});
    headers.set('X-Kim-Auth-Version','2');
    if (options.body && !(options.body instanceof Blob)) headers.set('Content-Type','application/json');
    let response;
    try { response = await fetch(`/api/v1/apps/kimnotes${path}`,{...options,headers,credentials:'same-origin',cache:'no-store',signal:options.signal || AbortSignal.timeout(60000)}); }
    catch { throw new Error('连接失败，请检查网络后重试。'); }
    const data = await response.json().catch(() => ({}));
    if (!response.ok) { const error = new Error(data.message || '操作未完成，请稍后重试。'); error.status = response.status; error.code = data.code; throw error; }
    return data;
  }

  function initializeAccountEntry() {
    const link = $('.site-nav .login-link');
    let checking = false;
    const restore = async () => {
      if (checking) return;
      checking = true;
      try { await api('/auth/me'); link.textContent = '我的便签'; link.href = '/notes/'; }
      catch (error) { if (error.status === 401) { link.textContent = '登录'; link.href = '/login/'; } }
      finally { checking = false; }
    };
    const channel = new BroadcastChannel('kimnotes-session');
    channel.addEventListener('message',restore);
    window.addEventListener('pageshow',restore);
    document.addEventListener('visibilitychange', () => { if (!document.hidden) restore(); });
    restore();
  }

  let turnstileScript;
  function loadTurnstile() {
    if (window.turnstile) return Promise.resolve(window.turnstile);
    if (!turnstileScript) turnstileScript = new Promise((resolve,reject) => {
      const script = document.createElement('script');
      const timer = setTimeout(() => failed(),20000);
      const failed = () => { clearTimeout(timer); script.remove(); turnstileScript = null; reject(new Error('人机验证加载失败，请检查网络后重试。')); };
      script.src = 'https://challenges.cloudflare.com/turnstile/v0/api.js?render=explicit';
      script.async = true;
      script.onload = () => { clearTimeout(timer); if (window.turnstile) resolve(window.turnstile); else failed(); };
      script.onerror = failed;
      document.head.append(script);
    });
    return turnstileScript;
  }

  function initializeAuth() {
    const form = $('#auth-form');
    const password = $('#auth-password');
    const confirm = $('#auth-confirm');
    const status = $('#auth-status');
    const toggle = $('[data-toggle-password]');
    const modes = new Set(['login', 'register', 'reset']);
    let mode = 'login', challengeId = '', submitting = false, serviceConfig = null;
    let checkingSession = false, authReady = false, captchaToken = '', widgetId = null, widgetRevision = 0;
    const needsHuman = () => !challengeId && !!serviceConfig?.humanVerification;
    const updateSubmit = () => { $('#auth-submit').disabled = submitting || !authReady || needsHuman() && !captchaToken; };
    const renderVerification = async () => {
      const revision = ++widgetRevision;
      captchaToken = '';
      if (widgetId !== null && window.turnstile) window.turnstile.remove(widgetId);
      widgetId = null;
      const block = $('#human-verification'), message = $('#verification-message'), retry = $('#verification-retry');
      $('#turnstile-widget').replaceChildren();
      block.hidden = !needsHuman(); updateSubmit();
      if (block.hidden) return;
      message.textContent = '正在加载人机验证…'; message.hidden = false; retry.hidden = true;
      const fail = (content) => {
        if (revision !== widgetRevision) return;
        captchaToken = ''; message.textContent = content; message.hidden = false; retry.hidden = false; updateSubmit();
      };
      try {
        const turnstile = await loadTurnstile();
        if (revision !== widgetRevision) return;
        widgetId = turnstile.render('#turnstile-widget',{
          sitekey:serviceConfig.humanVerification.siteKey,theme:'light',language:'zh-cn',
          size:block.clientWidth < 300 ? 'compact' : 'flexible',action:mode,
          'response-field':false,
          callback:token => { if (revision !== widgetRevision) return; captchaToken = token; message.hidden = true; retry.hidden = true; updateSubmit(); },
          'error-callback':() => fail('人机验证暂时不可用，请重新加载。'),
          'expired-callback':() => fail('验证已过期，请重新验证。'),
          'timeout-callback':() => fail('验证超时，请重新验证。'),
        });
        message.hidden = true;
      } catch (error) { fail(error.message); }
    };
    $('#verification-retry').addEventListener('click',renderVerification);
    let resetToken = null;
    if (new URL(location.href).searchParams.get('mode') === 'reset' && location.hash) {
      const parts = location.hash.slice(1).split(':');
      if (/^[0-9a-f-]{36}$/.test(parts[0]) && /^[A-Za-z0-9_-]{43}$/.test(parts[1])) resetToken = parts;
      history.replaceState(null,'',location.pathname + location.search);
    }
    const switchMode = (next) => {
      mode = modes.has(next) ? next : 'login';
      challengeId = '';
      $('#auth-email').disabled = false;
      $('#code-field').hidden = true; $('#auth-code').disabled = true; $('#auth-code').required = false;
      const reset = mode === 'reset';
      const register = mode === 'register';
      const setPassword = reset && resetToken;
      const title = setPassword ? '设置新密码' : reset ? '找回密码' : register ? '注册小羊账号' : '登录小羊便签';
      $('#auth-title').textContent = title;
      document.title = `${title} · 小羊便签 KimNotes`;
      $('#auth-subtitle').textContent = reset ? '通过注册邮箱重置密码。' : register ? '一个小羊账号，使用小羊的不同应用。' : '登录后，查看你已同步的便签。';
      $('#password-field').hidden = reset && !setPassword;
      password.disabled = reset && !setPassword;
      password.required = !reset || !!setPassword;
      password.minLength = register || setPassword ? 8 : 1;
      password.autocomplete = register || setPassword ? 'new-password' : 'current-password';
      password.placeholder = register || setPassword ? '设置密码，至少 8 位' : '输入密码';
      password.value = '';
      password.type = 'password';
      toggle.setAttribute('aria-label', '显示密码');
      toggle.setAttribute('aria-pressed', 'false');
      toggle.firstElementChild.src = '/assets/icons/visibility.svg';
      $('#confirm-field').hidden = !(register || setPassword);
      confirm.disabled = !(register || setPassword);
      confirm.required = !!(register || setPassword);
      confirm.value = '';
      confirm.setCustomValidity('');
      $('#auth-options').hidden = register || reset || serviceConfig?.passwordReset === false;
      $('#auth-submit').textContent = setPassword ? '保存新密码' : reset ? '发送重置邮件' : register ? '注册账号' : '登录';
      $('#auth-email').closest('.field').hidden = !!setPassword;
      $('#auth-email').disabled = !!setPassword; $('#auth-email').required = !setPassword;
      const switchText = register ? '已经有账号？' : reset ? '想起密码了？' : '还没有账号？';
      const switchLink = document.createElement('a');
      const target = register || reset ? 'login' : 'register';
      switchLink.href = target === 'login' ? '/login/' : '/login/?mode=register';
      switchLink.dataset.authMode = target;
      switchLink.textContent = target === 'login' ? '返回登录' : '注册账号';
      $('#auth-switch').replaceChildren(document.createTextNode(switchText), switchLink);
      status.hidden = true;
      if (authReady) renderVerification();
    };
    document.addEventListener('click', (event) => {
      const link = event.target.closest('[data-auth-mode]');
      if (!link) return;
      event.preventDefault();
      if (submitting || !authReady) return;
      resetToken = null;
      switchMode(link.dataset.authMode);
      const url = new URL(location.href);
      if (mode === 'login') url.searchParams.delete('mode'); else url.searchParams.set('mode', mode);
      history.replaceState(null, '', url);
      $('#auth-email').focus();
    });
    toggle.addEventListener('click', () => {
      const visible = password.type === 'password';
      password.type = visible ? 'text' : 'password';
      toggle.setAttribute('aria-pressed', String(visible));
      toggle.setAttribute('aria-label', visible ? '隐藏密码' : '显示密码');
      toggle.firstElementChild.src = `/assets/icons/${visible ? 'visibility_off' : 'visibility'}.svg`;
    });
    form.addEventListener('input', () => { confirm.setCustomValidity(''); status.hidden = true; });
    form.addEventListener('submit', async (event) => {
      event.preventDefault();
      if (submitting || !authReady) return;
      if ((mode === 'register' && !challengeId || mode === 'reset' && resetToken) && confirm.value !== password.value) {
        confirm.setCustomValidity('两次输入的密码不一致。');
        status.textContent = '两次输入的密码不一致，请重新确认。';
        status.classList.add('error');
        status.hidden = false;
        confirm.reportValidity();
        return;
      }
      if (!form.reportValidity()) return;
      if (needsHuman() && !captchaToken) {
        status.textContent = '请先完成人机验证。'; status.classList.add('error'); status.hidden = false; return;
      }
      submitting = true; const submit = $('#auth-submit'), label = submit.textContent;
      submit.disabled = true; submit.textContent = '请稍候…';
      status.classList.remove('error');
      status.textContent = ''; status.hidden = true;
      try {
        const email = $('#auth-email').value.trim();
        const path = challengeId ? '/auth/verify' : mode === 'reset' ? resetToken ? '/auth/reset/complete' : '/auth/reset' : `/auth/${mode}`;
        const payload = challengeId ? {challengeId,code:$('#auth-code').value} : mode === 'reset' ? resetToken ? {challengeId:resetToken[0],token:resetToken[1],password:password.value} : {email} : {email,password:password.value};
        const data = await api(path,{method:'POST',body:JSON.stringify(payload),headers:needsHuman() ? {'X-Kim-Turnstile':captchaToken} : {}});
        if (data.verificationRequired) {
          challengeId = data.challengeId;
          $('#auth-email').disabled = true;
          password.value = ''; confirm.value = ''; password.disabled = true; confirm.disabled = true;
          $('#password-field').hidden = true; $('#confirm-field').hidden = true;
          $('#code-field').hidden = false; $('#auth-code').disabled = false; $('#auth-code').required = true;
          status.textContent = data.message; submit.textContent = '验证并登录'; $('#auth-code').focus();
        } else if (mode === 'reset') {
          const complete = !!resetToken; resetToken = null;
          if (complete) switchMode('login');
          status.textContent = data.message;
        } else {
          const channel = new BroadcastChannel('kimnotes-session'); channel.postMessage('changed'); channel.close();
          password.value = ''; confirm.value = ''; location.assign('/notes/'); return;
        }
      } catch (error) { status.textContent = error.message; status.classList.add('error'); }
      finally { submitting = false; if (submit.textContent === '请稍候…') submit.textContent = label; status.hidden = false; renderVerification(); }
    });
    switchMode(new URL(location.href).searchParams.get('mode'));
    const restoreSession = async () => {
      if (checkingSession || submitting) return;
      checkingSession = true; authReady = false; updateSubmit();
      $('#auth-content').hidden = true; $('#auth-session-check').hidden = false;
      $('#auth-session-message').textContent = '正在检查登录状态…'; $('#auth-session-retry').hidden = true;
      try {
        if (!resetToken) {
          try { await api('/auth/me'); location.replace('/notes/'); return; }
          catch (error) { if (error.status !== 401) throw error; }
        }
        serviceConfig = await api('/config');
        if (!serviceConfig.humanVerification?.siteKey) throw new Error('人机验证暂时不可用，请稍后重试。');
        if (!serviceConfig.passwordReset) $('#auth-options').hidden = true;
        $('#auth-preview-note').textContent = `${serviceConfig.emailVerification ? '注册时需要验证邮箱。' : ''}30 分钟无操作或登录满 8 小时后需重新登录。公共电脑使用后请退出账号。`;
        if (mode === 'reset' && !resetToken && !serviceConfig.passwordReset) { status.textContent = '找回密码邮件服务暂未配置，请稍后再试。'; status.hidden = false; }
        authReady = true; $('#auth-session-check').hidden = true; $('#auth-content').hidden = false;
        renderVerification();
      } catch (error) { $('#auth-session-message').textContent = error.message; $('#auth-session-retry').hidden = false; }
      finally { checkingSession = false; }
    };
    $('#auth-session-retry').addEventListener('click',restoreSession);
    window.addEventListener('pageshow',event => { if (event.persisted) restoreSession(); });
    const channel = new BroadcastChannel('kimnotes-session'); channel.addEventListener('message',restoreSession);
    restoreSession();
  }

  async function initializeNotes() {
    const demo = new URL(location.href).searchParams.get('demo') === '1';
    let account;
    if (!demo) {
      try { account = await api('/auth/me'); }
      catch (error) { if (error.status !== 401) $('.notes-gate p').textContent = error.message; return; }
    }
    async function cloudAPI(path,options = {}) {
      const headers = new Headers(options.headers || {});
      if (account?.user?.id) headers.set('X-Kim-Expected-User',account.user.id);
      try {
        const result = await api(path,{...options,headers});
        if (expired) throw Object.assign(new Error('登录已过期，请重新登录。'),{status:401});
        return result;
      }
      catch (error) {
        if (error.code === 'account_changed') { expireSession(); location.reload(); }
        else if (error.status === 401) expireSession();
        throw error;
      }
    }
    $('#notes-gate').hidden = true;
    $('#notes-workspace').hidden = false;
    $('#demo-account').hidden = false;
    $('#notes-login-link').hidden = true;
    $('.demo-notice').hidden = false;
    $('#demo-create').hidden = !demo;
    document.title = demo ? '我的便签 · 示例预览 · 小羊便签' : '我的便签 · 小羊便签';
    if (!demo) {
      $('#demo-account summary span').textContent = '我的账号';
      const panel = $('#demo-account .account-panel'); panel.replaceChildren(text('p',account.user.email));
      const passwordButton = text('button','修改密码'); passwordButton.type = 'button';
      passwordButton.addEventListener('click', () => $('#password-dialog').showModal());
      const logout = text('button','退出登录'); logout.type = 'button';
      logout.addEventListener('click',async () => {
        if (!confirm('退出后，这个浏览器将不再显示你的便签。')) return;
        logout.disabled = true;
        try { preserveDraft(); await cloudAPI('/auth/logout',{method:'POST',body:'{}'}); const channel = new BroadcastChannel('kimnotes-session'); channel.postMessage('changed'); channel.close(); expireSession(); location.replace('/login/'); }
        catch (error) { announce(error.message); logout.disabled = false; }
      });
      const devices = text('button','登录设备'); devices.type = 'button'; devices.addEventListener('click',showDevices);
      panel.append(devices,passwordButton,logout);
      $('.demo-notice').textContent = '正在连接云端…';
      $('#cloud-tools').hidden = false;
    }
    // These are public synthetic fixtures, never an authentication fallback.
    // Real notes must come from an authenticated, app/user-scoped server API.
    const notes = demo ? [
      {id:'daily',title:'今天的小计划',date:'2026-10-29',time:'10:32',createdAt:'2026-10-20T09:15:00+08:00',theme:'#d6e9df',historyDate:'2026-10-20 17:15',blocks:[
        {type:'p',text:'把零散的想法记下来，留一点空间给今天。'},
        {type:'h3',text:'今天要做的事'},
        {type:'checklist',items:[{text:'整理本周的项目进度',done:true},{text:'记录会议中的新想法',done:false},{text:'把截图和资料放在一起',done:false}]},
        {type:'p',text:'灵感随手记，重要的事慢慢完成。'},
        {type:'image',src:'/assets/v1.4.11/note-mint-2x.png',width:672,height:896,caption:'项目计划 · 桌面便签'}
      ],previous:[{type:'p',text:'整理本周的项目进度，记录会议中的新想法。'},{type:'p',text:'明天把需要用到的截图收集到一起。'}]},
      {id:'capture',title:'截图贴图小技巧',date:'2026-10-28',time:'09:18',createdAt:'2026-10-20T11:30:00+08:00',theme:'#d7e8fa',historyDate:'2026-10-20 11:30',blocks:[
        {type:'p',text:'框选完成后，可以继续拖动选区，或拉动边角调整范围。'},
        {type:'list',items:['工具栏提供矩形、箭头、马赛克和文字标注。','绿色对勾会复制图片，并保留置顶贴图。','插入便签会追加到最近打开且仍未关闭的便签底部。']},
        {type:'image',src:'/assets/v1.4.11/screenshot-selection-2x.png',width:1000,height:720,caption:'v1.4.11 截图标注界面'},
        {type:'p',text:'快捷键：Ctrl+F2 唤回最近贴图，Ctrl+← 上一张，Ctrl+→ 下一张。'},
        {type:'p',text:'关闭贴图保留历史；唤回后可继续查看上一张、下一张。'}
      ],previous:[{type:'p',text:'截图后可以标注重点，也可以直接贴在桌面上参考。'}]},
      {id:'weekly',title:'每周工作清单',date:'2026-10-27',time:'16:15',createdAt:'2026-10-19T09:00:00+08:00',theme:'#f5edc7',historyDate:'2026-10-19 09:00',blocks:[
        {type:'p',text:'把每周固定要做的事整理在一起。'},
        {type:'h3',text:'本周重点'},
        {type:'checklist',items:[{text:'汇总用户反馈',done:true},{text:'核对使用说明与实际功能',done:false},{text:'准备下次版本更新',done:false}]},
        {type:'p',text:'需要提醒的事项，在桌面端转为待办，按具体时间、每天、工作日或每周安排。'}
      ],previous:[{type:'p',text:'汇总用户反馈，核对使用说明。'}]},
      {id:'travel',title:'旅行清单',date:'2026-10-26',time:'20:40',createdAt:'2026-10-18T18:20:00+08:00',theme:'#f1dce8',historyDate:'2026-10-18 18:20',blocks:[
        {type:'p',text:'出发前再检查一遍，轻装出门。'},
        {type:'list',items:['身份证、手机、充电器','轻便外套与雨具','提前下载路线与车票','相机与备用存储卡']},
        {type:'p',text:'行程留一些空白，遇到喜欢的地方就多走一会儿。'}
      ],previous:[{type:'p',text:'身份证、手机、充电器、轻便外套。'}]},
      {id:'ideas',title:'一些灵感',date:'2026-10-24',time:'14:05',createdAt:'2026-10-17T12:30:00+08:00',theme:'#e6def3',historyDate:'2026-10-17 12:30',blocks:[
        {type:'p',text:'好的工具，让人把注意力放回正在做的事。'},
        {type:'p',text:'减少需要记住的步骤，让常用操作出现在顺手的位置。'},
        {type:'h3',text:'记下来，之后再想'},
        {type:'list',items:['用真实例子解释功能','默认设置尽量简单','让文字和图片待在同一张便签里']}
      ],previous:[{type:'p',text:'减少步骤，让记录更简单。'}]},
      {id:'reading',title:'读书摘录',date:'2026-10-23',time:'21:10',createdAt:'2026-10-16T20:25:00+08:00',theme:'#e4e8ec',historyDate:'2026-10-16 20:25',blocks:[
        {type:'p',text:'读到一段喜欢的文字，先记下来，再慢慢体会。'},
        {type:'p',text:'今天的阅读笔记：从一件可以完成的小事开始，比等待一整段空闲时间更容易。'},
        {type:'p',text:'下次回看时，补上自己的想法和例子。'}
      ],previous:[{type:'p',text:'从一件可以完成的小事开始。'}]}
    ] : [];
    const search = $('#note-search');
    let view = 'all';
    const drawer = $('#reader-panel');
    const list = $('#note-list');
    const reader = $('#note-reader');
    const layout = $('#notes-layout');
    const noteTime = window.KimNotesTime;
    let timeGroup = 'month';
    try { const saved = localStorage.getItem('kimnotes-time-group'); if (noteTime.modes.includes(saved)) timeGroup = saved; } catch {}
    const foldedGroups = new Set();
    let selected = null, cursor = '0', syncing = false, loaded = demo, editingID = null, editingBase = 0, editingDocument = null, pendingOperation = null, saving = false, uploading = false, expired = false;
    const draftStore = demo ? null : window.KimSession.drafts({setItem:(...args) => sessionStorage.setItem(...args),getItem:(...args) => sessionStorage.getItem(...args),removeItem:(...args) => sessionStorage.removeItem(...args)});
    const themes = ['#eaf0f7','#fdf7d1','#ddf0e3','#fbe3ee','#e9e2f8','#d9ecfb','#f6f5f1','#fcebdb'];
    function fromCloud(note) {
      const date = noteTime.modified(note), document = note.document;
      return {id:note.id,title:document.title || '未命名便签',date:date?.toLocaleDateString('sv-SE') || '',time:date?.toLocaleTimeString('zh-CN',{hour:'2-digit',minute:'2-digit'}) || '',theme:themes[document.themeId] || themes[0],raw:note,blocks:[...(document.text || '').split(/\n\s*\n/).map(value => ({type:'p',text:value})),...(document.attachments || []).map(id => ({type:'image',src:`/api/v1/apps/kimnotes/attachments/${id}`,caption:'便签图片'}))]};
    }
    const plainText = (note) => note.blocks.map((block) => block.text || (block.items || []).map((item) => typeof item === 'string' ? item : item.text).join(' ') || block.caption || '').join(' ');
    const getNote = () => notes.find((note) => note.id === selected);
    let noticeTimer;
    function announce(message) {
      const status = $('#notes-status');
      clearTimeout(noticeTimer); status.textContent = message; status.hidden = false;
      noticeTimer = setTimeout(() => { status.hidden = true; }, 4500);
    }
    const normalized = value => value.normalize('NFKC').toLocaleLowerCase();
    function normalizedText(value) {
      const parts = typeof Intl.Segmenter === 'function'
        ? new Intl.Segmenter('zh-CN', {granularity:'grapheme'}).segment(value)
        : (function* () { let index = 0; for (const segment of value) { yield {segment,index}; index += segment.length; } })();
      let folded = ''; const starts = [], ends = [];
      for (const part of parts) {
        const segment = normalized(part.segment);
        folded += segment;
        for (let i = 0; i < segment.length; i++) { starts.push(part.index); ends.push(part.index + part.segment.length); }
      }
      return {folded,starts,ends};
    }
    function highlight(root) {
      const query = normalized(search.value.trim()); if (!query) return;
      const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
      const nodes = []; while (walker.nextNode()) nodes.push(walker.currentNode);
      for (const node of nodes) {
        const value = node.nodeValue, map = normalizedText(value);
        let at = map.folded.indexOf(query); if (at < 0) continue;
        const fragment = document.createDocumentFragment(); let last = 0;
        while (at >= 0) {
          const start = map.starts[at], end = map.ends[at + query.length - 1];
          if (start >= last) { fragment.append(document.createTextNode(value.slice(last,start)), text('mark',value.slice(start,end))); last = end; }
          at = map.folded.indexOf(query, at + query.length);
        }
        fragment.append(document.createTextNode(value.slice(last))); node.replaceWith(fragment);
      }
    }
    function snippet(note) {
      const value = plainText(note), query = normalized(search.value.trim());
      const map = normalizedText(value), at = query ? map.folded.indexOf(query) : -1;
      const start = at >= 0 ? Math.max(0,map.starts[at] - 8) : 0;
      return `${start ? '…' : ''}${value.slice(start,start + 130)}${value.length > start + 130 ? '…' : ''}`;
    }
    function revealMatch() {
      const panel = $('#reader-panel'), match = $('.note-body mark', reader) || $('.reader-title mark', reader);
      panel.scrollTop = 0;
      if (match && panel.getClientRects().length) {
        const hit = match.getBoundingClientRect(), bounds = panel.getBoundingClientRect();
        if (hit.bottom > bounds.bottom - 24) panel.scrollTop = Math.max(0, hit.top - bounds.top - 48);
      }
    }
    async function copyNote(button) {
      const note = getNote(); if (!note) return;
      const contents = [note.title,...note.blocks.filter(block => block.type !== 'image').map(block => block.text || (block.items || []).map(item => typeof item === 'string' ? `• ${item}` : `${item.done ? '[x]' : '[ ]'} ${item.text}`).join('\n'))].join('\n\n');
      button.disabled = true;
      try {
        if (!navigator.clipboard?.writeText) throw new Error('clipboard unavailable');
        await navigator.clipboard.writeText(contents); announce('已复制便签正文');
      } catch {
        const dialog = $('#copy-preview'), field = $('#copy-content');
        field.value = contents; dialog.showModal(); field.focus(); field.select();
      } finally { if (button.isConnected) button.disabled = false; }
    }

    const privateImages = new Map();
    const imageObserver = typeof IntersectionObserver === 'function' ? new IntersectionObserver(entries => { for (const entry of entries) if (entry.isIntersecting) { imageObserver.unobserve(entry.target); loadImage(entry.target); } },{rootMargin:'160px'}) : null;
    function attachImage(image,block) {
      image.alt = block.caption || '便签图片'; image.loading = 'lazy'; image.decoding = 'async';
      if (!block.src.startsWith('/api/')) { image.src = block.src; return; }
      image.dataset.privateImage = block.src;
      privateImages.set(image,{controller:new AbortController(),url:null});
      if (imageObserver) imageObserver.observe(image); else loadImage(image);
    }
    async function loadImage(image) {
      const state = privateImages.get(image); if (!state || state.loading) return;
      state.loading = true;
      try {
        const response = await fetch(image.dataset.privateImage,{headers:{'X-Kim-Expected-User':account.user.id},credentials:'same-origin',cache:'no-store',signal:state.controller.signal});
        if (response.status === 401) { expireSession(); return; }
        if (response.status === 409) { expireSession(); location.reload(); return; }
        if (!response.ok) throw new Error('图片加载失败');
        const blob = await response.blob();
        if (!privateImages.has(image)) return;
        state.url = URL.createObjectURL(blob); image.src = state.url;
      } catch (error) { if (error.name !== 'AbortError') { image.alt = '图片暂时无法加载，点击重试'; image.closest('button')?.setAttribute('title','点击查看原图'); } }
    }
    function releaseImages(root) {
      for (const [image,state] of privateImages) if (root.contains(image)) {
        imageObserver?.unobserve(image); state.controller.abort(); if (state.url) URL.revokeObjectURL(state.url); privateImages.delete(image);
      }
    }
    function renderBlocks(blocks, root) {
      const content = text('div', '', 'note-body');
      for (const block of blocks) {
        if (block.type === 'p' || block.type === 'h3') content.append(text(block.type, block.text));
        if (block.type === 'list' || block.type === 'checklist') {
          const ul = document.createElement('ul');
          if (block.type === 'checklist') ul.className = 'checklist';
          for (const item of block.items) {
            const li = document.createElement('li');
            if (typeof item === 'string') li.textContent = item;
            else {
              const checkbox = icon(item.done ? 'check_box' : 'check_box_outline_blank');
              checkbox.alt = item.done ? '已完成' : '未完成';
              li.append(checkbox, text('span', item.text));
            }
            ul.append(li);
          }
          content.append(ul);
        }
        if (block.type === 'image') {
          const figure = document.createElement('figure'); figure.className = 'note-attachment';
          const button = document.createElement('button'); button.type = 'button';
          button.dataset.lightbox = block.src; button.dataset.imageTitle = block.caption;
          button.setAttribute('aria-label', '查看便签中的图片');
          const image = document.createElement('img'); attachImage(image,block);
          if (block.width && block.height) { image.width = block.width; image.height = block.height; }
          button.append(image); figure.append(button, text('figcaption', block.caption)); content.append(figure);
        }
      }
      root.append(content);
    }
    function renderReader() {
      releaseImages(reader); reader.replaceChildren();
      const note = getNote();
      if (!note) {
        const empty = text('div', '', 'empty-state');
        if (!loaded) { empty.append(text('p','正在读取你的便签…')); reader.append(empty); return; }
        empty.append(icon('search'), text('h2', notes.length ? '没有匹配的便签' : '写下第一张便签'), text('p', notes.length ? '试试其他关键词，或清除筛选。' : '新建一张便签，内容会保存在你的账号中。'));
        reader.append(empty); return;
      }
      drawer.style.setProperty('--note-theme',note.theme);
      const actions = text('div', '', 'reader-actions');
      const copyButton = document.createElement('button'); copyButton.type = 'button'; copyButton.className = 'copy-note-button';
      copyButton.append(icon('content_copy'), document.createTextNode('复制正文'));
      copyButton.addEventListener('click', () => copyNote(copyButton));
      const controls = text('div', '', 'reader-action-buttons'); controls.append(copyButton);
      if (!demo) {
        const edit = text('button','编辑'); edit.type = 'button'; edit.addEventListener('click', () => openEditor(note));
        const remove = text('button',note.raw.deletedAt ? '恢复' : '删除'); remove.type = 'button';
        remove.addEventListener('click', () => changeNote(note,note.raw.deletedAt ? 'restore' : 'delete',remove));
        if (!note.raw.deletedAt) controls.append(edit); controls.append(remove);
      }
      actions.append(text('span', demo ? '只读' : note.raw.conflictOf ? '冲突副本' : note.raw.deletedAt ? '最近删除' : '云端便签', 'reader-badge'), controls);
      if (!demo && (note.raw.document.rtf || note.raw.document.rtfGzip)) actions.append(text('span','含桌面富文本 · 请在桌面端编辑','richtext-notice'));
      $('#reader-heading').textContent = note.title;
      const timestamps = text('div','','reader-time');
      for (const [label,date] of [['创建于',noteTime.created(note)],['最近编辑',noteTime.modified(note)]]) {
        const value = text('time',`${label} ${noteTime.absolute(date)}`);
        if (date) value.dateTime = date.toISOString();
        timestamps.append(value);
      }
      reader.append(actions, text('h2', note.title, 'reader-title'), timestamps);
      renderBlocks(note.blocks, reader);
      highlight($('.reader-title', reader)); highlight($('.note-body', reader));
    }
    function selectNote(id) {
      selected = id;
      list.querySelectorAll('[data-note-id]').forEach((button) => button.setAttribute('aria-pressed', String(button.dataset.noteId === id)));
      renderReader();
      layout.dataset.reading = 'true';
      if (!drawer.open) drawer.showModal();
      revealMatch();
      $('#reader-back').focus({preventScroll:true});
    }
    function renderList() {
      const query = search.value.trim().normalize('NFKC').toLocaleLowerCase();
      const visible = notes.filter((note) => {
        if (!demo && !!note.raw.deletedAt !== (view === 'deleted')) return false;
        if (demo && view === 'deleted') return false;
        const matches = `${note.title} ${plainText(note)}`.normalize('NFKC').toLocaleLowerCase().includes(query);
        return matches && (view !== 'images' || note.blocks.some((block) => block.type === 'image'));
      });
      visible.sort((a,b) => noteTime.compare(a,b,view === 'deleted'));
      $('#notes-time-label').textContent = query ? '搜索结果' : view === 'deleted' ? '按删除时间分组' : '最近内容更新';
      $('#note-time-group').value = timeGroup;
      $('#clear-search').hidden = !search.value;
      $('#notes-count').textContent = `${visible.length} 张`;
      releaseImages(list); list.replaceChildren();
      for (const name of ['all','images','deleted']) {
        const count = notes.filter(note => (demo ? name !== 'deleted' : !!note.raw.deletedAt === (name === 'deleted')) && (name !== 'images' || note.blocks.some(block => block.type === 'image'))).length;
        $(`[data-count=${name}]`).textContent = count || '';
      }
      if (!loaded) { list.append(text('p','正在读取便签…','empty-state')); renderReader(); return; }
      if (selected && !visible.some((note) => note.id === selected)) { selected = null; if (drawer.open) drawer.close(); }
      if (!visible.length) {
        const empty = text('div', '', 'empty-state');
        const clear = text('button', '清除搜索与筛选'); clear.type = 'button';
        clear.addEventListener('click', clearFilters);
        empty.append(icon('search'), text('h2', search.value || view === 'images' ? '没有找到便签' : view === 'deleted' ? '最近删除为空' : '这里还没有便签'), text('p', demo || search.value ? '换个关键词再试试。' : view === 'deleted' ? '删除的便签会在这里保留 30 天供你恢复。' : '点击「新建便签」开始记录。'));
        if (search.value || view !== 'all') empty.append(clear);
        list.append(empty);
      }
      function card(note) {
        const button = document.createElement('button'); button.type = 'button';
        button.className = 'note-item'; button.dataset.noteId = note.id;
        button.setAttribute('aria-pressed', String(note.id === selected));
        button.style.setProperty('--note-theme',note.theme);
        button.setAttribute('aria-label',`打开便签：${note.title}`);
        const modified = noteTime.modified(note,view === 'deleted');
        const date = text('time', noteTime.label(note,new Date(),view === 'deleted'));
        if (modified && view !== 'deleted') date.textContent = date.textContent.replace(/更新$/,'') + ' ' + modified.toLocaleTimeString('zh-CN',{hour:'2-digit',minute:'2-digit',hour12:false});
        if (modified) date.dateTime = modified.toISOString();
        date.title = noteTime.absolute(modified);
        button.append(text('h2', note.title), text('p', snippet(note)));
        const images = note.blocks.filter(block => block.type === 'image');
        if (images.length) {
          button.classList.add('has-image');
          const preview = text('div','','note-card-image'), image = document.createElement('img'); attachImage(image,images[0]); preview.append(image); button.append(preview);
        }
        const meta = text('div','','card-meta'); meta.append(date);
        if (images.length) { const count = text('span',String(images.length),'card-image-count'); count.prepend(icon('image')); meta.append(count); }
        button.append(meta);
        highlight(button);
        button.addEventListener('click', () => selectNote(note.id)); return button;
      }
      if (query) {
        for (const note of visible) list.append(card(note));
      } else {
        for (const group of noteTime.groups(visible,timeGroup,view === 'deleted')) {
          const section = text('section','','note-time-group'); section.dataset.groupKey = group.key;
          const heading = text('h2','','note-group-heading'), toggle = text('button','','note-group-toggle'); toggle.type = 'button';
          const grid = text('div','','note-group-grid'); grid.id = `note-group-${timeGroup}-${group.key}`;
          const expanded = !foldedGroups.has(`${timeGroup}:${group.key}`);
          toggle.setAttribute('aria-controls',grid.id); toggle.setAttribute('aria-expanded',String(expanded));
          const caption = noteTime.heading(group,timeGroup);
          toggle.title = group.label;
          toggle.setAttribute('aria-label',`${group.label}，${group.notes.length} 张便签`);
          toggle.append(text('span',caption.title),text('span',caption.detail,'note-group-detail'),icon('expand_more'));
          grid.hidden = !expanded;
          // Hidden groups don't decode private thumbnails until they are opened.
          const fill = () => { if (!grid.childElementCount) for (const note of group.notes) grid.append(card(note)); };
          if (expanded) fill();
          toggle.addEventListener('click',() => {
            const show = toggle.getAttribute('aria-expanded') !== 'true';
            toggle.setAttribute('aria-expanded',String(show)); grid.hidden = !show;
            if (show) { foldedGroups.delete(`${timeGroup}:${group.key}`); fill(); }
            else { foldedGroups.add(`${timeGroup}:${group.key}`); releaseImages(grid); grid.replaceChildren(); }
          });
          heading.append(toggle); section.append(heading,grid); list.append(section);
        }
      }
      renderReader();
      revealMatch();
    }
    function clearFilters() {
      search.value = ''; setView('all'); search.focus();
    }
    search.addEventListener('input', (event) => { if (!event.isComposing) renderList(); });
    search.addEventListener('compositionend', renderList);
    $('#note-time-group').addEventListener('change',event => {
      if (!noteTime.modes.includes(event.target.value)) return;
      timeGroup = event.target.value;
      try { localStorage.setItem('kimnotes-time-group',timeGroup); } catch {}
      renderList();
    });
    document.querySelectorAll('[data-notes-view]').forEach(button => button.addEventListener('click',() => setView(button.dataset.notesView)));
    function setView(next) {
      view = next;
      document.querySelectorAll('[data-notes-view]').forEach(button => { if (button.dataset.notesView === view) button.setAttribute('aria-current','page'); else button.removeAttribute('aria-current'); });
      $('#notes-title').firstChild.textContent = view === 'images' ? '含图片的便签 ' : view === 'deleted' ? '最近删除 ' : '我的便签 ';
      $('.notes-intro').textContent = view === 'deleted' ? '30 天内，可以把便签找回来。' : '随手记下，在这里接着看。';
      renderList();
    }
    $('#clear-search').addEventListener('click', clearFilters);
    const closeReader = () => { if (drawer.open) drawer.close(); };
    $('#reader-back').addEventListener('click',closeReader);
    $('#reader-close').addEventListener('click',closeReader);
    $('#reader-expand').addEventListener('click',() => { const expanded = drawer.classList.toggle('is-expanded'); $('#reader-expand').setAttribute('aria-label',expanded ? '收起阅读' : '展开阅读'); });
    drawer.addEventListener('close',() => { releaseImages(reader); layout.dataset.reading = 'false'; drawer.classList.remove('is-expanded'); $('#reader-expand').setAttribute('aria-label','展开阅读'); list.querySelector(`[data-note-id="${selected}"]`)?.focus({preventScroll:true}); });
    document.addEventListener('click', (event) => {
      const menu = $('#demo-account');
      if (menu.open && !menu.contains(event.target)) menu.open = false;
    });
    renderList();
    if (demo) return;

    function preserveDraft() {
      if (!account?.user?.id || !$('#note-editor').open || !editingID) return true;
      return draftStore.save(account.user.id,{editingID,editingBase,document:{...editingDocument,title:$('#edit-title').value,text:$('#edit-text').value,themeId:Number($('#edit-theme').value)},pendingOperation});
    }
    function restoreDraft() {
      const draft = draftStore.read(account.user.id); if (!draft?.document || !draft.editingID) return;
      editingID = draft.editingID; editingBase = draft.editingBase || 0; editingDocument = draft.document; editingDocument.attachments ||= []; pendingOperation = draft.pendingOperation || null;
      $('#edit-title').value = draft.document.title || ''; $('#edit-text').value = draft.document.text || ''; $('#edit-theme').value = String(draft.document.themeId || 0);
      $('#editor-title').textContent = '继续未保存的便签'; $('#edit-status').textContent = '已恢复此账号的草稿。确认内容后再保存。';
      const rich = !!(draft.document.rtf || draft.document.rtfGzip);
      $('#edit-title').readOnly = rich; $('#edit-text').readOnly = rich; $('#edit-theme').disabled = rich; $('#edit-image').disabled = rich; $('#edit-save').disabled = rich;
      $('#edit-images').textContent = `含 ${(draft.document.attachments || []).length} 张图片。`;
      $('#note-editor').showModal(); $('#edit-title').focus();
    }
    function expireSession() {
      if (expired) return; expired = true; const saved = preserveDraft(); activity.stop();
      document.querySelectorAll('dialog[open]').forEach(dialog => dialog.close());
      releaseImages(document); notes.length = 0; reader.replaceChildren(); list.replaceChildren();
      $('#notes-workspace').hidden = true; $('#demo-account').hidden = true; $('#notes-login-link').hidden = false; $('#notes-gate').hidden = false;
      $('#gate-title').textContent = '登录已过期'; $('.notes-gate p').textContent = '请重新登录。未保存的内容已按账号保留，登录原账号后可继续编辑。';
      if (!saved) {
        $('.notes-gate p').textContent = '浏览器未能暂存草稿。请先复制下方内容，再重新登录。';
        const recovery = text('textarea','','expired-draft'); recovery.readOnly = true; recovery.value = `${$('#edit-title').value}\n\n${$('#edit-text').value}`; recovery.setAttribute('aria-label','未保存的草稿，请复制后登录');
        $('.notes-gate').append(recovery);
      }
      const entry = $('.notes-gate .button'); entry.href = '/login/?reason=expired'; entry.focus();
    }
    const activity = window.KimSession.createActivity({send:() => cloudAPI('/auth/activity',{method:'POST',body:'{}'}),onExpired:expireSession});
    ['pointerdown','keydown','wheel','touchstart'].forEach(name => document.addEventListener(name,activity.interact,{passive:true}));
    document.addEventListener('visibilitychange',() => { if (document.hidden) preserveDraft(); });
    window.addEventListener('pagehide',() => { preserveDraft(); releaseImages(document); activity.stop(); });

    async function showDevices() {
      const dialog = $('#devices-dialog'); if (!dialog.open) dialog.showModal();
      const list = $('#devices-list'), status = $('#devices-status'); status.textContent = '正在读取登录设备…'; $('#devices-retry').hidden = true;
      try {
        const data = await cloudAPI('/auth/sessions'); list.replaceChildren();
        for (const session of data.sessions) {
          const row = text('div','','device-item'), body = text('div',''), title = text('strong',session.deviceName || (session.clientKind === 'desktop' ? 'Windows 客户端' : '网页浏览器'));
          if (session.current) title.append(text('span','当前设备','device-current'));
          body.append(title,text('p',`${session.appId || 'kimnotes'} · ${session.clientKind === 'desktop' ? '桌面' : '网页'} · 最近活跃 ${new Date(session.lastActiveAt || session.createdAt).toLocaleString('zh-CN')}`));
          const revoke = text('button',session.current ? '退出' : '退出设备'); revoke.type = 'button';
          revoke.addEventListener('click',async () => {
            if (!confirm(session.current ? '退出当前浏览器？未保存的草稿会保留。' : '退出这台设备？该设备需要重新登录。')) return;
            revoke.disabled = true;
            try { preserveDraft(); const result = await cloudAPI(`/auth/sessions/${encodeURIComponent(session.id)}`,{method:'DELETE'}); if (result.current || session.current) { expireSession(); const channel = new BroadcastChannel('kimnotes-session'); channel.postMessage('changed'); channel.close(); } else await showDevices(); }
            catch (error) { if (error.status === 401) expireSession(); else status.textContent = error.message; revoke.disabled = false; }
          });
          row.append(body,revoke); list.append(row);
        }
        status.textContent = data.sessions.length ? '桌面端自动续期；网页无操作 30 分钟或登录满 8 小时后失效。' : '暂无登录设备。';
      } catch (error) { if (error.status === 401) expireSession(); else { status.textContent = error.message; $('#devices-retry').hidden = false; } }
    }
    $('#devices-retry').addEventListener('click',showDevices);
    $('#logout-all').addEventListener('click',async () => {
      if (!confirm('退出所有应用中的全部登录设备？各设备需要重新登录。')) return;
      const button = $('#logout-all'); button.disabled = true; preserveDraft();
      try { await cloudAPI('/auth/logout-all',{method:'POST',body:'{}'}); const channel = new BroadcastChannel('kimnotes-session'); channel.postMessage('changed'); channel.close(); expireSession(); }
      catch (error) { if (error.status === 401) expireSession(); else $('#devices-status').textContent = error.message; }
      finally { button.disabled = false; }
    });

    async function synchronize(manual = false) {
      if (syncing || saving || expired || document.hidden) return;
      syncing = true; const state = $('.demo-notice'); state.textContent = '正在同步…';
      try {
        if (loaded) {
          const current = await cloudAPI('/auth/me');
          if (current.user.id !== account.user.id) { expireSession(); location.reload(); return; }
        }
        let changed = !loaded;
        let more = true;
        while (more) {
          const data = await cloudAPI(`/sync/pull?cursor=${encodeURIComponent(cursor)}&limit=100`);
          for (const value of data.changes) {
            const note = fromCloud(value), index = notes.findIndex(item => item.id === value.id);
            if (index < 0) { notes.push(note); changed = true; } else if (notes[index].raw.revision < value.revision) { notes[index] = note; changed = true; }
          }
          cursor = data.cursor; more = data.hasMore;
        }
        loaded = true;
        if (changed) { const previousScroll = $('#reader-panel').scrollTop; renderList(); $('#reader-panel').scrollTop = previousScroll; }
        state.textContent = `已同步 · ${new Date().toLocaleTimeString('zh-CN',{hour:'2-digit',minute:'2-digit'})}`;
        if (manual) announce('便签已更新');
      } catch (error) {
        state.textContent = error.status === 401 ? '登录已过期，请重新登录' : '同步未完成 · 稍后自动重试';
        if (error.status === 401) expireSession();
        if (manual) announce(error.message);
      } finally { syncing = false; }
    }
    function openEditor(note) {
      if (pendingOperation) { announce('上一张便签尚未确认保存，请先重试或取消。'); $('#note-editor').showModal(); return; }
      editingID = note?.id || crypto.randomUUID(); editingBase = note?.raw.revision || 0; editingDocument = note ? structuredClone(note.raw.document) : {attachments:[]};
      editingDocument.attachments ||= [];
      $('#edit-title').value = note?.raw.document.title || ''; $('#edit-text').value = note?.raw.document.text || '';
      $('#edit-theme').value = String(note?.raw.document.themeId || 0);
      $('#editor-title').textContent = note ? '编辑便签' : '新建便签';
      const desktopRichText = !!(note?.raw.document.rtf || note?.raw.document.rtfGzip);
      $('#edit-status').textContent = desktopRichText ? '此便签包含桌面富文本，网页版仅供查看，避免覆盖格式。' : '点击保存后，内容会同步到你的账号。';
      $('#edit-save').disabled = desktopRichText; $('#edit-save').textContent = '保存便签';
      $('#edit-image').disabled = desktopRichText;
      $('#edit-title').readOnly = desktopRichText; $('#edit-text').readOnly = desktopRichText; $('#edit-theme').disabled = desktopRichText;
      $('#edit-images').textContent = editingDocument.attachments.length ? `含 ${editingDocument.attachments.length} 张图片，保存时会保留。` : '支持 PNG、JPEG、GIF、WebP，单张不超过 10 MB。';
      $('#note-editor').showModal(); $('#edit-title').focus(); preserveDraft();
    }
    async function sendOperation(operation) {
      const data = await cloudAPI('/sync/push',{method:'POST',body:JSON.stringify({operations:[operation]})});
      const result = data.results[0], note = fromCloud(result.note), index = notes.findIndex(value => value.id === note.id);
      if (index < 0) notes.push(note); else if (notes[index].raw.revision <= note.raw.revision) notes[index] = note;
      // Do not advance the pull cursor from a push acknowledgement: other
      // devices may have written intervening events this browser has not read.
      return result;
    }
    async function changeNote(note,action,button) {
      if (action === 'delete' && !confirm(`删除「${note.title}」？30 天内可以恢复。`)) return;
      button.disabled = true;
      try {
        const result = await sendOperation({operationId:crypto.randomUUID(),noteId:note.id,baseRevision:note.raw.revision,action});
        announce(result.status === 'conflict' ? '这张便签已有新改动，请查看更新后再操作。' : action === 'delete' ? '已移到最近删除' : '便签已恢复');
        renderList(); await synchronize();
      } catch (error) { announce(error.message); } finally { if (button.isConnected) button.disabled = false; }
    }
    $('#edit-form').addEventListener('submit',async event => {
      event.preventDefault(); if (saving || uploading) return;
      if (editingDocument?.rtf || editingDocument?.rtfGzip) { $('#edit-status').textContent = '请在桌面客户端编辑此富文本便签，格式和图片会保留。'; return; }
      const operation = pendingOperation || {operationId:crypto.randomUUID(),noteId:editingID,baseRevision:editingBase,action:'upsert',document:{title:$('#edit-title').value,text:$('#edit-text').value,themeId:Number($('#edit-theme').value),attachments:editingDocument?.attachments || []}};
      pendingOperation = operation; preserveDraft(); saving = true; $('#edit-save').disabled = true; $('#edit-status').textContent = '正在保存…';
      try {
        const result = await sendOperation(operation); pendingOperation = null; draftStore.remove(account.user.id);
        $('#note-editor').close(); selected = result.note.id; renderList();
        announce(result.status === 'conflict_copy' ? '发现两处修改，已保留为冲突副本。' : '便签已保存到云端');
      } catch (error) { $('#edit-status').textContent = `${error.message} 内容仍保留在当前窗口，可重试保存。`; if (error.status === 401) expireSession(); }
      finally { saving = false; $('#edit-save').disabled = false; await synchronize(); }
    });
    $('#note-editor').addEventListener('cancel',event => { event.preventDefault(); $('#editor-cancel').click(); });
    $('#editor-cancel').addEventListener('click', () => { if (saving || uploading) return; if (pendingOperation && !confirm('保存结果尚未确认。取消会放弃当前重试；已到达服务器的内容仍可能保存成功。')) return; pendingOperation = null; draftStore.remove(account.user.id); $('#note-editor').close(); });
    $('#edit-form').addEventListener('input', () => {
      preserveDraft();
      if (pendingOperation) { $('#edit-title').value = pendingOperation.document.title; $('#edit-text').value = pendingOperation.document.text; $('#edit-theme').value = String(pendingOperation.document.themeId); $('#edit-status').textContent = '请先重试确认上一笔保存，或取消后重新编辑。'; }
    });
    $('#new-note').addEventListener('click', () => openEditor());
    $('#edit-image').addEventListener('change',async event => {
      const file = event.target.files[0]; if (!file) return;
      if (saving || pendingOperation || uploading) { event.target.value = ''; return; }
      if (file.size > 10 * 1024 * 1024) { $('#edit-status').textContent = '单张图片需要小于 10 MB。'; event.target.value = ''; return; }
      if (editingDocument.attachments.length >= 32) { $('#edit-status').textContent = '一张便签最多 32 张图片。'; return; }
      uploading = true; $('#edit-save').disabled = true; event.target.disabled = true; $('#edit-status').textContent = '正在上传图片…';
      try {
        const image = await cloudAPI('/attachments',{method:'POST',body:file,headers:{'Content-Type':file.type || 'application/octet-stream'}});
        if (!editingDocument.attachments.includes(image.id)) editingDocument.attachments.push(image.id); preserveDraft();
        $('#edit-images').textContent = `含 ${editingDocument.attachments.length} 张图片。`; $('#edit-status').textContent = '图片已上传，保存便签后完成关联。';
      } catch (error) { $('#edit-status').textContent = error.message; }
      finally { uploading = false; $('#edit-save').disabled = false; event.target.disabled = false; event.target.value = ''; }
    });
    $('#sync-notes').addEventListener('click', () => synchronize(true));
    $('#password-form').addEventListener('submit',async event => {
      event.preventDefault(); const button = $('#change-password'); button.disabled = true;
      try { preserveDraft(); await cloudAPI('/auth/password',{method:'POST',body:JSON.stringify({currentPassword:$('#current-password').value,newPassword:$('#next-password').value})}); location.replace('/login/'); }
      catch (error) { $('#password-status').textContent = error.message; }
      finally { button.disabled = false; }
    });
    window.addEventListener('online', () => synchronize());
    const sessionChannel = new BroadcastChannel('kimnotes-session');
    sessionChannel.onmessage = () => { expireSession(); location.reload(); };
    window.addEventListener('pageshow', event => { if (event.persisted) { preserveDraft(); $('#notes-workspace').hidden = true; location.reload(); } });
    window.addEventListener('beforeunload', event => { preserveDraft(); if ($('#note-editor').open && ($('#edit-title').value || $('#edit-text').value)) { event.preventDefault(); event.returnValue = ''; } });
    document.addEventListener('visibilitychange', () => { if (!document.hidden) synchronize(); });
    setInterval(synchronize,30000); await synchronize();
    if (!expired) restoreDraft();
    if (!expired && new URL(location.href).searchParams.get('panel') === 'devices') showDevices();
  }
})();
