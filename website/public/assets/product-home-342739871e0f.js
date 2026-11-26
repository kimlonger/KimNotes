(() => {
  'use strict';
  const home = document.querySelector('.product-home');
  if (!home) return;

  home.classList.add("interactive");
  const tabs = [...home.querySelectorAll('#tabs button')];
  const scenes = tabs.map(tab => document.getElementById(tab.getAttribute('aria-controls')));
  const themes = [...home.querySelectorAll('#groups .sw')];
  const preview = document.querySelector('#tbig img');
  const chipName = document.querySelector('#chipname');
  const chipDot = document.querySelector('#chipdot');
  const heroImages = [...document.querySelectorAll('#heroLayers img')];
  const firstHero = heroImages[0].getAttribute('src');
  const hero = document.querySelector('.hero');
  const gallery = document.querySelector('#themes');
  const demos = document.querySelector('.demo');
  const motion = document.querySelector('#motion-toggle');
  const preference = matchMedia('(prefers-reduced-motion: reduce)');
  const visible = new Set();
  let paused = preference.matches;
  let sceneIndex = 0;
  let themeIndex = themes.length - 1;
  let heroIndex = -1;
  let heroFrame = 0;
  let chosenScene = false;
  let chosenTheme = false;
  let heroTimer, sceneTimer, themeTimer;
  let heroRequest = 0;

  function showScene(index, animate) {
    sceneIndex = index;
    tabs.forEach((tab, i) => {
      const selected = i === index;
      tab.setAttribute('aria-pressed', String(selected));
      tab.classList.toggle('on', selected);
      scenes[i].classList.toggle('on', selected);
      scenes[i].classList.toggle('play', selected && animate);
    });
  }

  function showTheme(index) {
    themeIndex = index;
    const choice = themes[index];
    preview.src = choice.querySelector('img').src;
    preview.alt = choice.textContent.trim() + '主题便签';
    document.querySelector('#glow').style.backgroundColor = choice.dataset.color;
    themes.forEach(item => {
      item.classList.toggle('on', item === choice);
      item.setAttribute('aria-pressed', String(item === choice));
    });
  }

  function nextHero() {
    heroIndex = (heroIndex + 1) % themes.length;
    const theme = themes[heroIndex];
    const next = 1 - heroFrame;
    const image = heroImages[next];
    const request = ++heroRequest;
    const swap = () => {
      if (request !== heroRequest || document.hidden || paused) return;
      heroImages[heroFrame].classList.remove('on');
      image.classList.add('on');
      heroFrame = next;
      chipName.textContent = theme.textContent.trim();
      chipDot.style.backgroundColor = theme.dataset.color;
      document.querySelector('#tRem').classList.toggle('show', heroIndex % 4 === 0);
      document.querySelector('#tOcr').classList.toggle('show', heroIndex % 4 === 2);
    };
    image.onload = swap;
    image.alt = theme.textContent.trim() + '主题的便签';
    image.src = heroIndex === themes.length - 1 ? firstHero : theme.querySelector('img').src;
  }

  function schedule() {
    clearInterval(heroTimer);
    clearInterval(sceneTimer);
    clearInterval(themeTimer);
    const running = !paused && !document.hidden;
    home.dataset.motion = paused ? 'paused' : 'playing';
    motion.setAttribute('aria-pressed', String(paused));
    motion.textContent = paused ? '播放动画' : '暂停动画';
    if (running && visible.has(hero)) heroTimer = setInterval(nextHero, 2900);
    if (running && visible.has(gallery) && !chosenTheme) {
      themeTimer = setInterval(() => showTheme((themeIndex + 1) % themes.length), 3400);
    }
    if (running && visible.has(demos) && !chosenScene) {
      sceneTimer = setInterval(() => showScene((sceneIndex + 1) % tabs.length, true), 7200);
    }
  }

  tabs.forEach((tab, index) => tab.addEventListener('click', () => {
    chosenScene = true;
    showScene(index, true);
    schedule();
  }));
  demos.addEventListener('focusin', () => { chosenScene = true; schedule(); });
  themes.forEach((theme, index) => theme.addEventListener('click', () => {
    chosenTheme = true;
    showTheme(index);
    schedule();
  }));
  gallery.addEventListener('focusin', () => { chosenTheme = true; schedule(); });
  motion.hidden = false;
  motion.addEventListener('click', () => { paused = !paused; schedule(); });
  preference.addEventListener('change', () => { paused = preference.matches; schedule(); });
  document.addEventListener('visibilitychange', schedule);
  showTheme(themeIndex);
  showScene(0, true);
  if ('IntersectionObserver' in window) {
    const observer = new IntersectionObserver(entries => {
      for (const entry of entries) {
        if (entry.isIntersecting) visible.add(entry.target);
        else visible.delete(entry.target);
      }
      schedule();
    });
    [hero, gallery, demos].forEach(el => observer.observe(el));
  } else {
    [hero, gallery, demos].forEach(el => visible.add(el));
  }
  schedule();

  const trial = document.querySelector('#note-trial');
  const note = trial.querySelector('.trial-note');
  const handle = trial.querySelector('.trial-handle');
  const colors = [...trial.querySelectorAll('[data-tone][type=button]')];
  home.querySelectorAll('[data-open-trial]').forEach(button => button.addEventListener('click', () => {
    trial.showModal();
    note.querySelector('textarea').focus();
  }));
  colors.forEach(button => button.addEventListener('click', () => {
    note.dataset.tone = button.dataset.tone;
    colors.forEach(color => color.setAttribute('aria-pressed', String(color === button)));
  }));
  let grab;
  handle.addEventListener('pointerdown', event => {
    if (!event.isPrimary || event.button !== 0) return;
    const box = note.getBoundingClientRect();
    grab = { id:event.pointerId, x:event.clientX - box.left, y:event.clientY - box.top };
    handle.setPointerCapture(event.pointerId);
  });
  handle.addEventListener('pointermove', event => {
    if (!grab || grab.id !== event.pointerId) return;
    const box = note.parentElement.getBoundingClientRect();
    const travelX = Math.max(1, box.width - note.offsetWidth);
    const travelY = Math.max(1, box.height - note.offsetHeight);
    const x = 100 * Math.max(0, Math.min(1, (event.clientX - box.left - grab.x) / travelX));
    const y = 100 * Math.max(0, Math.min(1, (event.clientY - box.top - grab.y) / travelY));
    note.style.left = x + '%';
    note.style.top = y + '%';
    note.style.transform = 'translate(' + (-x) + '%, ' + (-y) + '%)';
  });
  handle.addEventListener('lostpointercapture', () => { grab = null; });
})();
