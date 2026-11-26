(() => {
  'use strict';
  const button = document.querySelector('#copy-invitation');
  const status = document.querySelector('#invitation-status');
  const prefix = 'https://note.kimlulu.com/transfer/#';
  // The final base64url digit of a 32-byte token has two zero padding bits.
  const tokenPattern = /^[A-Za-z0-9_-]{42}[AEIMQUYcgkosw048]$/;
  const shortCodePattern = /^[23456789ABCDEFGHJKLMNPQRSTUVWXYZ]{8}$/;
  function currentToken() {
    const value = location.hash.slice(1);
    if (shortCodePattern.test(value.toUpperCase())) return value.toUpperCase();
    return tokenPattern.test(value) ? value : '';
  }

  function showStatus(message) {
    status.textContent = message;
    status.hidden = !message;
  }

  function update() {
    const token = currentToken();
    button.hidden = !token;
    button.textContent = token.length === 8 ? '复制取件码' : '复制邀请';
    showStatus(button.hidden ? '请打开发送方分享的完整邀请链接。' : '');
  }

  button.addEventListener('click', async () => {
    const token = currentToken();
    if (!token) return update();
    try {
      await navigator.clipboard.writeText(token.length === 8 ? token : prefix + token);
      if (currentToken() === token) showStatus('已复制，请在桌面版的「接收」页粘贴。');
    } catch {
      if (currentToken() === token) showStatus('请从地址栏复制完整邀请链接。');
    }
  });
  window.addEventListener('hashchange', update);
  update();
})();
