import { readFileSync } from 'node:fs'
import { resolve as resolvePath } from 'node:path'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query'
import { api } from '@/api/client'
import { curlOf, routesOf, type OpenApiDocument } from '@/lib/openapi'
import ApiReference from '@/features/help/ApiReference.vue'
import { router } from '@/router'

/*
  The API reference, drawn from the document the server publishes - here the committed copy the
  backend keeps equal to it. Every route in it is on the page, each says what it needs, and nothing
  on the page asks for a secret: the requests read it from a variable, where it already is.
*/
const doc = JSON.parse(
  readFileSync(resolvePath(process.cwd(), '../docs/integration/openapi/pi-integration-v1.json'), 'utf-8'),
) as OpenApiDocument

let wrapper: VueWrapper | null = null
let queryClient: QueryClient

beforeEach(() => {
  vi.spyOn(api, 'get').mockImplementation(async () => doc as never)
})

afterEach(() => {
  wrapper?.unmount()
  wrapper = null
  window.location.hash = ''
  vi.restoreAllMocks()
})

async function page() {
  queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  wrapper = mount(ApiReference, {
    global: { plugins: [[VueQueryPlugin, { queryClient }]] },
    attachTo: document.body,
  })
  await flushPromises()
  return wrapper
}

const section = (w: VueWrapper, id: string) => w.get(`section[id="${id}"]`)

describe('the API reference', () => {
  it('reads the document the server publishes', async () => {
    await page()

    expect(api.get).toHaveBeenCalledWith('/api/v1/integration/openapi', undefined, expect.anything())
  })

  it('has every route, each once, and an index that reaches each', async () => {
    const w = await page()
    const routes = routesOf(doc)

    for (const route of routes) {
      expect(w.findAll(`section[id="${route.id}"]`), route.path).toHaveLength(1)
      expect(w.find(`nav a[href="#${route.id}"]`).exists(), route.path).toBe(true)
    }
  })

  it('lists every scope with what it allows', async () => {
    const w = await page()
    const scopes = Object.keys(doc.components!.securitySchemes!['oauth2']!.flows!.clientCredentials!.scopes)

    const listed = w.findAll('dl dt').map((dt) => dt.text())
    expect(listed).toEqual([...scopes].sort((a, b) => a.localeCompare(b)))
  })

  it('says what each route needs of the token', async () => {
    const w = await page()
    const id = (path: string) => routesOf(doc).find((r) => r.path === path)!.id

    expect(section(w, id('/api/integration/v1/oauth/token')).text()).toContain('No token: this is where a token comes from.')
    expect(section(w, id('/api/integration/v1/status')).text()).toContain("Any registered system's token.")
    expect(section(w, id('/api/integration/v1/inventory/lookup')).text()).toContain('A token with the scope')
    expect(section(w, id('/api/integration/v1/inventory/lookup')).text()).toContain('inventory.read')
  })

  it('names a refusal as a problem document, and an answer by its shape', async () => {
    const w = await page()
    const lookup = section(w, routesOf(doc).find((r) => r.path === '/api/integration/v1/inventory/lookup')!.id)

    expect(lookup.text()).toContain('200 OK - InventoryLookupResponse')
    expect(lookup.text()).toContain('400 Bad Request - a problem document, with the reason')
  })

  it('draws a nested object only when it is opened', async () => {
    const w = await page()
    const lookup = section(w, routesOf(doc).find((r) => r.path === '/api/integration/v1/inventory/lookup')!.id)

    // The answer's own fields, opened.
    const answer = lookup.findAll('details').find((d) => d.find('summary').text() === 'Fields')!
    ;(answer.element as HTMLDetailsElement).open = true
    await answer.trigger('toggle')
    await flushPromises()
    expect(answer.text()).toContain('bindings')

    // Its list of bindings, not yet drawn, then drawn when opened.
    const bindings = answer.findAll('details').find((d) => d.find('summary').text() === 'Fields of InventoryBindingInfo')!
    expect(bindings.text()).not.toContain('firstSeenIsDumpWindow')

    ;(bindings.element as HTMLDetailsElement).open = true
    await bindings.trigger('toggle')
    await flushPromises()
    expect(bindings.text()).toContain('firstSeenIsDumpWindow')
  })

  it('gives each route a request to start from, and copies exactly that', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined)
    vi.stubGlobal('navigator', { ...navigator, clipboard: { writeText } })
    const w = await page()
    const token = routesOf(doc)[0]!

    const block = section(w, token.id).get('pre').text()
    expect(block).toBe(curlOf(doc, token))
    expect(block).toContain('$CLIENT_SECRET')

    await section(w, token.id).get(`[aria-label="Copy the request for POST ${token.path}"]`).trigger('click')
    await flushPromises()

    expect(writeText).toHaveBeenCalledWith(curlOf(doc, token))
    vi.unstubAllGlobals()
  })

  it("gives the document's description in its own words, its full stop not doubled", async () => {
    const w = await page()
    const header = w.get('article > header').text()

    expect(header).toContain(doc.info.description!)
    expect(header).not.toContain('..')
  })

  it('opens at the route an address names, once the routes are drawn, and only then', async () => {
    const target = routesOf(doc).at(-1)!
    window.location.hash = `#${target.id}`
    const scrolled = vi.spyOn(Element.prototype, 'scrollIntoView').mockImplementation(() => {})

    await page()
    expect(scrolled.mock.contexts.map((element) => (element as Element).id)).toEqual([target.id])

    // A refetch draws the same routes again: the reader, by now elsewhere, is not pulled back.
    await queryClient.invalidateQueries({ queryKey: ['help', 'openapi'] })
    await flushPromises()
    expect(api.get).toHaveBeenCalledTimes(2)
    expect(scrolled).toHaveBeenCalledTimes(1)
  })

  it('offers the document itself to download', async () => {
    const w = await page()
    const link = w.get('a[download]')

    expect(link.attributes('href')).toMatch(/\/api\/v1\/integration\/openapi$/)
    expect(link.attributes('download')).toBe('pi-integration-v1.json')
  })
})

describe('Help', () => {
  it('asks no permission of anyone signed in', () => {
    const help = router.getRoutes().filter((r) => r.path.startsWith('/help'))

    expect(help.map((r) => r.path)).toEqual(
      expect.arrayContaining(['/help', '/help/importing-files', '/help/reading-the-numbers', '/help/connecting-a-system', '/help/api-reference']),
    )
    for (const route of help) {
      expect(route.meta['permission'], route.path).toBeUndefined()
      expect(route.meta['anyPermission'], route.path).toBeUndefined()
    }
  })
})
