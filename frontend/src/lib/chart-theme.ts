/**
 * Makes a chart's option answer to the person's appearance settings.
 *
 * A chart is a canvas: CSS does not reach it, so the type scale, the typeface and the corners have
 * to be handed over. Every chart wrote its label sizes as plain numbers, 9 to 12, which is right at
 * the default setting and is the base this scales from. Doing it here, once, over the finished
 * option means the tenth chart cannot forget, and a chart's own numbers stay readable as design
 * sizes. Colours are not touched here: those go through `chartColor`, which reads the tokens.
 */

import type { ECharts, EChartsCoreOption } from 'echarts/core'

const DEFAULT_RADIUS_MD_PX = 6

function token(name: string): string {
  return getComputedStyle(document.documentElement).getPropertyValue(name).trim()
}

function rootFontSize(): number {
  return parseFloat(getComputedStyle(document.documentElement).fontSize) || 16
}

/** How far the corners have moved from the default: `--radius-md` is in rem. */
function radiusFactor(): number {
  const rem = parseFloat(token('--radius-md'))
  return Number.isFinite(rem) ? (rem * rootFontSize()) / DEFAULT_RADIUS_MD_PX : 1
}

function textFactor(): number {
  const scale = parseFloat(token('--text-scale'))
  return Number.isFinite(scale) && scale > 0 ? scale : 1
}

/**
 * The typeface to draw in: the person's, once it has arrived.
 *
 * zrender keeps the width of every string by font name for good. A chart drawn while a face was
 * still downloading measured its labels in the fallback, under the new name, and those widths then
 * stayed: "Samsung" was cut off at the axis until the next reload. So a face is not named to a chart
 * until it is loaded, and `lib/appearance.ts` redraws the charts at that moment.
 */
function fontFamily(): string {
  const stack = token('--font-sans') || 'sans-serif'
  const first = /^['"]?([^,'"]+)/.exec(stack)?.[1]?.trim()

  if (!first || typeof document.fonts?.check !== 'function') return stack

  return document.fonts.check(`12px "${first}"`) ? stack : stack.slice(stack.indexOf(',') + 1).trim()
}

function adjust(value: unknown, text: number, radius: number): unknown {
  if (Array.isArray(value)) {
    // A series' `data` is thousands of numbers, and a number here is never a size.
    if (typeof value[0] !== 'object' || value[0] === null) return value
    return value.map((item) => adjust(item, text, radius))
  }

  if (value === null || typeof value !== 'object') return value

  const result: Record<string, unknown> = {}

  for (const [key, field] of Object.entries(value)) {
    if (key === 'fontSize' && typeof field === 'number') {
      result[key] = Math.round(field * text * 10) / 10
    } else if (key === 'borderRadius' && typeof field === 'number') {
      result[key] = Math.round(field * radius * 10) / 10
    } else if (key === 'borderRadius' && Array.isArray(field)) {
      result[key] = field.map((corner) =>
        typeof corner === 'number' ? Math.round(corner * radius * 10) / 10 : corner,
      )
    } else {
      result[key] = adjust(field, text, radius)
    }
  }

  return result
}

/** Every chart sets its option through this. */
export function setThemedOption(
  chart: ECharts,
  option: EChartsCoreOption,
  settings?: { notMerge?: boolean },
): void {
  chart.setOption(themed(option), settings)
}

/** The option, in the person's typeface and at their size. */
export function themed<T extends object>(option: T): T {
  const adjusted = adjust(option, textFactor(), radiusFactor()) as T & { textStyle?: object }

  return {
    ...adjusted,
    textStyle: {
      fontFamily: fontFamily(),
      ...adjusted.textStyle,
    },
  }
}
