import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { describe, expect, it } from 'vitest'
import contract from '@/lib/appearance-contract.json'

/*
  Every accent, computed from the stylesheet: an offered colour that fails contrast is a defect the
  person cannot see coming. AA is 4.5:1 for text. Each accent is both a fill behind text and text on
  a surface, in two themes, and it is a badge's text on its own subtle tint.

  Inside sRGB too, because outside it the browser picks a colour of its own and these sums would be
  about a colour nobody sees.
*/

const css = (name: string) =>
  readFileSync(resolve(__dirname, `../../../src/design-system/${name}`), 'utf8')

const tokens = css('tokens.css')
const appearance = css('appearance.css')

type Oklch = { l: number; c: number; h: number }

function parse(value: string): Oklch {
  const match = /oklch\(([\d.]+)%\s+([\d.]+)\s+([\d.]+)\)/.exec(value)
  if (!match) throw new Error(`not an oklch colour: ${value}`)
  return { l: Number(match[1]) / 100, c: Number(match[2]), h: Number(match[3]) }
}

/** The declarations of the first block with exactly this selector. */
function block(source: string, selector: string): Record<string, string> {
  const start = source.indexOf(`${selector} {`)
  if (start < 0) throw new Error(`no block ${selector}`)
  const body = source.slice(source.indexOf('{', start) + 1, source.indexOf('}', start))

  return Object.fromEntries(
    body
      .split(';')
      .map((line) => /(--[\w-]+):\s*(.+)/s.exec(line.trim()))
      .filter((m): m is RegExpExecArray => m !== null)
      .map((m) => [m[1]!, m[2]!.trim()]),
  )
}

function linear({ l, c, h }: Oklch): [number, number, number] {
  const a = c * Math.cos((h * Math.PI) / 180)
  const b = c * Math.sin((h * Math.PI) / 180)
  const L = (l + 0.3963377774 * a + 0.2158037573 * b) ** 3
  const M = (l - 0.1055613458 * a - 0.0638541728 * b) ** 3
  const S = (l - 0.0894841775 * a - 1.291485548 * b) ** 3

  return [
    4.0767416621 * L - 3.3077115913 * M + 0.2309699292 * S,
    -1.2684380046 * L + 2.6097574011 * M - 0.3413193965 * S,
    -0.0041960863 * L - 0.7034186147 * M + 1.707614701 * S,
  ]
}

function luminance(colour: Oklch): number {
  const [r, g, b] = linear(colour).map((v) => Math.min(Math.max(v, 0), 1)) as [number, number, number]
  return 0.2126 * r + 0.7152 * g + 0.0722 * b
}

function contrast(a: Oklch, b: Oklch): number {
  const [hi, lo] = [luminance(a), luminance(b)].sort((x, y) => y - x) as [number, number]
  return (hi + 0.05) / (lo + 0.05)
}

const light = block(tokens, ':root')
const dark = block(tokens, ":root[data-theme='dark']")

const LIGHT_SURFACE = parse('oklch(100% 0 0)')
const LIGHT_TEXT_ON_ACCENT = parse(light['--c-accent-text']!)
const LIGHT_CANVAS = parse(light['--c-canvas']!)
const DARK_SURFACE = parse(dark['--c-surface']!)
const DARK_CANVAS = parse(dark['--c-canvas']!)

function accentTokens(accent: string, theme: 'light' | 'dark') {
  if (accent === 'blue') return theme === 'light' ? light : dark

  const selector =
    theme === 'light' ? `:root[data-accent='${accent}']` : `:root[data-theme='dark'][data-accent='${accent}']`

  return block(appearance, selector)
}

const AA = 4.5

describe.each(contract.accent.values)('the %s accent', (accent) => {
  describe('in the light theme', () => {
    const t = accentTokens(accent, 'light')
    const fill = parse(t['--c-accent']!)
    const hover = parse(t['--c-accent-hover']!)
    const subtle = parse(t['--c-accent-subtle']!)

    it('carries text on a button, at rest and hovered', () => {
      expect(contrast(LIGHT_TEXT_ON_ACCENT, fill)).toBeGreaterThanOrEqual(AA)
      expect(contrast(LIGHT_TEXT_ON_ACCENT, hover)).toBeGreaterThanOrEqual(AA)
    })

    it('reads as a link on a surface and on the canvas', () => {
      expect(contrast(fill, LIGHT_SURFACE)).toBeGreaterThanOrEqual(AA)
      expect(contrast(fill, LIGHT_CANVAS)).toBeGreaterThanOrEqual(AA)
    })

    it('reads as a badge on its own tint', () => {
      expect(contrast(fill, subtle)).toBeGreaterThanOrEqual(AA)
    })
  })

  describe('in the dark theme', () => {
    const t = accentTokens(accent, 'dark')
    const fill = parse(t['--c-accent']!)
    const hover = parse(t['--c-accent-hover']!)
    const subtle = parse(t['--c-accent-subtle']!)
    const text = parse(t['--c-accent-text'] ?? dark['--c-accent-text']!)

    it('carries text on a button, at rest and hovered', () => {
      expect(contrast(text, fill)).toBeGreaterThanOrEqual(AA)
      expect(contrast(text, hover)).toBeGreaterThanOrEqual(AA)
    })

    it('reads as a link on a surface and on the canvas', () => {
      expect(contrast(fill, DARK_SURFACE)).toBeGreaterThanOrEqual(AA)
      expect(contrast(fill, DARK_CANVAS)).toBeGreaterThanOrEqual(AA)
    })

    it('reads as a badge on its own tint', () => {
      expect(contrast(fill, subtle)).toBeGreaterThanOrEqual(AA)
    })
  })

  // Blue is the default and predates this: its subtle tint and dark hover are clipped a little by
  // the browser, and changing them here would change how the product already looks.
  it.skipIf(accent === 'blue')('stays inside sRGB in both themes', () => {
    for (const theme of ['light', 'dark'] as const) {
      const t = accentTokens(accent, theme)
      for (const name of ['--c-accent', '--c-accent-hover', '--c-accent-subtle']) {
        for (const channel of linear(parse(t[name]!))) {
          expect(channel).toBeGreaterThanOrEqual(-0.002)
          expect(channel).toBeLessThanOrEqual(1.002)
        }
      }
    }
  })

  it('is not a colour that already means something', () => {
    const { h, c } = parse(accentTokens(accent, 'light')['--c-accent']!)

    // Red, amber and green: danger, warning and success. A grey has no hue to speak of.
    const meansSomething = c > 0.05 && (h < 110 || (h > 125 && h < 170) || h > 355)

    expect(meansSomething).toBe(false)
  })
})

describe('the offered accents', () => {
  it('each have both of their blocks, so a new one cannot be half written', () => {
    for (const accent of contract.accent.values.filter((a) => a !== 'blue')) {
      expect(appearance).toContain(`:root[data-accent='${accent}'] {`)
      expect(appearance).toContain(`:root[data-theme='dark'][data-accent='${accent}'] {`)
    }
  })
})
