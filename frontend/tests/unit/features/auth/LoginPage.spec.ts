import { afterEach, describe, expect, it, vi } from 'vitest'
import LoginPage from '@/features/auth/LoginPage.vue'
import { fakeApi, mountPage, OPTIONS, settle, USER } from './harness'

const LOGIN = '/api/v1/auth/login'
const SECOND = '/api/v1/auth/login/two-step'
const OPTIONS_PATH = '/api/v1/auth/options'

afterEach(() => {
  vi.unstubAllGlobals()
  document.body.replaceChildren()
})

async function signIn(w: Awaited<ReturnType<typeof mountPage>>['wrapper'], password = 'a-password') {
  await w.find('input[autocomplete="username"]').setValue('a.person')
  await w.find('input[autocomplete="current-password"]').setValue(password)
  await w.find('form').trigger('submit')
  await settle()
}

describe('the sign-in page', () => {
  it('offers recovery only where this deployment can send a code', async () => {
    fakeApi({ [OPTIONS_PATH]: { status: 200, body: OPTIONS } })
    const { wrapper } = await mountPage(LoginPage)

    expect(wrapper.text()).toContain('Forgot password?')
  })

  it('says who can help where it cannot', async () => {
    fakeApi({
      [OPTIONS_PATH]: { status: 200, body: { ...OPTIONS, recoverByEmail: false, recoverBySms: false } },
    })
    const { wrapper } = await mountPage(LoginPage)

    expect(wrapper.text()).not.toContain('Forgot password?')
    expect(wrapper.text()).toContain('Contact your administrator')
  })

  it('says who can help when the API cannot be asked', async () => {
    fakeApi({ [OPTIONS_PATH]: { status: 500 } })
    const { wrapper } = await mountPage(LoginPage)

    expect(wrapper.text()).not.toContain('Forgot password?')
  })

  it('takes the length of "remember me" from the server rather than stating its own', async () => {
    fakeApi({ [OPTIONS_PATH]: { status: 200, body: { ...OPTIONS, rememberDays: 14 } } })
    const { wrapper } = await mountPage(LoginPage)

    expect(wrapper.text()).toContain('Remember me for 14 days')
  })

  it('sends "remember me" when it is ticked', async () => {
    const api = fakeApi({
      [OPTIONS_PATH]: { status: 200, body: OPTIONS },
      [LOGIN]: { status: 200, body: USER },
    })
    const { wrapper } = await mountPage(LoginPage)

    await wrapper.find('input[type="checkbox"]').setValue(true)
    await signIn(wrapper)

    expect(api.bodyOf(LOGIN)).toEqual({
      username: 'a.person',
      password: 'a-password',
      rememberMe: true,
    })
  })

  it('does not send it when it is not', async () => {
    const api = fakeApi({
      [OPTIONS_PATH]: { status: 200, body: OPTIONS },
      [LOGIN]: { status: 200, body: USER },
    })
    const { wrapper } = await mountPage(LoginPage)

    await signIn(wrapper)

    expect(api.bodyOf(LOGIN)).toMatchObject({ rememberMe: false })
  })

  it('goes where it was headed, and only inside the application', async () => {
    fakeApi({ [OPTIONS_PATH]: { status: 200, body: OPTIONS }, [LOGIN]: { status: 200, body: USER } })
    const { wrapper, router } = await mountPage(LoginPage, '/login?next=/deep/page')

    await signIn(wrapper)

    expect(router.currentRoute.value.path).toBe('/deep/page')
  })

  it('refuses to be sent off the site', async () => {
    fakeApi({ [OPTIONS_PATH]: { status: 200, body: OPTIONS }, [LOGIN]: { status: 200, body: USER } })
    const { wrapper, router } = await mountPage(LoginPage, '/login?next=https://elsewhere.test/')

    await signIn(wrapper)

    expect(router.currentRoute.value.path).toBe('/')
  })

  it('shows why a sign-in failed, empties the password and puts focus back in it', async () => {
    fakeApi({
      [OPTIONS_PATH]: { status: 200, body: OPTIONS },
      [LOGIN]: {
        status: 401,
        body: { status: 'InvalidCredentials', message: 'Incorrect username or password.', lockedUntil: null },
      },
    })
    const { wrapper } = await mountPage(LoginPage)

    await signIn(wrapper)

    const password = wrapper.find<HTMLInputElement>('input[autocomplete="current-password"]')

    expect(wrapper.find('[role="alert"]').text()).toBe('Incorrect username or password.')
    expect(password.element.value).toBe('')
    expect(document.activeElement).toBe(password.element)
  })

  it('tells a locked account when it frees up', async () => {
    fakeApi({
      [OPTIONS_PATH]: { status: 200, body: OPTIONS },
      [LOGIN]: {
        status: 401,
        body: {
          status: 'AccountLocked',
          message: 'Too many failed attempts.',
          lockedUntil: new Date(Date.now() + 5 * 60_000).toISOString(),
        },
      },
    })
    const { wrapper } = await mountPage(LoginPage)

    await signIn(wrapper)

    expect(wrapper.find('[role="alert"]').text()).toContain('Unlocks')
  })

  it('says when the limiter has refused, in plain words', async () => {
    fakeApi({ [OPTIONS_PATH]: { status: 200, body: OPTIONS }, [LOGIN]: { status: 429 } })
    const { wrapper } = await mountPage(LoginPage)

    await signIn(wrapper)

    expect(wrapper.find('[role="alert"]').text()).toContain('Too many attempts')
  })

  it('carries the username to recovery without putting it in the address', async () => {
    fakeApi({ [OPTIONS_PATH]: { status: 200, body: OPTIONS } })
    const { wrapper, router } = await mountPage(LoginPage)

    await wrapper.find('input[autocomplete="username"]').setValue('a.person')
    await wrapper.findAll('button').find((b) => b.text() === 'Forgot password?')!.trigger('click')
    await settle()

    expect(router.currentRoute.value.path).toBe('/recover')
    expect(router.currentRoute.value.fullPath).not.toContain('a.person')
  })
})

describe('the captcha', () => {
  const CAPTCHA = '/api/v1/auth/captcha'
  const picture = (id: string) => ({
    status: 200,
    body: { id, image: `data:image/png;base64,${id}`, expiresAt: new Date(Date.now() + 600_000).toISOString(), alphabet: 'بتج۲۳' },
  })
  const refusal = (captchaRequired: boolean) => ({
    status: 401,
    body: { status: 'InvalidCredentials', message: 'Incorrect username or password.', lockedUntil: null, captchaRequired },
  })

  it('is hidden at first', async () => {
    fakeApi({ [OPTIONS_PATH]: { status: 200, body: OPTIONS } })
    const { wrapper } = await mountPage(LoginPage)

    expect(wrapper.find('fieldset').exists()).toBe(false)
  })

  it('stays hidden after a refusal that does not ask for it', async () => {
    fakeApi({ [OPTIONS_PATH]: { status: 200, body: OPTIONS }, [LOGIN]: refusal(false) })
    const { wrapper } = await mountPage(LoginPage)

    await signIn(wrapper)

    expect(wrapper.find('fieldset').exists()).toBe(false)
  })

  it('appears with a picture and its keyboard when the refusal that made three asks for it', async () => {
    const api = fakeApi({ [OPTIONS_PATH]: { status: 200, body: OPTIONS }, [LOGIN]: refusal(true), [CAPTCHA]: picture('c1') })
    const { wrapper } = await mountPage(LoginPage)

    await signIn(wrapper)

    expect(wrapper.get('fieldset legend').text()).toBe('Security check')
    expect(wrapper.get('fieldset img').attributes('src')).toBe('data:image/png;base64,c1')
    expect(wrapper.findAll('[role="group"] button')).toHaveLength(6)
    expect(api.sent.filter((s) => s.path === CAPTCHA)).toHaveLength(1)
  })

  it('appears at once when this address already needs it', async () => {
    fakeApi({ [OPTIONS_PATH]: { status: 200, body: { ...OPTIONS, captchaRequired: true } }, [CAPTCHA]: picture('c1') })
    const { wrapper } = await mountPage(LoginPage)
    await settle()

    expect(wrapper.get('fieldset img').attributes('src')).toBe('data:image/png;base64,c1')
  })

  it('keeps "Sign in" waiting until the characters are typed', async () => {
    fakeApi({ [OPTIONS_PATH]: { status: 200, body: { ...OPTIONS, captchaRequired: true } }, [CAPTCHA]: picture('c1') })
    const { wrapper } = await mountPage(LoginPage)
    await settle()

    await wrapper.find('input[autocomplete="username"]').setValue('a.person')
    await wrapper.find('input[autocomplete="current-password"]').setValue('a-password')
    const button = () => wrapper.findAll('button').find((b) => b.text() === 'Sign in')!

    expect(button().attributes('disabled')).toBeDefined()

    await wrapper.findAll('[role="group"] button')[0]!.trigger('click')
    expect(button().attributes('disabled')).toBeUndefined()
  })

  it("sends the answer with the picture's id, then shows a new picture with the answer cleared", async () => {
    const api = fakeApi({
      [OPTIONS_PATH]: { status: 200, body: { ...OPTIONS, captchaRequired: true } },
      [CAPTCHA]: [picture('c1'), picture('c2')],
      [LOGIN]: refusal(true),
    })
    const { wrapper } = await mountPage(LoginPage)
    await settle()

    const keys = wrapper.findAll('[role="group"] button')
    await keys[0]!.trigger('click')
    await keys[3]!.trigger('click')
    await signIn(wrapper)

    expect(api.bodyOf(LOGIN)).toMatchObject({ captchaId: 'c1', captchaAnswer: 'ب۲' })
    expect(wrapper.get('fieldset img').attributes('src')).toBe('data:image/png;base64,c2')
    expect(wrapper.get<HTMLInputElement>('fieldset input').element.value).toBe('')
  })
})

describe('the second step', () => {
  function needsCode(extra: Record<string, unknown> = {}) {
    return fakeApi({
      [OPTIONS_PATH]: { status: 200, body: OPTIONS },
      [LOGIN]: { status: 202, body: { ticket: 'the-ticket', expiresAt: '2026-11-07T10:00:00Z' } },
      ...extra,
    })
  }

  it('replaces the form and goes nowhere yet', async () => {
    needsCode()
    const { wrapper, router } = await mountPage(LoginPage)

    await signIn(wrapper)

    expect(wrapper.find('h1').text()).toBe('Two-step sign-in')
    expect(router.currentRoute.value.path).toBe('/login')
  })

  it('takes focus and asks for a numeric keyboard', async () => {
    needsCode()
    const { wrapper } = await mountPage(LoginPage)

    await signIn(wrapper)

    const code = wrapper.find('input[autocomplete="one-time-code"]')

    expect(code.attributes('inputmode')).toBe('numeric')
    expect(document.activeElement).toBe(code.element)
  })

  it('sends the ticket with the code, and goes in', async () => {
    const api = needsCode({ [SECOND]: { status: 200, body: USER } })
    const { wrapper, router } = await mountPage(LoginPage)

    await signIn(wrapper)
    await wrapper.find('input[autocomplete="one-time-code"]').setValue('123456')
    await wrapper.find('form').trigger('submit')
    await settle()

    expect(api.bodyOf(SECOND)).toEqual({ ticket: 'the-ticket', code: '123456' })
    expect(router.currentRoute.value.path).toBe('/')
  })

  it('stays put on a wrong code, with the field emptied and focused', async () => {
    needsCode({
      [SECOND]: {
        status: 401,
        body: { status: 'InvalidCode', message: 'That code is not right.', lockedUntil: null },
      },
    })
    const { wrapper } = await mountPage(LoginPage)

    await signIn(wrapper)
    await wrapper.find('input[autocomplete="one-time-code"]').setValue('123456')
    await wrapper.find('form').trigger('submit')
    await settle()

    const code = wrapper.find<HTMLInputElement>('input[autocomplete="one-time-code"]')

    expect(wrapper.find('[role="alert"]').text()).toBe('That code is not right.')
    expect(code.element.value).toBe('')
    expect(document.activeElement).toBe(code.element)
  })

  it('goes back to the password when the ticket has gone, saying why', async () => {
    needsCode({
      [SECOND]: {
        status: 401,
        body: { status: 'ChallengeExpired', message: 'That sign-in has run out of time.', lockedUntil: null },
      },
    })
    const { wrapper } = await mountPage(LoginPage)

    await signIn(wrapper)
    await wrapper.find('input[autocomplete="one-time-code"]').setValue('123456')
    await wrapper.find('form').trigger('submit')
    await settle()

    expect(wrapper.find('h1').text()).toBe('Welcome back')
    expect(wrapper.find('[role="alert"]').text()).toContain('run out of time')
  })

  it('takes a recovery code in a field of its own', async () => {
    const api = needsCode({ [SECOND]: { status: 200, body: USER } })
    const { wrapper } = await mountPage(LoginPage)

    await signIn(wrapper)
    await wrapper.findAll('button').find((b) => b.text() === 'Use a recovery code')!.trigger('click')
    await settle()

    expect(wrapper.find('input[autocomplete="one-time-code"]').exists()).toBe(false)

    await wrapper.find('input').setValue('7K2M-9QXT-4HNP-W3RD')
    await wrapper.find('form').trigger('submit')
    await settle()

    expect(api.bodyOf(SECOND)).toMatchObject({ code: '7K2M-9QXT-4HNP-W3RD' })
  })

  it('does not submit a code that is not whole', async () => {
    const api = needsCode({ [SECOND]: { status: 200, body: USER } })
    const { wrapper } = await mountPage(LoginPage)

    await signIn(wrapper)
    await wrapper.find('input[autocomplete="one-time-code"]').setValue('123')
    await wrapper.find('form').trigger('submit')
    await settle()

    expect(api.bodyOf(SECOND)).toBeUndefined()
  })
})
