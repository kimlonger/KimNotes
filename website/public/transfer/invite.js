(() => {
  'use strict';
  const button = document.querySelector('#copy-invitation');
  const status = document.querySelector('#invitation-status');
  const prefix = 'https://note.kimlulu.com/transfer/#';
  // The final base64url digit of a 32-byte token has two zero padding bits.
  const tokenPattern = /^[A-Za-z0-9_-]{42}[AEIMQUYcgkosw048]$/;
  const currentToken = () => location.hash.slice(1);

  function showStatus(message) {
    status.textContent = message;
    status.hidden = !message;
  }

  function update() {
    button.hidden = !tokenPattern.test(currentToken());
    showStatus(button.hidden ? '请打开发送方分享的完整邀请链接。' : '');
  }

  button.addEventListener('click', async () => {
    const token = currentToken();
    if (!tokenPattern.test(token)) return update();
    try {
      await navigator.clipboard.writeText(prefix + token);
      showStatus('邀请已复制，请在桌面版粘贴。');
    } catch {
      showStatus('请从地址栏复制完整邀请链接。');
    }
  });
  window.addEventListener('hashchange', update);
  update();
})();
