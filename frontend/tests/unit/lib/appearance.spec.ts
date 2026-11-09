import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { nextTick } from 'vue'
import contract from '@/lib/appearance-contract.json'

/*
  The settings are module state, as the theme always was, so each test imports a fresh copy after
  arranging what is in the browser. What it writes onto <html> and sends to the API is what matters.
*/

const put = vi.fn()

vi.mock('@/api/client', () => ({
  api: { put: (...args: unknown[]) => put(...args) },
  ApiError: class ApiError extends Error {
    status = 500
  },
}))

function media(matches: Record<string, boolean>) {
  window.matchMedia = ((query: string) => ({
    matches: matches[query] ?? false,
    addEventListener: () => undefined,
  })) as unknown as typeof window.matchMedia
}

async function fresh() {
  vi.resetModules()
  return await import('@/lib/appearance')
}

const root = document.documentElement

beforeEach(() => {
  localStorage.clear()
  put.mockReset()
  put.mockResolvedValue(undefined)
  for (const key of Object.keys(root.dataset)) delete root.dataset[key]
  media({})
})

afterEach(() => {
  vi.useRealTimers()
})

describe('what a person starts with', () => {
  it('is the contract\'s defaults', async () => {
    const { DEFAULTS, useAppearance } = await fresh()

    expect(useAppearance().appearance).toEqual(DEFAULTS)
    expect(DEFAULTS.theme).toBe(contract.theme.default)
    expect(useAppearance().isDefault.value).toBe(true)
  })

  it('keeps a theme chosen before there was more to choose', async () => {
    localStorage.setItem('sqm.theme', 'dark')

    const { useAppearance } = await fresh()

    expect(useAppearance().appearance.theme).toBe('dark')
    expect(root.dataset['theme']).toBe('dark')
  })

  it('reads what this browser kept', async () => {
    localStorage.setItem('sqm.appearance', JSON.stringify({ accent: 'teal', fontSize: 'large' }))

    const { useAppearance } = await fresh()

    expect(useAppearance().appearance).toMatchObject({ accent: 'teal', fontSize: 'large' })
  })

  it('ignores a value it does not know, and a setting that does not exist', async () => {
    localStorage.setItem('sqm.appearance', JSON.stringify({ accent: 'chartreuse', sparkle: 'on' }))

    const { useAppearance, DEFAULTS } = await fresh()

    expect(useAppearance().appearance).toEqual(DEFAULTS)
  })

  it('survives what is stored being unreadable', async () => {
    localStorage.setItem('sqm.appearance', '{not json')

    const { useAppearance, DEFAULTS } = await fresh()

    expect(useAppearance().appearance).toEqual(DEFAULTS)
  })
})

describe('a choice', () => {
  it.each([
    ['fontFamily', 'inter', 'font'],
    ['fontSize', 'large', 'text'],
    ['fontWeight', 'medium', 'weight'],
    ['lineSpacing', 'relaxed', 'leading'],
    ['density', 'compact', 'density'],
    ['accent', 'teal', 'accent'],
    ['corners', 'round', 'corners'],
    ['motion', 'none', 'motion'],
  ] as const)('of %s reaches the page at once', async (key, value, attribute) => {
    const { useAppearance } = await fresh()

    useAppearance().set(key, value)

    expect(root.dataset[attribute]).toBe(value)
  })

  it('is kept in this browser', async () => {
    const { useAppearance } = await fresh()

    useAppearance().set('accent', 'violet')

    expect(JSON.parse(localStorage.getItem('sqm.appearance')!)).toMatchObject({ accent: 'violet' })
  })

  it('that the contract does not offer changes nothing', async () => {
    const { useAppearance, DEFAULTS } = await fresh()

    useAppearance().set('accent', 'chartreuse')

    expect(useAppearance().appearance.accent).toBe(DEFAULTS.accent)
  })

  it('tells the charts to read the tokens again', async () => {
    const { useAppearance, useChartRevision } = await fresh()
    const revision = useChartRevision()
    const before = revision.value

    useAppearance().set('accent', 'magenta')

    expect(revision.value).toBeGreaterThan(before)
  })

  it('can all be undone at once', async () => {
    const { useAppearance, DEFAULTS } = await fresh()
    const { set, reset, appearance, isDefault } = useAppearance()

    set('accent', 'teal')
    set('fontSize', 'larger')
    expect(isDefault.value).toBe(false)

    reset()

    expect(appearance).toEqual(DEFAULTS)
    expect(isDefault.value).toBe(true)
    expect(root.dataset['accent']).toBe('blue')
  })
})

describe('theme and motion that follow the system', () => {
  it('reads the system for the theme', async () => {
    media({ '(prefers-color-scheme: dark)': true })

    const { useAppearance } = await fresh()

    expect(root.dataset['theme']).toBe('dark')
    expect(useAppearance().isDark.value).toBe(true)
  })

  it('lets an explicit theme beat the system', async () => {
    media({ '(prefers-color-scheme: dark)': true })

    const { useAppearance } = await fresh()
    useAppearance().set('theme', 'light')

    expect(root.dataset['theme']).toBe('light')
  })

  it('resolves motion to what the stylesheet knows', async () => {
    await fresh()
    expect(root.dataset['motion']).toBe('full')

    media({ '(prefers-reduced-motion: reduce)': true })
    await fresh()
    expect(root.dataset['motion']).toBe('reduced')
  })

  it('lets a person ask for full motion against the system, and for none', async () => {
    media({ '(prefers-reduced-motion: reduce)': true })

    const { useAppearance } = await fresh()

    useAppearance().set('motion', 'full')
    expect(root.dataset['motion']).toBe('full')

    useAppearance().set('motion', 'none')
    expect(root.dataset['motion']).toBe('none')
  })
})

describe('whose settings they are', () => {
  it('uses what the account saved over what this browser kept', async () => {
    localStorage.setItem('sqm.appearance', JSON.stringify({ accent: 'teal' }))

    const { adoptAppearance, useAppearance } = await fresh()
    adoptAppearance(7, { accent: 'violet' })

    expect(useAppearance().appearance.accent).toBe('violet')
    expect(root.dataset['accent']).toBe('violet')
  })

  it('does not send straight back what it has just been given', async () => {
    vi.useFakeTimers()
    const { adoptAppearance } = await fresh()

    adoptAppearance(7, { accent: 'violet' })
    await vi.runAllTimersAsync()

    expect(put).not.toHaveBeenCalled()
  })

  it('makes this browser\'s look the account\'s, the first time, if it is nobody else\'s', async () => {
    localStorage.setItem('sqm.theme', 'dark')

    const { adoptAppearance } = await fresh()
    adoptAppearance(7, null)

    expect(put).toHaveBeenCalledOnce()
    expect(put.mock.calls[0]![1]).toMatchObject({ theme: 'dark' })
  })

  it('sends nothing for someone who has never changed anything', async () => {
    const { adoptAppearance } = await fresh()

    adoptAppearance(7, null)

    expect(put).not.toHaveBeenCalled()
  })

  it('does not hand one person\'s look to the next on a shared machine', async () => {
    localStorage.setItem('sqm.appearance', JSON.stringify({ accent: 'teal' }))
    localStorage.setItem('sqm.appearance.owner', '7')

    const { adoptAppearance, useAppearance, DEFAULTS } = await fresh()
    adoptAppearance(8, null)

    expect(useAppearance().appearance).toEqual(DEFAULTS)
    expect(put).not.toHaveBeenCalled()
  })

  it('remembers its owner across a reload', async () => {
    localStorage.setItem('sqm.appearance', JSON.stringify({ accent: 'teal' }))

    const first = await fresh()
    first.adoptAppearance(7, { accent: 'teal' })

    const second = await fresh()
    second.adoptAppearance(7, null)

    expect(second.useAppearance().appearance.accent).toBe('teal')
  })
})

describe('saving', () => {
  it('sends one request for a run of quick changes', async () => {
    vi.useFakeTimers()
    const { adoptAppearance, useAppearance } = await fresh()
    adoptAppearance(7, { theme: 'system' })

    const { set } = useAppearance()
    set('fontSize', 'large')
    set('fontSize', 'larger')
    set('accent', 'sky')
    await vi.runAllTimersAsync()

    expect(put).toHaveBeenCalledOnce()
    expect(put.mock.calls[0]![0]).toBe('/api/v1/auth/me/appearance')
    expect(put.mock.calls[0]![1]).toMatchObject({ fontSize: 'larger', accent: 'sky' })
  })

  it('says when it has', async () => {
    vi.useFakeTimers()
    const { adoptAppearance, useAppearance } = await fresh()
    adoptAppearance(7, { theme: 'system' })

    const { set, saveState } = useAppearance()
    set('accent', 'sky')
    await vi.runAllTimersAsync()

    expect(saveState.value).toBe('saved')
  })

  it('says when it could not, and keeps the look', async () => {
    vi.useFakeTimers()
    put.mockRejectedValue(new Error('no route'))
    const { adoptAppearance, useAppearance } = await fresh()
    adoptAppearance(7, { theme: 'system' })

    const { set, saveState, appearance } = useAppearance()
    set('accent', 'sky')
    await vi.runAllTimersAsync()
    await nextTick()

    expect(saveState.value).toBe('failed')
    expect(appearance.accent).toBe('sky')
  })

  it('sends nothing for someone not signed in', async () => {
    vi.useFakeTimers()
    const { useAppearance } = await fresh()

    useAppearance().set('accent', 'sky')
    await vi.runAllTimersAsync()

    expect(put).not.toHaveBeenCalled()
  })

  it('stops at sign-out, even with a change waiting', async () => {
    vi.useFakeTimers()
    const { adoptAppearance, releaseAppearance, useAppearance } = await fresh()
    adoptAppearance(7, { theme: 'system' })

    useAppearance().set('accent', 'sky')
    releaseAppearance()
    await vi.runAllTimersAsync()

    expect(put).not.toHaveBeenCalled()
  })
})
