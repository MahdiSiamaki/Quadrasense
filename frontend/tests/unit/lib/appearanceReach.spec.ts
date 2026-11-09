import { readdirSync, readFileSync, statSync } from 'node:fs'
import { join, relative, resolve } from 'node:path'
import { describe, expect, it } from 'vitest'

/*
  "Applied on every page and every chart" is a claim about all the code, including what has not been
  written yet. These read the source, as wheelTraps.spec.ts does, so that the next chart or the next
  animation cannot quietly step outside the settings.
*/

const SRC = resolve(__dirname, '../../../src')

function sources(dir: string): string[] {
  return readdirSync(dir).flatMap((name) => {
    const path = join(dir, name)
    if (statSync(path).isDirectory()) return sources(path)
    return /\.(vue|ts|css)$/.test(name) ? [path] : []
  })
}

const files = sources(SRC).map((path) => ({
  name: relative(SRC, path).replaceAll('\\', '/'),
  text: readFileSync(path, 'utf8'),
}))

/** Comments say what these rules are, and must not trip them. */
function code(text: string): string {
  return text.replace(/\/\*[\s\S]*?\*\//g, '').replace(/^\s*\/\/.*$/gm, '').replace(/<!--[\s\S]*?-->/g, '')
}

describe('every chart', () => {
  it('sets its option through setThemedOption, which carries the typeface and the sizes', () => {
    const direct = files.filter(
      (f) => f.name !== 'lib/chart-theme.ts' && /\.setOption\(/.test(code(f.text)),
    )

    expect(direct.map((f) => f.name)).toEqual([])
  })

  it('redraws on the chart revision, not on the theme alone', () => {
    const charts = files.filter((f) => f.text.includes('setThemedOption('))
      .filter((f) => f.name !== 'lib/chart-theme.ts')

    expect(charts.length).toBeGreaterThan(0)

    for (const chart of charts) {
      expect(chart.text, chart.name).toContain('watch(revision')
    }
  })
})

describe('every animation', () => {
  it('answers to the setting and not only to the operating system', () => {
    // lib/appearance.ts reads the system's preference, to resolve "system". Nothing else may.
    const direct = files.filter(
      (f) => f.name !== 'lib/appearance.ts' && /prefers-reduced-motion/.test(code(f.text)),
    )

    expect(direct.map((f) => f.name)).toEqual([])
  })
})

describe('the type scale', () => {
  it('has no fixed size in a stylesheet or a utility, which would not grow with the setting', () => {
    const fixed = files.filter((f) =>
      /text-\[[\d.]+(px|rem|em)\]/.test(code(f.text)) ||
      /(^|[\s;{])font-size:\s*[\d.]+(px|rem|em)/m.test(code(f.text)),
    )

    expect(fixed.map((f) => f.name)).toEqual([])
  })
})
