import { describe, expect, it } from 'vitest'
import { mount } from '@vue/test-utils'
import CaptchaField from '@/features/auth/CaptchaField.vue'

/*
  The captcha's field (ADR-026; MCI red line 2-3 row 3): the picture, a new one on request, the answer
  set right to left, and an on-screen keyboard of exactly the characters the server says a captcha may
  hold - for someone with no Persian keyboard layout.
*/
const ALPHABET = 'بتج۲۳'

function field(modelValue = '', props: Record<string, unknown> = {}) {
  return mount(CaptchaField, {
    props: { image: 'data:image/png;base64,AAAA', alphabet: ALPHABET, modelValue, ...props },
  })
}

const keys = (w: ReturnType<typeof field>) => w.findAll('[role="group"] button')

describe('the captcha field', () => {
  it('shows the picture and a key for every character the server allows, and a key to delete', () => {
    const w = field()

    expect(w.get('img').attributes('src')).toBe('data:image/png;base64,AAAA')
    expect(keys(w).slice(0, -1).map((k) => k.text())).toEqual([...ALPHABET])
    expect(keys(w).at(-1)!.attributes('aria-label')).toBe('Delete the last character')
  })

  it('types a pressed key after what is there', async () => {
    const w = field('ب')

    await keys(w)[3]!.trigger('click')

    expect(w.emitted('update:modelValue')!.at(-1)).toEqual(['ب۲'])
  })

  it('deletes the last character', async () => {
    const w = field('بت۲')

    await keys(w).at(-1)!.trigger('click')

    expect(w.emitted('update:modelValue')!.at(-1)).toEqual(['بت'])
  })

  it('stops at twelve characters', async () => {
    const w = field('ب'.repeat(12))

    await keys(w)[0]!.trigger('click')

    expect(w.emitted('update:modelValue')).toBeUndefined()
  })

  it('takes what is typed on a real keyboard too, right to left and in Persian', async () => {
    const w = field()
    const input = w.get('input')

    expect(input.attributes('dir')).toBe('rtl')
    expect(input.attributes('lang')).toBe('fa')
    expect(input.attributes('autocomplete')).toBe('off')

    await input.setValue('بت')
    expect(w.emitted('update:modelValue')!.at(-1)).toEqual(['بت'])
  })

  it('asks for a new picture', async () => {
    const w = field()

    await w.findAll('button').find((b) => b.text() === 'New picture')!.trigger('click')

    expect(w.emitted('refresh')).toHaveLength(1)
  })

  it('says it is drawing while there is no picture yet', () => {
    const w = field('', { image: null, loading: true })

    expect(w.find('img').exists()).toBe(false)
    expect(w.text()).toContain('Drawing a picture')
  })

  it('locks every key while the form is sending', () => {
    const w = field('', { disabled: true })

    expect(w.get('fieldset').attributes('disabled')).toBeDefined()
  })
})
