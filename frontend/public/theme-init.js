/*
  Applies the reader's appearance settings before the first paint.

  The app sets these when its module bundle runs, which is after the browser has painted the page
  once with the defaults: a reader who chose dark saw a light flash on every load, and one who chose
  large text saw the page jump. This runs first, from <head>, synchronously. It is a file rather than
  an inline script because the security model allows no inline script
  (docs/architecture/04-security-model.md: default-src 'self', no unsafe-inline). Keep the keys and
  the rules in step with src/lib/appearance.ts.

  A value the stylesheet does not know matches no rule and leaves the default, so nothing here needs
  to check one.
*/
;(function () {
  var settings = {}
  try {
    settings = JSON.parse(localStorage.getItem('sqm.appearance') || 'null') || {
      theme: localStorage.getItem('sqm.theme') || 'system',
    }
  } catch (e) {
    // Storage can throw in a locked-down browser; the system preferences still apply.
  }

  var theme = settings.theme || 'system'
  var dark =
    theme === 'dark' ||
    (theme === 'system' && window.matchMedia('(prefers-color-scheme: dark)').matches)

  var motion = settings.motion || 'system'
  if (motion === 'system') {
    motion = window.matchMedia('(prefers-reduced-motion: reduce)').matches ? 'reduced' : 'full'
  }

  var root = document.documentElement
  root.setAttribute('data-theme', dark ? 'dark' : 'light')
  root.setAttribute('data-motion', motion)
  root.setAttribute('data-font', settings.fontFamily || 'system')
  root.setAttribute('data-text', settings.fontSize || 'medium')
  root.setAttribute('data-weight', settings.fontWeight || 'regular')
  root.setAttribute('data-leading', settings.lineSpacing || 'normal')
  root.setAttribute('data-density', settings.density || 'normal')
  root.setAttribute('data-accent', settings.accent || 'blue')
  root.setAttribute('data-corners', settings.corners || 'normal')
})()
