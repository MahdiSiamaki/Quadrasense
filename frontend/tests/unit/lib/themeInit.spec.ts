import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { beforeEach, describe, expect, it, vi } from 'vitest'

/*
  public/theme-init.js runs before the bundle, so the first paint is right, and it repeats what
  lib/appearance.ts does. They must agree: if they differ, a page paints one way and jumps to the
  other when the module loads, on every load. This runs both on the same settings and compares.
*/

vi.mock('@/api/client', () => ({ api: {}, ApiError: class extends Error {} }))

const script = readFileSync(resolve(__dirname, '../../../public/theme-init.js'), 'utf8')
const root = document.documentElement

function media(matches: Record<string, boolean>) {
  window.matchMedia = ((query: string) => ({
    matches: matches[query] ?? false,
    addEventListener: () => undefined,
  })) as unknown as typeof window.matchMedia
}

function attributes() {
  return { ...root.dataset }
}

function clear() {
  for (const key of Object.keys(root.dataset)) delete root.dataset[key]
}

function runInit() {
  clear()
  new Function(script)()
  return attributes()
}

async function runModule() {
  clear()
  vi.resetModules()
  await import('@/lib/appearance')
  return attributes()
}

beforeEach(() => {
  localStorage.clear()
  media({})
})

describe('the script before the first paint', () => {
  it('agrees with the module on a person who has chosen nothing', async () => {
    expect(runInit()).toEqual(await runModule())
  })

  it('agrees on a person who has chosen everything', async () => {
    localStorage.setItem(
      'sqm.appearance',
      JSON.stringify({
        theme: 'dark',
        fontFamily: 'inter',
        fontSize: 'larger',
        fontWeight: 'medium',
        lineSpacing: 'relaxed',
        density: 'comfortable',
        accent: 'teal',
        corners: 'round',
        motion: 'reduced',
      }),
    )

    expect(runInit()).toEqual(await runModule())
  })

  it('agrees on a theme kept the old way', async () => {
    localStorage.setItem('sqm.theme', 'dark')

    expect(runInit()).toEqual(await runModule())
  })

  it('agrees on what follows the system', async () => {
    media({ '(prefers-color-scheme: dark)': true, '(prefers-reduced-motion: reduce)': true })

    const first = runInit()

    expect(first).toEqual(await runModule())
    expect(first).toMatchObject({ theme: 'dark', motion: 'reduced' })
  })

  it('still sets the theme when storage is unreadable', () => {
    const getItem = vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('denied')
    })

    expect(runInit()).toMatchObject({ theme: 'light', accent: 'blue' })

    getItem.mockRestore()
  })
})
