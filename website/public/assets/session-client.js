/* Browser session policy. No authentication credentials are stored here. */
((root) => {
  'use strict';
  const createActivity = ({send, onExpired, now = Date.now, visible = () => !document.hidden, setTimer = setInterval, clearTimer = clearInterval}) => {
    let lastInteraction = 0, lastSent = 0, sending = false, stopped = false;
    const interact = event => { if (event?.isTrusted && visible()) lastInteraction = now(); };
    const tick = async () => {
      const current = now();
      // A poll, focus, visibility change or page load is not user activity.
      if (stopped || sending || !visible() || !lastInteraction || current - lastInteraction > 60000 || lastInteraction <= lastSent || current - lastSent < 60000) return;
      sending = true; const reported = lastInteraction;
      try { await send(); lastSent = reported; }
      catch (error) { if (error.status === 401) { stopped = true; onExpired(); } }
      finally { sending = false; }
    };
    const timer = setTimer(tick,15000);
    return {interact,tick,stop() { stopped = true; clearTimer(timer); }};
  };
  const drafts = storage => ({
    save(userId, draft) { if (!userId) return false; try { storage.setItem(`kimnotes-draft:${userId}`,JSON.stringify({...draft,version:1,userId})); return true; } catch { return false; } },
    read(userId) { try { const draft = JSON.parse(storage.getItem(`kimnotes-draft:${userId}`)); return draft?.version === 1 && draft.userId === userId ? draft : null; } catch { return null; } },
    remove(userId) { try { storage.removeItem(`kimnotes-draft:${userId}`); } catch {} }
  });
  const client = {createActivity,drafts};
  if (typeof module !== 'undefined' && module.exports) module.exports = client;
  else root.KimSession = client;
})(typeof window !== 'undefined' ? window : globalThis);
