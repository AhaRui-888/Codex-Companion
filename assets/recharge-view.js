(() => {
  'use strict';
  if (location.origin !== 'https://xai-tools.cn' || window.top !== window) return;
  const style = document.createElement('style');
  style.textContent = /*COMPANION_STYLE*/;
  const update = () => {
    if (!document.documentElement) return;
    if (!style.isConnected) document.documentElement.appendChild(style);
    const ready = !!document.querySelector('.recharge-panel .amount-grid');
    if (document.documentElement.classList.contains('companion-recharge') !== ready)
      document.documentElement.classList.toggle('companion-recharge', ready);
    // Scope the layout to the known recharge page. Authentication/error pages
    // retain their own usable layout if the website session expires.
    document.querySelectorAll('.amount-grid button, .payment-methods button').forEach(button => {
      const selected = String(button.classList.contains('selected'));
      if (button.getAttribute('aria-pressed') !== selected) button.setAttribute('aria-pressed', selected);
    });
  };
  const observer = new MutationObserver(update);
  observer.observe(document, { childList: true, subtree: true, attributes: true, attributeFilter: ['class'] });
  document.addEventListener('DOMContentLoaded', update, { once: true });
  update();
})();
