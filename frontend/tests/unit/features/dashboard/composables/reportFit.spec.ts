import { afterEach, describe, expect, it } from 'vitest'
import { csvMessage, fitReport, measureRows, splitRows, verticalOverflow } from '@/features/dashboard/composables/reportFit'

/*
  happy-dom lays nothing out, so each test gives the elements the measurements a browser would: what the
  functions do with them is the rule under test. That they hold in a real layout is proven by measuring real
  exports (ADR-016 §11, 2026-11-09).
*/

function measured(element: HTMLElement, sizes: { scrollHeight?: number; clientHeight?: number; height?: number; top?: number }) {
  if (sizes.scrollHeight !== undefined) Object.defineProperty(element, 'scrollHeight', { configurable: true, get: () => sizes.scrollHeight })
  if (sizes.clientHeight !== undefined) Object.defineProperty(element, 'clientHeight', { configurable: true, get: () => sizes.clientHeight })
  if (sizes.top !== undefined) Object.defineProperty(element, 'offsetTop', { configurable: true, get: () => sizes.top })
  if (sizes.height !== undefined) element.getBoundingClientRect = () => ({ height: sizes.height } as DOMRect)
  return element
}

afterEach(() => {
  document.body.innerHTML = ''
})

describe('fitting a report for a copy that cannot scroll', () => {
  it('grows a card body by what the table scrolling inside it hides', () => {
    document.body.innerHTML =
      // overflow-y, not the shorthand: happy-dom does not expand it, a browser does.
      '<div id="r"><div data-report-body style="height: 300px"><div class="table" style="overflow-y: auto"></div></div></div>'
    const body = document.querySelector<HTMLElement>('[data-report-body]')!
    const table = document.querySelector<HTMLElement>('.table')!
    // As a flex layout would: the body is as tall as its style says, and the table gets what the body gains.
    const bodyHeight = () => Number.parseFloat(body.style.height)
    Object.defineProperty(body, 'scrollHeight', { configurable: true, get: bodyHeight })
    Object.defineProperty(body, 'clientHeight', { configurable: true, get: bodyHeight })
    body.getBoundingClientRect = () => ({ height: bodyHeight() }) as DOMRect
    Object.defineProperty(table, 'scrollHeight', { configurable: true, get: () => 260 })
    Object.defineProperty(table, 'clientHeight', { configurable: true, get: () => 200 + (bodyHeight() - 300) })

    expect(verticalOverflow(body)).toBe(60)
    fitReport(document.getElementById('r')!)

    expect(body.style.height).toBe('360px')
    expect(verticalOverflow(body)).toBe(0)
  })

  it('turns every scroll box off, so the copy draws no scrollbar for a fraction of a pixel', () => {
    document.body.innerHTML =
      '<div id="r"><div data-report-body><div class="a" style="overflow-y: auto"></div><div class="b" style="overflow: hidden"></div></div></div>'
    measured(document.querySelector<HTMLElement>('[data-report-body]')!, { scrollHeight: 100, clientHeight: 100, height: 100 })

    fitReport(document.getElementById('r')!)

    expect(document.querySelector<HTMLElement>('.a')!.style.overflow).toBe('visible')
    // Clipping is left alone: it draws no scrollbar, and a rounded card or a fill bar needs it.
    expect(document.querySelector<HTMLElement>('.b')!.style.overflow).toBe('hidden')
  })

  it('leaves a body alone when nothing in it overflows', () => {
    document.body.innerHTML = '<div id="r"><div data-report-body style="height: 400px"></div></div>'
    measured(document.querySelector<HTMLElement>('[data-report-body]')!, { scrollHeight: 400, clientHeight: 400, height: 400 })

    fitReport(document.getElementById('r')!)

    expect(document.querySelector<HTMLElement>('[data-report-body]')!.style.height).toBe('400px')
  })
})

describe('cutting an image into pages', () => {
  it('reads the rows from where the cards really are, a row as tall as its tallest card', () => {
    document.body.innerHTML =
      '<div id="r"><section data-widget-id="a"></section><section data-widget-id="b"></section><section data-widget-id="c"></section></div>'
    measured(document.querySelector<HTMLElement>('[data-widget-id="a"]')!, { top: 0, height: 300 })
    measured(document.querySelector<HTMLElement>('[data-widget-id="b"]')!, { top: 0, height: 520 })
    measured(document.querySelector<HTMLElement>('[data-widget-id="c"]')!, { top: 536, height: 200 })

    expect(measureRows(document.getElementById('r')!)).toEqual([
      { ids: ['a', 'b'], height: 520 },
      { ids: ['c'], height: 200 },
    ])
  })

  it('starts a page before a row that would take it past the limit, never splitting a row', () => {
    const rows = [
      { ids: ['a'], height: 3000 },
      { ids: ['b', 'c'], height: 3000 },
      { ids: ['d'], height: 3000 },
      { ids: ['e'], height: 9000 },
    ]

    expect(splitRows(rows, 200, 16, 7000)).toEqual([['a', 'b', 'c'], ['d'], ['e']])
  })

  it('keeps a short report on one page', () => {
    expect(splitRows([{ ids: ['a'], height: 400 }, { ids: ['b'], height: 400 }], 200, 16, 7000)).toEqual([['a', 'b']])
  })
})

describe('what a CSV download says it held', () => {
  it('never claims a file per widget when some were left out', () => {
    expect(csvMessage(3, 2, 3)).toBe('A ZIP with 2 CSV files for 3 widgets was downloaded; about.txt says which were left out and why.')
    expect(csvMessage(3, 3, 3)).toBe('A ZIP with one CSV per widget was downloaded; about.txt says what each file is.')
    expect(csvMessage(1, 1, 1)).toBe('The CSV was downloaded.')
  })
})
