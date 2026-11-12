import { describe, expect, it } from 'vitest'

/*
  A table that scrolls sideways inside its own box must not widen the page.

  Tailwind's `sr-only` is `position: absolute`. An absolutely positioned element is clipped by an
  overflow box only when that box, or something inside it, is its containing block - so a hidden
  "Actions" header in a table whose scroll box is not positioned is laid out at its own place past
  the table's end, outside the box, and the page grows to reach it. Measured at a 375px viewport on
  2026-11-12: the API clients page 456px wide, Campaign lists 483px, and the Explorer's saved
  queries 505px once a query was named `dual_sim_handsets_by_tac_and_vendor` (375px with a short
  name, so whether it shows depends on the data). With the scroll box `relative`, all three: 375px.

  Found in the sources because the test DOM does no layout: every scroll box that holds a table
  with a visually hidden element in it is positioned.
*/
const vue = import.meta.glob('/src/**/*.vue', { query: '?raw', import: 'default', eager: true }) as Record<string, string>

/** The class list of each element whose class names a sideways scroll, and the table it holds. */
function scrollBoxesWithTables(source: string) {
  const boxes: Array<{ classes: string; table: string; line: number }> = []
  const opening = /<div\b[^>]*\bclass="([^"]*\boverflow-(?:x-)?(?:auto|scroll)\b[^"]*)"[^>]*>/g

  for (const match of source.matchAll(opening)) {
    // The box's own content, up to the first table's end: a table that starts right inside it.
    const after = source.slice(match.index + match[0].length)
    const next = after.replace(/^\s*(<!--[\s\S]*?-->\s*)*/, '')
    if (!next.startsWith('<table')) continue

    const end = next.indexOf('</table>')
    boxes.push({
      classes: match[1]!,
      table: end === -1 ? next : next.slice(0, end),
      line: source.slice(0, match.index).split('\n').length,
    })
  }

  return boxes
}

/** Positioned, or contained: paint or layout containment makes the box the containing block too. */
const positioned = (classes: string) =>
  /(^|\s)(relative|absolute|fixed|sticky|contain-(paint|layout|strict|content))(\s|$)/.test(classes)

describe('a table that scrolls sideways', () => {
  it('keeps what it hides inside its box, so the page does not widen', () => {
    const escaping = Object.entries(vue).flatMap(([file, source]) =>
      scrollBoxesWithTables(source)
        .filter((box) => /\bsr-only\b/.test(box.table) && !positioned(box.classes))
        .map((box) => `${file}:${box.line}`),
    )

    expect(escaping).toEqual([])
  })

  it('finds the tables it is about', () => {
    // A guard that matches nothing guards nothing: these hold hidden header labels today.
    const holding = Object.entries(vue)
      .filter(([, source]) => scrollBoxesWithTables(source).some((box) => /\bsr-only\b/.test(box.table)))
      .map(([file]) => file)

    expect(holding).toEqual(
      expect.arrayContaining([
        '/src/features/explorer/SavedQueriesPanel.vue',
        '/src/features/integration/IntegrationClientsPage.vue',
        '/src/features/planning/CampaignListsPage.vue',
      ]),
    )
  })
})
