// Progressive enhancement: content stays visible until a short, one-time effect runs.
export function initScrollReveal() {
  if (!('IntersectionObserver' in window) || !Element.prototype.animate || !window.matchMedia) return;
  const reducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)');
  if (reducedMotion.matches) return;

  const targets = [...document.querySelectorAll([
    '.section-heading', '.service-list > .service-link',
    '#features .guide-layout > article', '.pricing-grid > article', '.plan-common',
    '.privacy-inner > div', '#connect .connection-plan-note', '#guide-tabs', '#guide-panel',
    '#faq > div', '.release-section > div', '#download'
  ].join(','))];
  const pending = new Set();
  const active = new Map();
  const cardSelector = '.service-list > .service-link, #features .guide-layout > article, .pricing-grid > article';
  let stopped = false;

  function finish(target) {
    pending.delete(target);
    observer.unobserve(target);
    const animation = active.get(target);
    active.delete(target);
    if (animation) animation.cancel();
  }

  function revealDestination(destination) {
    if (!destination) return;
    for (const target of targets) {
      if (target === destination || target.contains(destination) || destination.contains(target)) finish(target);
    }
  }

  function hashDestination(hash) {
    try { return document.getElementById(decodeURIComponent(hash.slice(1))); }
    catch { return null; }
  }

  function onHashChange() { revealDestination(hashDestination(location.hash)); }
  function onFocus(event) { revealDestination(event.target); }
  function onLinkClick(event) {
    if (!(event.target instanceof Element)) return;
    const link = event.target.closest('a[href]');
    if (!link) return;
    const url = new URL(link.href, location.href);
    if (url.origin === location.origin && url.pathname === location.pathname && url.search === location.search && url.hash) {
      revealDestination(hashDestination(url.hash));
    }
  }

  function stop() {
    stopped = true;
    observer.disconnect();
    pending.clear();
    for (const animation of active.values()) animation.cancel();
    active.clear();
    document.removeEventListener('focusin', onFocus);
    document.removeEventListener('click', onLinkClick, true);
    window.removeEventListener('hashchange', onHashChange);
    window.removeEventListener('beforeprint', stop);
    window.removeEventListener('pageshow', onPageShow);
    if (reducedMotion.removeEventListener) reducedMotion.removeEventListener('change', onMotionChange);
    else reducedMotion.removeListener(onMotionChange);
  }

  function onMotionChange(event) { if (event.matches) stop(); }
  function onPageShow(event) { if (event.persisted) stop(); }

  const observer = new IntersectionObserver(entries => {
    if (stopped) return;
    // Measure a row before starting effects so transforms cannot change its stagger order.
    const ready = entries.filter(entry => entry.isIntersecting && pending.has(entry.target)).map(entry => {
      const target = entry.target;
      let delay = 0;
      if (target.matches(cardSelector)) {
        const row = [...target.parentElement.children].filter(sibling =>
          sibling.matches(cardSelector) && Math.abs(sibling.getBoundingClientRect().top - entry.boundingClientRect.top) < 2);
        delay = Math.min(Math.max(row.indexOf(target), 0) * 70, 140);
      }
      return { target, top: entry.boundingClientRect.top, delay };
    });
    for (const { target, top, delay } of ready) {
      finish(target);
      if (top < 0 || document.hidden || reducedMotion.matches) continue;
      try {
        const animation = target.animate([
          { opacity: 0, transform: 'translateY(16px)' },
          { opacity: 1, transform: 'translateY(0)' }
        ], { duration: 500, delay, easing: 'cubic-bezier(.22, 1, .36, 1)', fill: 'backwards' });
        active.set(target, animation);
        const forget = () => { if (active.get(target) === animation) active.delete(target); };
        animation.addEventListener('finish', forget, { once: true });
        animation.addEventListener('cancel', forget, { once: true });
      } catch { /* An unavailable animation must never affect content or navigation. */ }
    }
    if (!pending.size) observer.disconnect();
  }, { rootMargin: '0px 0px -24px 0px', threshold: 0 });

  for (const target of targets) {
    // Do not replay the first screen, restored scroll positions, or content already passed.
    if (target.getBoundingClientRect().top >= window.innerHeight) {
      pending.add(target);
      observer.observe(target);
    }
  }
  onHashChange();
  revealDestination(document.activeElement === document.body ? null : document.activeElement);
  document.addEventListener('focusin', onFocus);
  document.addEventListener('click', onLinkClick, true);
  window.addEventListener('hashchange', onHashChange);
  window.addEventListener('beforeprint', stop);
  window.addEventListener('pageshow', onPageShow);
  if (reducedMotion.addEventListener) reducedMotion.addEventListener('change', onMotionChange);
  else reducedMotion.addListener(onMotionChange);
}
