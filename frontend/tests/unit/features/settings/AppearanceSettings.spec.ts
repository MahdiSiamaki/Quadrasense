import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { nextTick } from 'vue'

/*
  The page itself: what a person presses and what they are told. The settings' own rules are in
  tests/unit/lib/appearance.spec.ts. The preview draws a canvas, which happy-dom has none of, and it
  is the real components, tested where they live.
*/

const put = vi.fn()

vi.mock('@/api/client', () => ({
  api: { put: (...args: unknown[]) => put(...args) },
  ApiError: class ApiError extends Error {
    status = 500
  },
}))

vi.mock('@/features/settings/AppearancePreview.vue', () => ({
  default: { template: '<div data-test="preview" />' },
}))

const root = document.documentElement

async function page() {
  vi.resetModules()
  const { default: AppearanceSettings } = await import('@/features/settings/AppearanceSettings.vue')
  const appearance = await import('@/lib/appearance')
  return { wrapper: mount(AppearanceSettings), ...appearance }
}

function buttonNamed(w: Awaited<ReturnType<typeof page>>['wrapper'], name: string) {
  return w.findAll('button').find((b) => b.text() === name)!
}

beforeEach(() => {
  localStorage.clear()
  put.mockReset()
  put.mockResolvedValue(undefined)
  for (const key of Object.keys(root.dataset)) delete root.dataset[key]
  window.matchMedia = (() => ({
    matches: false,
    addEventListener: () => undefined,
  })) as unknown as typeof window.matchMedia
})

describe('the appearance page', () => {
  it('offers every setting', async () => {
    const { wrapper } = await page()

    for (const label of [
      'Theme',
      'Typeface',
      'Text size',
      'Text weight',
      'Line spacing',
      'Tables and forms',
      'Corners',
      'Accent colour',
      'Animation',
    ]) {
      expect(wrapper.text()).toContain(label)
    }
  })

  it('applies a choice to the whole page at once', async () => {
    const { wrapper } = await page()

    await wrapper.find('input[name="accent"][value="teal"]').setValue(true)

    expect(root.dataset['accent']).toBe('teal')
  })

  it('takes a size from the joined buttons', async () => {
    const { wrapper } = await page()

    await buttonNamed(wrapper, 'Larger').trigger('click')

    expect(root.dataset['text']).toBe('larger')
    expect(buttonNamed(wrapper, 'Larger').attributes('aria-checked')).toBe('true')
  })

  it('moves between sizes with the arrow keys', async () => {
    const { wrapper } = await page()

    await buttonNamed(wrapper, 'Medium').trigger('keydown', { key: 'ArrowRight' })

    expect(root.dataset['text']).toBe('large')
  })

  it('shows which theme is chosen, by more than a colour', async () => {
    const { wrapper } = await page()

    await wrapper.find('input[name="theme"][value="dark"]').setValue(true)

    expect(wrapper.find<HTMLInputElement>('input[name="theme"][value="dark"]').element.checked).toBe(true)
    expect(root.dataset['theme']).toBe('dark')
  })

  it('has nothing to reset until something has changed', async () => {
    const { wrapper } = await page()

    expect(buttonNamed(wrapper, 'Reset to defaults').attributes('disabled')).toBeDefined()

    await wrapper.find('input[name="accent"][value="sky"]').setValue(true)

    expect(buttonNamed(wrapper, 'Reset to defaults').attributes('disabled')).toBeUndefined()
  })

  it('puts everything back', async () => {
    const { wrapper } = await page()

    await wrapper.find('input[name="accent"][value="sky"]').setValue(true)
    await buttonNamed(wrapper, 'Larger').trigger('click')
    await buttonNamed(wrapper, 'Reset to defaults').trigger('click')

    expect(root.dataset['accent']).toBe('blue')
    expect(root.dataset['text']).toBe('medium')
  })

  it('says what each animation level does', async () => {
    const { wrapper } = await page()

    await buttonNamed(wrapper, 'None').trigger('click')

    expect(wrapper.text()).toContain('Everything happens at once.')
  })

  it('says when a change has reached the account', async () => {
    vi.useFakeTimers()
    const { wrapper, adoptAppearance } = await page()
    adoptAppearance(7, { theme: 'system' })

    await wrapper.find('input[name="accent"][value="sky"]').setValue(true)
    await vi.runAllTimersAsync()
    vi.useRealTimers()
    await flushPromises()
    await nextTick()

    expect(wrapper.find('[role="status"]').text()).toBe('Saved to your account.')
  })

  it('says when it could not, without undoing what the person sees', async () => {
    vi.useFakeTimers()
    put.mockRejectedValue(new Error('no route'))
    const { wrapper, adoptAppearance } = await page()
    adoptAppearance(7, { theme: 'system' })

    await wrapper.find('input[name="accent"][value="sky"]').setValue(true)
    await vi.runAllTimersAsync()
    vi.useRealTimers()
    await flushPromises()
    await nextTick()

    expect(wrapper.find('[role="status"]').text()).toContain('could not be saved')
    expect(root.dataset['accent']).toBe('sky')
  })
})
