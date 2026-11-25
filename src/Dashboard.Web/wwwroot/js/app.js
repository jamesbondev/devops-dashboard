// Small client helpers: title badge, notifications, quick-jump shortcut, presentation mode, theme.
window.dash = {
  setTitle: (count) => { document.title = (count > 0 ? `(${count}) ` : '') + 'DevOps Dashboard'; },
  notify: async (key, title, body) => {
    try {
      const seen = JSON.parse(localStorage.getItem('dash.notified') || '{}');
      if (seen[key]) return;
      if (!('Notification' in window)) return;
      if (Notification.permission === 'default') await Notification.requestPermission();
      if (Notification.permission !== 'granted') return;
      new Notification(title, { body });
      seen[key] = Date.now();
      const cutoff = Date.now() - 7 * 864e5;
      for (const k of Object.keys(seen)) if (seen[k] < cutoff) delete seen[k];
      localStorage.setItem('dash.notified', JSON.stringify(seen));
    } catch { /* storage or notifications unavailable */ }
  },
  requestNotifications: async () => { try { return await Notification.requestPermission(); } catch { return 'unsupported'; } },
  getPresent: () => { try { return localStorage.getItem('dash.present') === '1'; } catch { return false; } },
  setPresent: (on) => { document.body.classList.toggle('present', on); try { localStorage.setItem('dash.present', on ? '1' : '0'); } catch { } },
  setTheme: (t) => { if (t) document.documentElement.setAttribute('data-theme', t); else document.documentElement.removeAttribute('data-theme'); try { localStorage.setItem('dash.theme', t || ''); } catch { } },
  getTheme: () => { try { return localStorage.getItem('dash.theme') || ''; } catch { return ''; } },
  download: (name, text) => { const a = document.createElement('a'); a.href = URL.createObjectURL(new Blob([text], { type: 'text/markdown' })); a.download = name; a.click(); setTimeout(() => URL.revokeObjectURL(a.href), 1000); },
  copy: async (text) => { try { await navigator.clipboard.writeText(text); return true; } catch { return false; } },
};
(function () {
  try {
    if (localStorage.getItem('dash.present') === '1') document.addEventListener('DOMContentLoaded', () => document.body.classList.add('present'));
    const t = localStorage.getItem('dash.theme'); if (t) document.documentElement.setAttribute('data-theme', t);
  } catch { }
  document.addEventListener('keydown', (e) => {
    if (e.key === '/' && !['INPUT', 'TEXTAREA', 'SELECT'].includes(document.activeElement?.tagName)) {
      const j = document.getElementById('jump'); if (j) { e.preventDefault(); j.focus(); j.select(); }
    }
  });
})();
