import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query'
import { api } from '@/api/client'
import type { IntegrationClient, IntegrationClientList, IssuedSecret } from '@/api/integration'
import IntegrationClientsPage from '@/features/integration/IntegrationClientsPage.vue'
import { sectionsFor } from '@/features/settings/sections'

/*
  The systems that call the integration API, as an administrator registers them. What matters here
  is what the page does with a secret: it shows it once, from the answer that issued it, and lets go
  of it when the dialog closes - PI keeps only the hash, so nothing can show it again.
*/
const client = (over: Partial<IntegrationClient>): IntegrationClient => ({
  id: 1,
  clientId: 'lodestone',
  displayName: 'Lodestone CRM',
  description: 'Declared SIM swaps and the handset of a number.',
  scopes: ['inventory.read'],
  isActive: true,
  secretSetAt: '2026-11-01T08:00:00Z',
  lastTokenAt: '2026-11-11T09:00:00Z',
  createdAt: '2026-11-01T08:00:00Z',
  createdBy: 'admin',
  updatedAt: '2026-11-01T08:00:00Z',
  updatedBy: 'admin',
  ...over,
})

const list: IntegrationClientList = {
  clients: [
    client({ id: 2, clientId: 'old-bi', displayName: 'Old BI feed', isActive: false, lastTokenAt: null }),
    client({ id: 1 }),
  ],
  scopes: [
    { scope: 'inventory.read', description: 'Look up a number, SIM or handset.' },
    { scope: 'inventory.reveal', description: 'See whole numbers, IMSIs and IMEIs in the answers.' },
  ],
}

const SECRET = 's3cr3t-shown-once-0123456789abcdef'

let wrapper: VueWrapper | null = null

beforeEach(() => {
  vi.spyOn(api, 'get').mockImplementation(async () => list as never)
})

afterEach(() => {
  wrapper?.unmount()
  wrapper = null
  vi.restoreAllMocks()
})

async function page() {
  wrapper = mount(IntegrationClientsPage, {
    global: { plugins: [[VueQueryPlugin, { queryClient: new QueryClient({ defaultOptions: { queries: { retry: false } } }) }]] },
    attachTo: document.body,
  })
  await flushPromises()
  return wrapper
}

/** The dialog whose title starts with the given words, open or not. */
function dialog(w: VueWrapper, title: string) {
  const found = w.findAll('dialog').find((d) => d.find('h2').text().startsWith(title))
  if (!found) throw new Error(`no dialog titled "${title}"`)
  return found
}

const button = (root: { findAll: VueWrapper['findAll'] }, text: string) => {
  const found = root.findAll('button').find((b) => b.text() === text)
  if (!found) throw new Error(`no button "${text}"`)
  return found
}

describe('the API clients page', () => {
  it('lists the active systems first, with what each may ask for and when it last did', async () => {
    const w = await page()

    const rows = w.findAll('tbody tr').map((r) => r.text())
    expect(rows[0]).toContain('Lodestone CRM')
    expect(rows[0]).toContain('lodestone')
    expect(rows[0]).toContain('inventory.read')
    expect(rows[0]).toContain('Active')
    expect(rows[1]).toContain('Old BI feed')
    expect(rows[1]).toContain('Deactivated')
    expect(rows[1]).toContain('never')
  })

  it('registers a system and shows its secret once, then lets go of it', async () => {
    const issued: IssuedSecret = { client: client({ id: 3, clientId: 'nexus-bi', displayName: 'Nexus BI' }), clientSecret: SECRET }
    const post = vi.spyOn(api, 'post').mockResolvedValue(issued as never)
    const w = await page()

    await button(w, 'Register a system').trigger('click')
    const form = dialog(w, 'Register a system')
    const [id, name] = form.findAll('input:not([type="checkbox"])')

    // The id's rule is said before the server has to: upper case is not an id.
    await id!.setValue('Nexus BI')
    await name!.setValue('Nexus BI')
    await form.findAll('input[type="checkbox"]')[0]!.setValue(true)
    expect(button(form, 'Register and issue a secret').attributes('disabled')).toBeDefined()

    await id!.setValue('nexus-bi')
    expect(button(form, 'Register and issue a secret').attributes('disabled')).toBeUndefined()

    await form.find('form').trigger('submit')
    await flushPromises()

    expect(post).toHaveBeenCalledWith('/api/v1/integration/clients/', {
      clientId: 'nexus-bi',
      displayName: 'Nexus BI',
      description: '',
      scopes: ['inventory.read'],
    })

    const shown = dialog(w, 'The secret for Nexus BI')
    expect(shown.text()).toContain('Shown this once')
    expect((shown.find('#issued-secret').element as HTMLInputElement).value).toBe(SECRET)

    await shown.find('form').trigger('submit')
    await flushPromises()

    // Gone from the page: nothing can show it again.
    expect(w.html()).not.toContain(SECRET)
  })

  it('asks before issuing a new secret, says what stops working, then shows the new one', async () => {
    const post = vi.spyOn(api, 'post').mockResolvedValue({ client: client({}), clientSecret: SECRET } as never)
    const w = await page()

    await w.get('[aria-label="Issue a new secret for Lodestone CRM"]').trigger('click')
    const confirm = dialog(w, 'A new secret for Lodestone CRM')
    expect(confirm.text()).toContain('The current secret stops working now')
    expect(post).not.toHaveBeenCalled()

    await confirm.find('form').trigger('submit')
    await flushPromises()

    expect(post).toHaveBeenCalledWith('/api/v1/integration/clients/1/secret', {})
    expect((dialog(w, 'The secret for Lodestone CRM').find('#issued-secret').element as HTMLInputElement).value).toBe(SECRET)
  })

  it('offers no new secret to a deactivated system', async () => {
    const w = await page()

    expect(w.get('[aria-label="Issue a new secret for Old BI feed"]').attributes('disabled')).toBeDefined()
  })

  it('says, before it is saved, that deactivating a system ends its tokens', async () => {
    const put = vi.spyOn(api, 'put').mockResolvedValue(client({ isActive: false }) as never)
    const w = await page()

    await w.get('[aria-label="Change Lodestone CRM"]').trigger('click')
    const form = dialog(w, 'Change Lodestone CRM')
    expect(form.text()).not.toContain('Saving this ends every token')

    await form.get('[role="switch"]').trigger('click')
    expect(form.text()).toContain('Saving this ends every token Lodestone CRM holds now.')

    await form.find('form').trigger('submit')
    await flushPromises()

    expect(put).toHaveBeenCalledWith('/api/v1/integration/clients/1', {
      displayName: 'Lodestone CRM',
      description: 'Declared SIM swaps and the handset of a number.',
      scopes: ['inventory.read'],
      isActive: false,
    })
  })

  it('shows the field the server refused, in its words', async () => {
    const { ApiError } = await import('@/api/client')
    vi.spyOn(api, 'post').mockRejectedValue(
      new ApiError(400, { title: 'Invalid', errors: { clientId: ['A client named "lodestone" is registered already.'] } } as never, null),
    )
    const w = await page()

    await button(w, 'Register a system').trigger('click')
    const form = dialog(w, 'Register a system')
    const [id, name] = form.findAll('input:not([type="checkbox"])')
    await id!.setValue('lodestone')
    await name!.setValue('Lodestone again')
    await form.findAll('input[type="checkbox"]')[0]!.setValue(true)
    await form.find('form').trigger('submit')
    await flushPromises()

    expect(form.text()).toContain('A client named "lodestone" is registered already.')
  })
})

describe('the settings section', () => {
  it('is there for whoever may manage integration, and for nobody else', () => {
    expect(sectionsFor(['integration.manage']).map((s) => s.label)).toContain('API clients')
    expect(sectionsFor(['user.view', 'audit.view']).map((s) => s.label)).not.toContain('API clients')
  })
})
