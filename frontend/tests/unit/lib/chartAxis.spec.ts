import { describe, expect, it } from 'vitest'
import { edgeDateLabels } from '@/lib/chart-axis'

/*
  A date axis that aligns its end labels must also keep them (lib/chart-axis.ts). Aligned but not kept, the
  last day's label is the one ECharts drops when it overlaps its neighbour, and the neighbour - centred a few
  days from the end - runs off the canvas: the churn chart of a 268-day PNG export, 2026-11-09. Found in the
  sources, because the test DOM has no canvas to measure.
*/
const vue = import.meta.glob('/src/**/*.vue', { query: '?raw', import: 'default', eager: true }) as Record<string, string>

describe('the dates under a time series', () => {
  it('keep the first and last day as labels, aligned inside the plot', () => {
    expect(edgeDateLabels).toMatchObject({ showMinLabel: true, showMaxLabel: true, alignMinLabel: 'left', alignMaxLabel: 'right' })
  })

  it('come from the shared setting in every chart, never an alignment alone', () => {
    const aligned = Object.entries(vue).filter(([, source]) => /alignMaxLabel|alignMinLabel/.test(source))
    expect(aligned.map(([file]) => file)).toEqual([])
    expect(Object.values(vue).filter((source) => source.includes('...edgeDateLabels')).length).toBeGreaterThanOrEqual(6)
  })
})
