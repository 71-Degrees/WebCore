# Colour theme: System, Light, Dark

WebCore gives every app the same visitor choice of colour theme: the page follows the operating system (System), or is forced to Light or Dark. The
choice is kept in the browser's `localStorage` under the key `theme`. It is **per browser, not per account**: a visitor who signs in on a second computer
chooses again there.

Files: `src/WebCore.UI/wwwroot/theme.js` (served as `_content/WebCore.UI/theme.js`) and `src/WebCore.UI/ThemeSwitch.razor`.

## 1. Colours are tokens, defined once per selector

Every colour on the page is a custom property on `:root`, with the light values. The dark values are written twice, once per selector, because a media
query cannot share a block with an attribute selector. Keep the two dark blocks identical:

```css
:root {
    --app-bg: #f4ecdc;
    --app-text: #2a211b;
    /* ...every other token, light values... */
    color-scheme: light;
}

/* Follows the system, unless the visitor chose Light. */
@media (prefers-color-scheme: dark) {
    :root:not([data-theme="light"]) {
        --app-bg: #17120f;
        --app-text: #efe4cf;
        /* ...the same dark values, token for token... */
        color-scheme: dark;
    }
}

/* The visitor chose Dark. */
:root[data-theme="dark"] {
    --app-bg: #17120f;
    --app-text: #efe4cf;
    /* ...the same dark values, token for token... */
    color-scheme: dark;
}

body {
    background: var(--app-bg);   /* an explicit background from a token, never left to the browser */
    color: var(--app-text);
}
```

Rules that follow from this:

- The `light` choice sets `data-theme="light"`, which the media query excludes, so Light wins over a dark system. The `:root` block carries `color-scheme: light`.
- The `system` choice removes the attribute, so the media query alone decides.
- Never test `prefers-color-scheme` anywhere else in the app's CSS. Use the tokens. A place that hard-codes a light colour (`white`, `#fff` on a card) breaks Dark.
- Contrast: text pairs stay at 4.5:1 or better in both palettes.

## 2. Load `theme.js` in `<head>`, synchronously

The site's Content-Security-Policy forbids inline scripts, so the script is an external file. It must be a plain `<script src>` in `<head>`, **without**
`defer` or `async`, so the saved theme is applied before the first paint:

```html
<head>
    <meta charset="utf-8" />
    <script src="@Assets["_content/WebCore.UI/theme.js"]"></script>
    <link rel="stylesheet" href="@Assets["app.css"]" />
    ...
</head>
```

Pass the path through `@Assets` like every other static file (including `_content/<library>/...` files), so it is fingerprinted and a phone never keeps a
stale copy. Put the script before the stylesheet.

## 3. Place the switch

The switch belongs in the header, where it is always on screen, next to the brand and the account links (CrateDigger does this):

```razor
<header class="app-header">
    <span class="app-brand-lockup">...</span>
    <span class="app-header-end">
        <ThemeSwitch />
        <AccountNav />
    </span>
</header>
```

`ThemeSwitch` renders a three-position rocker: `<div class="wc-theme" role="group" aria-label="Colour theme">` with three buttons,
`data-theme-choice="light"` (a sun, "Light theme"), `data-theme-choice="system"` (a half-filled circle, "Match system", the middle "Auto" position)
and `data-theme-choice="dark"` (a moon, "Dark theme"). `theme.js` marks the active one with `aria-pressed="true"` and keeps every open tab in step.
The control has no text, so its accessible names carry the meaning; each button's hit area is at least 44 px square.

## 4. The switch's CSS (add to the app's stylesheet, using its tokens)

The component ships no stylesheet of its own: each app styles it with its tokens, so it takes the app's palette. Paste this block and change the
token names to the app's own (the fallbacks keep it readable if a token is missing):

```css
/* Colour theme rocker (WebCore ThemeSwitch): sun | Auto | moon. The visible segments are 32 px tall; the button is 44 px square so it stays a phone
   touch target, and the rocker keeps its look at any size. Pressed = the lit segment, inverted like a rocker switch. */
.wc-theme {
    position: relative;
    display: inline-flex;
    align-items: stretch;
    flex: none;
    border: 1px solid var(--app-border, #3d3129);
    border-radius: 999px;
    background: var(--app-stock, #211a16);
    overflow: hidden;
}

.wc-theme-button {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    box-sizing: border-box;
    min-width: 44px;
    min-height: 44px;
    margin: 0;
    padding: 0;
    border: 0;
    background: transparent;
    color: var(--app-muted, #b8a890);
    cursor: pointer;
}

/* The Auto segment sits between the sun and the moon; a hairline divides it from each end. */
.wc-theme-auto { border-left: 1px solid var(--app-border, #3d3129); border-right: 1px solid var(--app-border, #3d3129); }

.wc-theme-button[aria-pressed="true"] {
    background: var(--app-ink, #efe4cf);
    color: var(--app-paper, #17120f);
}

.wc-theme-button:hover:not([aria-pressed="true"]) { color: var(--app-ink, #efe4cf); }

.wc-theme-button:focus-visible {
    outline: 3px solid var(--app-focus, #e8b65a);
    outline-offset: -3px;
}
```

## 5. What the visitor sees

- A first visit has no saved choice, so the page follows the system (the browser's own setting decides).
- Clicking a button applies the theme at once and saves it. Reloading keeps it. A second open tab changes with it.
- If storage is blocked (private browsing, site data disabled), the choice still applies to the page until it is closed; nothing breaks.

## 6. Checks before an app ships the switch

- Load the page with the system in light and in dark, then click each button: the page, the cards, the form fields, the native controls (`color-scheme`)
  and the focus rings change, with no light-only colour left.
- Two tabs on the same origin agree after a click.
- The header row (brand, rocker, account links) wraps on a 360 px wide phone and does not push the page's main content sideways.
- The rocker's three positions read correctly with no text: Light is lit under light, Dark under dark, Auto under system.
