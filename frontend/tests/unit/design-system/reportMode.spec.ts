import { describe, expect, it, vi } from 'vitest'
import { defineComponent, h, nextTick } from 'vue'
import { mount } from '@vue/test-utils'
import ChangeTimeSeries from '@/design-system/ChangeTimeSeries.vue'
import BarChart from '@/design-system/BarChart.vue'
import { provideReportMode } from '@/lib/report-mode'
import type { DailyChange } from '@/api/dashboard'

/*
  A chart in an export is copied, not used (ADR-016 §11). The PNG of 2026-11-09 showed the daily chart opened
  on its last third with a zoom slider drawn under it, while its caption said every day of the range; and every
  canvas was drawn at the screen's pixel ratio, then blown up to the image's.
*/

const echarts = vi.hoisted(() => ({ inits: [] as Array<Record<string, unknown>>, options: [] as Array<Record<string, unknown>> }))

vi.mock('echarts/core', () => ({
  use: () => {},
  init: (_element: unknown, _theme: unknown, options: Record<string, unknown>) => {
    echarts.inits.push(options)
    return {
      setOption: (option: Record<string, unknown>) => echarts.options.push(option),
      on: () => {},
      resize: () => {},
      dispose: () => {},
    }
  },
}))

const InReport = defineComponent({
  setup(_, { slots }) {
    provideReportMode({ pixelRatio: 2 })
    return () => slots.default?.()
  },
})

const days: DailyChange[] = ['2026-10-29', '2026-10-30', '2026-10-31'].map((date, i) => ({
  date, added: 100 + i, removed: 90, net: 10 + i, cumulative: 1000 + i, unknownDeviceRows: 0,
}) as DailyChange)

async function draw(inReport: boolean) {
  echarts.inits.length = 0
  echarts.options.length = 0
  const chart = () => h(ChangeTimeSeries, { data: days, zoomStart: 66 })
  mount(inReport ? InReport : chart, inReport ? { slots: { default: chart } } : {})
  await nextTick()
  return { init: echarts.inits[0]!, option: echarts.options.at(-1)! }
}

describe('a chart drawn into an export', () => {
  it('shows every day it was given: no zoom window and no slider', async () => {
    const { option } = await draw(true)

    expect(option['dataZoom']).toEqual([])
    expect((option['grid'] as { bottom: number }).bottom).toBe(24)
  })

  it('draws its canvas at the export\'s pixel ratio, not the screen\'s', async () => {
    const { init } = await draw(true)

    expect(init['devicePixelRatio']).toBe(2)
  })

  it('on a live page keeps its zoom, slider and the screen\'s ratio', async () => {
    const { init, option } = await draw(false)

    expect((option['dataZoom'] as unknown[]).length).toBe(2)
    expect(init['devicePixelRatio']).toBeUndefined()
  })

  it('holds for every chart, a bar chart too', async () => {
    echarts.inits.length = 0
    mount(InReport, { slots: { default: () => h(BarChart, { data: [{ key: 'Samsung', count: 10, percent: 50 }], height: 'auto' }) } })
    await nextTick()

    expect(echarts.inits[0]!['devicePixelRatio']).toBe(2)
  })

  it('is asked for by every chart in the product, so a new one cannot miss it', () => {
    const vue = import.meta.glob('/src/**/*.vue', { query: '?raw', import: 'default', eager: true }) as Record<string, string>
    const charts = Object.entries(vue).filter(([, source]) => source.includes('echarts.init('))

    expect(charts.length).toBeGreaterThanOrEqual(9)
    expect(charts.filter(([, source]) => !source.includes('useReportMode()') || !source.includes('reportInitOptions(')).map(([file]) => file)).toEqual([])
    // A chart that zooms on the page shows every day in a report.
    const zooming = charts.filter(([, source]) => /^\s+dataZoom:/m.test(source))
    expect(zooming.length).toBeGreaterThanOrEqual(4)
    expect(zooming.filter(([, source]) => !/dataZoom: report \? \[\] :/.test(source)).map(([file]) => file)).toEqual([])
  })
})
