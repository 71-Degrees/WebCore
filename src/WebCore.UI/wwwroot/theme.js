// @ts-check
/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */
// Colour theme: the visitor's choice of System, Light or Dark (docs/theme.md).
// Two jobs. APPLY sets document.documentElement.dataset.theme from the saved choice; it must run from a synchronous
// <script> in <head> (no defer, no async), so the page never paints in the wrong theme. CONTROL wires every
// [data-theme-choice] button; listeners sit on document, so buttons that Blazor renders later still work.
(() => {
  'use strict';

  const KEY = 'theme';
  const BUTTON = '[data-theme-choice]';
  const CHOICES = ['light', 'dark', 'system'];

  /** @param {unknown} value @returns {string} */
  const parseChoice = (value) => (CHOICES.includes(/** @type {string} */ (value)) ? /** @type {string} */ (value) : 'system');

  // Storage can be blocked (private windows, disabled site data). An unreadable value means "system".
  const readChoice = () => {
    try {
      return parseChoice(localStorage.getItem(KEY));
    } catch {
      return 'system';
    }
  };

  // A blocked write still applies to this page; the choice just is not remembered for the next visit.
  const writeChoice = (choice) => {
    try {
      localStorage.setItem(KEY, choice);
    } catch {
      console.warn('The colour theme could not be saved; it applies to this page only.');
    }
  };

  /** @param {string} choice */
  const apply = (choice) => {
    const root = document.documentElement;
    if (choice === 'system') {
      delete root.dataset.theme;
    } else {
      root.dataset.theme = choice;
    }
  };

  // Marks the active button with aria-pressed; the others are "false".
  const sync = () => {
    const current = readChoice();
    document.querySelectorAll(BUTTON).forEach((button) => {
      const own = /** @type {HTMLElement} */ (button).dataset.themeChoice;
      button.setAttribute('aria-pressed', String(own === current));
    });
  };

  const choose = (choice) => {
    writeChoice(choice);
    apply(choice);
    sync();
  };

  document.addEventListener('click', (event) => {
    const target = event.target instanceof Element ? event.target.closest(BUTTON) : null;
    const own = /** @type {HTMLElement | null} */ (target)?.dataset.themeChoice;
    if (own === undefined || !CHOICES.includes(own)) return;
    choose(own);
  });

  // Another tab changed the choice: follow it. A null key means the whole storage was cleared.
  window.addEventListener('storage', (event) => {
    if (event.key !== null && event.key !== KEY) return;
    apply(readChoice());
    sync();
  });

  // Blazor can add the switch after load, so the states are re-read whenever nodes are added.
  new MutationObserver(sync).observe(document.documentElement, { childList: true, subtree: true });

  // Blazor's enhanced navigation patches the page in place, <html> attributes included, and the new page's html has no data-theme: the saved choice is put back the moment
  // the attribute drifts from it, so the theme holds from page to page without a refresh. Setting the attribute to what it should already be changes nothing, so no loop.
  const holdChoice = () => {
    const wanted = readChoice();
    const actual = document.documentElement.dataset.theme;
    if ((wanted === 'system' ? undefined : wanted) !== actual) apply(wanted);
  };
  new MutationObserver(holdChoice).observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] });

  apply(readChoice());
  document.addEventListener('DOMContentLoaded', sync);
})();
