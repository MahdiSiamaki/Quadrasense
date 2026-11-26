import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { defineComponent, h } from 'vue'
import { createMemoryHistory, createRouter, RouterView } from 'vue-router'
import HelpLayout from '@/features/help/HelpLayout.vue'
import { HELP_SECTIONS } from '@/features/help/sections'

/*
  The frame around Help's pages. A new page opens at its top: the menu stays beside a long page, so a
  guide is often chosen from the end of another, and the router keeps the scroll position. Above the
  page, on a phone, the guides wrap rather than scroll, so the one being read is never past an edge.
*/
const Page = defineComponent({ render: () => h('p', 'a page') })

let wrapper: VueWrapper | null = null

afterEach(() => {
  wrapper?.unmount()
  wrapper = null
  vi.restoreAllMocks()
})

async function mountAt(path: string) {
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [
      {
        path: '/help',
        component: HelpLayout,
        children: HELP_SECTIONS.map((section) => ({ path: section.to.slice('/help/'.length), component: Page })),
      },
    ],
  })
  await router.push(path)
  await router.isReady()
  wrapper = mount(defineComponent({ render: () => h(RouterView) }), { global: { plugins: [router] }, attachTo: document.body })
  await flushPromises()
  return router
}

describe('the Help frame', () => {
  it('opens a new page at its top', async () => {
    const scrollTo = vi.spyOn(window, 'scrollTo').mockImplementation(() => {})
    const router = await mountAt('/help/api-reference')

    await router.push('/help/importing-files')
    await flushPromises()

    expect(scrollTo).toHaveBeenCalledWith({ top: 0 })
  })

  it('leaves an anchor on the same page to the browser', async () => {
    const scrollTo = vi.spyOn(window, 'scrollTo').mockImplementation(() => {})
    const router = await mountAt('/help/api-reference')

    await router.push('/help/api-reference#IntegrationHistory')
    await flushPromises()

    expect(scrollTo).not.toHaveBeenCalled()
  })

  it('wraps the guides above the page on a phone, every one in sight, rather than scrolling them', async () => {
    // Settings' longer list scrolls in one row. Help's did, and hid the API reference past the
    // row's edge at 375 px: measured in the browser, which lays out what this DOM cannot.
    await mountAt('/help/api-reference')
    const row = wrapper!.get('nav[aria-label="Help"] > ul')

    expect(row.classes()).toContain('flex-wrap')
    expect(row.classes()).not.toContain('overflow-x-auto')
    expect(row.findAll('a').map((a) => a.attributes('href'))).toEqual(HELP_SECTIONS.map((s) => s.to))
    expect(row.get('a[aria-current="page"]').text()).toBe('API reference')
  })
})
