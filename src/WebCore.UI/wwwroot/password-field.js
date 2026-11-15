/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */
// Progressive enhancement for every [data-password-field] (WebCore.UI PasswordField). Without this script the field is a plain password input.
// Listeners sit on document and fields are looked up when used, so fields that appear after load (Blazor renders late) work too.
(() => {
  const FIELD = '[data-password-field]';
  const TOGGLE = '.wc-password-toggle';
  const INPUT = '.wc-password-input';
  const CAPS = '.wc-password-caps-text';
  const SHOW = 'Show password';
  const HIDE = 'Hide password';

  const fieldOf = (node) => (node instanceof Element ? node.closest(FIELD) : null);

  // The button is hidden in the markup so a page without JavaScript shows no control that does nothing; enhancing a field reveals it.
  const enhance = (field) => {
    const button = field.querySelector(TOGGLE);
    if (button) button.hidden = false;
  };

  const enhanceAll = (root) => {
    if (root.matches?.(FIELD)) enhance(root);
    root.querySelectorAll?.(FIELD).forEach(enhance);
  };

  const setVisible = (field, visible) => {
    const input = field.querySelector(INPUT);
    const button = field.querySelector(TOGGLE);
    if (!input || !button) return;
    input.type = visible ? 'text' : 'password';
    button.setAttribute('aria-pressed', String(visible));
    button.setAttribute('aria-label', visible ? HIDE : SHOW);
    button.querySelector('.wc-password-eye').hidden = visible;
    button.querySelector('.wc-password-eye-off').hidden = !visible;
  };

  const setCapsWarning = (field, on) => {
    const warning = field.querySelector(CAPS);
    if (warning) warning.hidden = !on;
  };

  // Key events carry the modifier state; focus events do not, so focus leaves the warning alone and the first key sets it.
  const onKey = (event) => {
    const field = fieldOf(event.target);
    if (!field || typeof event.getModifierState !== 'function') return;
    setCapsWarning(field, event.getModifierState('CapsLock'));
  };

  const onBlur = (event) => {
    const field = fieldOf(event.target);
    if (field) setCapsWarning(field, false);
  };

  document.addEventListener('click', (event) => {
    if (!(event.target instanceof Element)) return;
    const button = event.target.closest(TOGGLE);
    const field = button && fieldOf(button);
    if (field) setVisible(field, field.querySelector(INPUT).type === 'password');
  });

  document.addEventListener('keydown', onKey, true);
  document.addEventListener('keyup', onKey, true);
  document.addEventListener('blur', onBlur, true);

  // Re-hide the password before the browser sends the form, so a revealed value is never left showing on the next page.
  document.addEventListener('submit', (event) => {
    if (!(event.target instanceof Element)) return;
    event.target.querySelectorAll(FIELD).forEach((field) => setVisible(field, false));
  }, true);

  enhanceAll(document);
  new MutationObserver((records) => {
    for (const record of records) {
      for (const node of record.addedNodes) {
        if (node instanceof Element) enhanceAll(node);
      }
    }
  }).observe(document.body, { childList: true, subtree: true });
})();
