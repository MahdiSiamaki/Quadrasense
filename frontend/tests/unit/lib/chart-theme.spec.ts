import { afterEach, describe, expect, it, vi } from 'vitest'
import { themed } from '@/lib/chart-theme'

/* A chart is a canvas, so what CSS does for everything else has to be handed to it. */

const root = document.documentElement

afterEach(() => {
  root.style.removeProperty('--text-scale')
  root.style.removeProperty('--radius-md')
  root.style.removeProperty('--font-sans')
})

function set(properties: Record<string, string>) {
  for (const [name, value] of Object.entries(properties)) root.style.setProperty(name, value)
}

describe('a chart option', () => {
  it('is left at its written sizes at the default setting', () => {
    set({ '--text-scale': '1' })

    const option = themed({ xAxis: { axisLabel: { fontSize: 11 } } })

    expect(option.xAxis.axisLabel.fontSize).toBe(11)
  })

  it('has every size scaled, however deep', () => {
    set({ '--text-scale': '1.16' })

    const option = themed({
      xAxis: { axisLabel: { fontSize: 11 } },
      series: [{ label: { fontSize: 12 }, markLine: { label: { fontSize: 10 } } }],
    })

    expect(option.xAxis.axisLabel.fontSize).toBe(12.8)
    expect(option.series[0]!.label.fontSize).toBe(13.9)
    expect(option.series[0]!.markLine.label.fontSize).toBe(11.6)
  })

  it('takes the person\'s typeface as the default for all its text', () => {
    set({ '--font-sans': "'Inter Variable', sans-serif" })

    expect(themed({}).textStyle).toMatchObject({ fontFamily: "'Inter Variable', sans-serif" })
  })

  it('does not name a typeface still downloading, whose label widths would stick', () => {
    set({ '--font-sans': "'Inter Variable', ui-sans-serif, Arial" })
    const check = vi.fn(() => false)
    Object.defineProperty(document, 'fonts', { value: { check }, configurable: true })

    expect(themed({}).textStyle).toEqual({ fontFamily: 'ui-sans-serif, Arial' })
    expect(check).toHaveBeenCalledWith('12px "Inter Variable"')

    Reflect.deleteProperty(document, 'fonts')
  })

  it('names it once it has arrived', () => {
    set({ '--font-sans': "'Inter Variable', ui-sans-serif, Arial" })
    Object.defineProperty(document, 'fonts', { value: { check: () => true }, configurable: true })

    expect(themed({}).textStyle).toEqual({ fontFamily: "'Inter Variable', ui-sans-serif, Arial" })

    Reflect.deleteProperty(document, 'fonts')
  })

  it('keeps a text style of its own', () => {
    set({ '--font-sans': 'serif' })

    const option = themed({ textStyle: { color: 'red' } })

    expect(option.textStyle).toEqual({ color: 'red', fontFamily: 'serif' })
  })

  it('follows the corners, a number or one for each corner', () => {
    // 0.75rem at a 16px root is twice the default 6px.
    set({ '--radius-md': '0.75rem' })

    const option = themed({
      series: [{ itemStyle: { borderRadius: 3 } }, { itemStyle: { borderRadius: [0, 3, 3, 0] } }],
    })

    expect(option.series[0]!.itemStyle.borderRadius).toBe(6)
    expect(option.series[1]!.itemStyle.borderRadius).toEqual([0, 6, 6, 0])
  })

  it('does not walk a series of plain numbers, which is most of what a chart holds', () => {
    const data = [1, 2, 3]

    const option = themed({ series: [{ data }] })

    expect(option.series[0]!.data).toBe(data)
  })

  it('leaves a formatter function alone', () => {
    const formatter = () => 'x'

    const option = themed({ tooltip: { formatter } })

    expect(option.tooltip.formatter).toBe(formatter)
  })

  it('does not change the option it was given', () => {
    set({ '--text-scale': '2' })
    const original = { xAxis: { axisLabel: { fontSize: 10 } } }

    themed(original)

    expect(original.xAxis.axisLabel.fontSize).toBe(10)
  })
})
