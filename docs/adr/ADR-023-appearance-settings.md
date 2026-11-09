# ADR-023: What a person can change about how the application looks

- **Status:** Accepted
- **Date:** 2026-11-07
- **Decided by:** the product owner asked on 2026-11-06 for one set of settings for theme, typeface, text
  size and weight, line spacing, density of tables and forms, main colour, corners and animation, with a
  live preview, a way back to the defaults, and each person's choices kept, applied on every page and
  chart without costing readability, accessibility or speed. The details were left to this work.

## Context

The theme was the one thing a person could change, kept in the browser. Everything else was fixed in
`tokens.css`, which was already the right shape for this: every component reads tokens, so a change to a
token reaches every page. What tokens do not reach is a canvas. The nine charts draw with ECharts, with
label sizes written as plain numbers and no typeface set at all, so they used a generic sans-serif whatever
the page was in.

## Decision 1: each setting is one attribute, and an attribute changes tokens

`<html>` carries `data-font`, `data-text`, `data-weight`, `data-leading`, `data-density`, `data-accent`,
`data-corners` and `data-motion`, beside `data-theme`. `design-system/appearance.css` turns each into token
values and contains nothing else. No component knows any of this exists, so a page written next year follows
without being told, and there is no second code path to fall out of step.

- **Text size** multiplies the type scale (`--text-scale`), not the root font size. Scaling the root would
  zoom the whole interface, spacing included, which is what the browser's own zoom is for.
- **Density** overrides `--spacing` on `table` and `form`. Tailwind's spacing utilities are
  `calc(var(--spacing) * n)`, so one variable changes every padding and gap inside, and the page's own rhythm
  stays. A field outside a form takes it too, so a filter bar matches a dialog.
- **Line spacing** multiplies every line-height token. **Corners** set the four radius tokens. **Weight** moves
  the three weights the product uses together, so a heading stays heavier than the text under it.

## Decision 2: steps, not free values

Four sizes, three weights, six colours. Each is a step someone has looked at, and every one of them can be
held to a test. A free colour picker could offer a colour that fails contrast, or that means danger, and a free
size could break a table. The cost is that someone wanting exactly 15.5px cannot have it.

**The accents leave out red, amber and green**, which mean danger, warning and success here: a primary button in
the colour of danger is a lie. `accentContrast.spec.ts` computes every pair from the stylesheet: text on a fill,
fill as text on a surface, on the canvas and on its own tint, in both themes, all at 4.5:1 or better, and inside
sRGB, because outside it the browser picks a colour of its own and the sums would be about nothing.

## Decision 3: one contract, for both sides

`frontend/src/lib/appearance-contract.json` lists each setting, its values and its default. The API embeds it and
validates against it, as it does the dashboard's widget contract, so a value cannot exist on one side only and a
new setting is a change to one file. The row is `jsonb` for the same reason. An old row can never break a page:
the frontend ignores what the contract no longer offers.

## Decision 4: two copies, and the account's wins

A copy in the browser exists for one reason: `public/theme-init.js` has to set the attributes before the first
paint, and the server cannot answer in time. It runs from `<head>`, as a file because the security model allows
no inline script. `themeInit.spec.ts` runs it and the module on the same settings and compares, because if they
differ a page paints one way and jumps to the other on every load.

The account's copy is what follows a person. When they sign in, what they saved wins. If they have saved nothing,
what is in this browser becomes theirs, which is how a theme chosen before this existed keeps working, unless
the browser's copy is someone else's, on a shared machine, when they start from the defaults.

It is read when the person becomes known, not on every refetch of the user, or the refetch would put back a look
that is being changed at that moment.

## Decision 5: no Save button

A choice applies to the whole application at once, which is the live preview, and it is written to the account
600 ms after the last change, so one request covers a run of clicks. The page says when it has, and says plainly
when it could not: the look stays in this browser either way. The preview panel is the real components, because
the settings page itself has no table and no form, and those are what density and text size are for.

## Decision 6: charts are told, through one door

`setThemedOption` wraps every chart's `setOption`. It walks the finished option and scales each `fontSize` and
`borderRadius` by the setting, and sets the typeface. The charts' own numbers stay as design sizes at the default.
`useChartRevision` replaces each chart watching `isDark`: one signal for everything that makes a canvas stale,
including a typeface finishing its download, since a canvas draws with whatever is loaded at that moment.
`appearanceReach.spec.ts` reads the source and fails on a chart that calls `setOption` itself.

The first series follows the accent: the categorical colour nearest to it swaps with the first slot. No colour is
added, so a chart never holds two of nearly one hue, and the colours that carry meaning (added, removed, exposed)
stay where they are.

## Decision 7: typefaces travel in the image

Inter, IBM Plex Sans and Atkinson Hyperlegible, from `@fontsource-variable`, under the SIL Open Font License. MCI's
red line 1-3 row 4 forbids a font from outside, and the Content-Security-Policy would refuse one. A browser fetches a
face only when text uses it and each is split by script, so the default, the system font, downloads nothing, and
choosing Inter downloads 48 KB. `tokens.css` still says no web font by default, and that is still the case.

## Decision 8: animation is resolved once

Tailwind's `motion-safe:` and `motion-reduce:` read only the operating system, so a "Reduced" setting would have
changed nothing. They are redefined over `data-motion`, which is always `full`, `reduced` or `none`, because
`system` has been resolved by then. Nothing else may read `prefers-reduced-motion`; `appearanceReach.spec.ts` checks.
Reduced keeps the owner's rule of 2026-10-08: nothing moves or turns, fades stay. None stops everything, spinners
included, whose state is also in their words.

## Rejected

| Option | Why not |
|---|---|
| Scaling the root font size | Zooms everything, spacing included; the browser's zoom already does that |
| A free colour picker | Can fail contrast, can mean danger, cannot be tested |
| A column per setting | A migration per setting, for values nothing queries |
| Settings only in the browser | Would not follow a person, which was asked for |
| Settings only on the server | A flash of the defaults on every load |
| A Save button | The look already changed; a button would only add a way to lose it |
| Scaling the fifty font sizes in the charts by hand | Fifty places to forget, and the tenth chart would forget |

## Consequences

- A setting a person has not touched costs nothing: the default adds one attribute each and no download.
- `GET auth/me` carries the settings, so a sign-in needs no second request.
- A new chart has to use `setThemedOption`. A test says so.
- The palette's own colour-blind separation was measured for this and is not good enough in slots 7 and 8. That
  was already so; it is in `docs/backlog.md` rather than changed here, because it moves colours people know.

## How it was proved

See `frontend/tests/unit/lib/appearance.spec.ts`, `chart-theme.spec.ts`, `themeInit.spec.ts`, `appearanceReach.spec.ts`,
`frontend/tests/unit/design-system/accentContrast.spec.ts`, `frontend/tests/unit/features/settings/` and
`backend/tests/Sqm.Integration.Tests/AppearanceTests.cs`. Each bug below was put back on purpose and a test failed.

| Put back on purpose | Test that failed |
|---|---|
| What the account saved is ignored | `uses what the account saved over what this browser kept` |
| A shared machine hands one person's look to the next | `does not hand one person's look to the next on a shared machine` |
| What was just adopted is sent straight back | `does not send straight back what it has just been given` |
| Every change is sent at once | `sends one request for a run of quick changes` |
| The charts are not told | `tells the charts to read the tokens again` |
| The first paint disagrees with the module | four tests in `themeInit.spec.ts` |
| The theme kept the old way is not read before the first paint | `agrees on a theme kept the old way` |
| A chart sets its option itself | `sets its option through setThemedOption, which carries the typeface and the sizes` |
| A component reads the system's motion preference | `answers to the setting and not only to the operating system` |
| An accent that fails contrast | three contrast tests for that accent |
| An accent with one of its two blocks | `each have both of their blocks` |
| Charts keep their written sizes | `has every size scaled, however deep` |
| Charts keep a generic typeface | four tests in `chart-theme.spec.ts` |
| A typeface still downloading is named to a chart | `does not name a typeface still downloading, whose label widths would stick` |
| A fixed size in a stylesheet | `has no fixed size in a stylesheet or a utility, which would not grow with the setting` |
| The API accepts any value | `Anything_else_is_refused` and two more |
| One row read for everyone | `One_persons_settings_are_not_anothers`, `Someone_who_has_saved_nothing_is_told_so` |

One reinsertion failed nothing, and rightly: leaving a waiting save to fire after sign-out sends nothing,
because a save checks first whether anyone is signed in. The clearing is tidiness, not a guard.

Measured in a real browser on the laptop's real stack, signed in as a test account, on 2026-11-07:

| | Measured |
|---|---|
| The defaults | body 13px, line 19.5px, table cell padding 8px, field padding 6px: as before this existed |
| Larger, Relaxed, Comfortable, Round, Inter, Teal | body 15.08px, line 26.0px, cell padding 9.6px, field padding 7.2px |
| Choosing Inter | one file downloaded, its Latin subset, 48 KB |
| Animation None, then Reduced | transitions 0s; then the dialog's 200ms back, without movement |
| A second browser with nothing stored | the same settings, from the sign-in |
| Reset to defaults | the page and the account both back to the defaults |
| The largest settings on eight pages, at 1280 and 375 px wide | no horizontal scroll and nothing past the edge, real data |
| Figures | each typeface's digits are of one width, so columns of numbers stay aligned |

Trying it found one fault no test had: after choosing a typeface, "Samsung" was cut off at a chart's axis
until the next reload. zrender keeps each string's width by font name, and the chart had measured in the
fallback while the face downloaded. `setThemedOption` now names a face only once it is loaded, and the
appearance module redraws the charts at that moment; a test holds it.
