/**
 * Laying a report out for a copy that cannot scroll (ADR-016 §11).
 *
 * A dashboard card is a fixed box whose content scrolls inside it. An image or a printout cannot scroll, and
 * html-to-image makes it worse: it freezes every box of the page at the size it measured, so a scroll box that
 * fits its content exactly overflows by a fraction of a pixel in the copy and draws a scrollbar, which takes a
 * line of the table and the end of every right-aligned figure with it (2026-11-09: every template's image).
 *
 * So, before anything is captured:
 *  1. each card body grows until nothing inside it overflows - a body whose height came from the dashboard's
 *     grid keeps it unless its content needs more;
 *  2. every scroll box then stops scrolling, so a fraction of a pixel in the copy draws nothing;
 *  3. an image is split into pages by the heights the cards really have, not by an estimate.
 */

const SCROLLS: ReadonlySet<string> = new Set(['auto', 'scroll'])

/** How far, in CSS pixels, a body's content overflows it, counting every scroll box inside it. */
export function verticalOverflow(body: HTMLElement): number {
  let worst = Math.max(0, body.scrollHeight - body.clientHeight)
  for (const element of body.querySelectorAll<HTMLElement>('*')) {
    if (SCROLLS.has(getComputedStyle(element).overflowY)) {
      worst = Math.max(worst, element.scrollHeight - element.clientHeight)
    }
  }
  return worst
}

/**
 * Grows each card body of a report (`[data-report-body]`) until nothing in it overflows, then turns every scroll
 * box off. A few passes, because growing a body can let a chart beside a table take some of the room.
 */
export function fitReport(root: HTMLElement, passes = 6): void {
  for (const body of root.querySelectorAll<HTMLElement>('[data-report-body]')) {
    for (let pass = 0; pass < passes; pass++) {
      const need = verticalOverflow(body)
      if (need < 1) break
      body.style.height = `${Math.ceil(body.getBoundingClientRect().height + need)}px`
    }
  }

  for (const element of root.querySelectorAll<HTMLElement>('*')) {
    const style = getComputedStyle(element)
    if (SCROLLS.has(style.overflowY) || SCROLLS.has(style.overflowX)) element.style.overflow = 'visible'
  }
}

/** One row of a report's grid: the cards side by side in it, and its height. */
export interface ReportRow {
  ids: string[]
  height: number
}

/** The rows of a rendered report, from where its cards (`[data-widget-id]`) really are. */
export function measureRows(report: HTMLElement): ReportRow[] {
  const rows = new Map<number, ReportRow>()
  for (const card of report.querySelectorAll<HTMLElement>('[data-widget-id]')) {
    const top = Math.round(card.offsetTop)
    const row = rows.get(top) ?? { ids: [], height: 0 }
    row.ids.push(card.dataset['widgetId']!)
    row.height = Math.max(row.height, card.getBoundingClientRect().height)
    rows.set(top, row)
  }
  return [...rows.entries()].sort(([a], [b]) => a - b).map(([, row]) => row)
}

/**
 * Splits rows into pages none taller than `max` CSS pixels, each page carrying the report's header. A row is
 * never split; a single row taller than `max` is a page of its own.
 */
export function splitRows(rows: ReportRow[], header: number, gap: number, max: number): string[][] {
  const pages: string[][] = []
  let page: string[] = []
  let height = header
  for (const row of rows) {
    const added = (page.length ? gap : 0) + row.height
    if (page.length && height + added > max) {
      pages.push(page)
      page = []
      height = header
    }
    height += (page.length ? gap : 0) + row.height
    page.push(...row.ids)
  }
  if (page.length) pages.push(page)
  return pages
}

/**
 * What a dashboard's CSV download held, said plainly: never "one CSV per widget" when some were left out -
 * a widget the person may not use, or one whose source was not ready (the server's X-Export-* headers).
 */
export function csvMessage(asked: number, included: number | null, requested: number | null): string {
  if (asked === 1) return 'The CSV was downloaded.'
  if (included !== null && requested !== null && included < requested) {
    return `A ZIP with ${included} CSV ${included === 1 ? 'file' : 'files'} for ${requested} widgets was downloaded; about.txt says which were left out and why.`
  }
  return 'A ZIP with one CSV per widget was downloaded; about.txt says what each file is.'
}
