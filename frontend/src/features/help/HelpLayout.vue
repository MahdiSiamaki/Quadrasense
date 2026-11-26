<script setup lang="ts">
import { computed, watch } from 'vue'
import { RouterLink, RouterView, useRoute } from 'vue-router'
import { HELP_SECTIONS, type HelpSection } from './sections'

/**
 * The frame every Help page sits in, like Settings': the guides listed beside the page, grouped,
 * and above it below the large breakpoint.
 *
 * Above the page they wrap rather than scroll, unlike Settings' longer list: four guides take two
 * lines at 375 px, and a row that scrolled hid the page being read past its edge - the API reference,
 * 11 px out even once scrolled to, when the page's scrollbar arrived with the document (2026-11-26).
 */
const route = useRoute()

/*
  A Help page opens at its top. The menu stays beside the page as it scrolls, so a guide is often
  chosen from the end of another - and the router keeps the scroll position, so it opened at its
  end: from the API reference's last route, Importing files opened 350 px past its title
  (2026-11-26). Only a new page: an anchor on the same page is the browser's to jump to.
*/
watch(
  () => route.path,
  () => window.scrollTo({ top: 0 }),
)

const groups = computed(() => {
  const byGroup = new Map<string, HelpSection[]>()
  for (const section of HELP_SECTIONS) {
    byGroup.set(section.group, [...(byGroup.get(section.group) ?? []), section])
  }
  return [...byGroup.entries()].map(([name, items]) => ({ name, items }))
})

const isCurrent = (to: string) => route.path === to || route.path.startsWith(`${to}/`)
</script>

<template>
  <div class="flex flex-col gap-6 lg:flex-row lg:gap-8">
    <aside class="shrink-0 lg:sticky lg:top-20 lg:w-56 lg:self-start">
      <h1 class="mb-3 text-xl font-semibold tracking-tight lg:mb-5">Help</h1>

      <nav aria-label="Help">
        <div class="hidden flex-col gap-5 lg:flex">
          <div v-for="group in groups" :key="group.name">
            <p class="mb-1.5 px-2.5 text-2xs font-semibold tracking-wider text-[var(--c-text-muted)] uppercase">
              {{ group.name }}
            </p>
            <ul class="flex flex-col gap-0.5">
              <li v-for="item in group.items" :key="item.to">
                <RouterLink
                  :to="item.to"
                  :aria-current="isCurrent(item.to) ? 'page' : undefined"
                  class="block rounded-[var(--radius-md)] px-2.5 py-1.5 text-sm font-medium"
                  :class="
                    isCurrent(item.to)
                      ? 'bg-[var(--c-surface-sunken)] text-[var(--c-text)]'
                      : 'text-[var(--c-text-secondary)] hover:bg-[var(--c-surface-hover)] hover:text-[var(--c-text)]'
                  "
                >
                  {{ item.label }}
                </RouterLink>
              </li>
            </ul>
          </div>
        </div>

        <ul class="-mx-1 flex flex-wrap gap-1 border-b pb-2 lg:hidden">
          <li v-for="item in HELP_SECTIONS" :key="item.to">
            <RouterLink
              :to="item.to"
              :aria-current="isCurrent(item.to) ? 'page' : undefined"
              class="block rounded-[var(--radius-md)] px-2.5 py-1.5 text-sm font-medium whitespace-nowrap"
              :class="
                isCurrent(item.to)
                  ? 'bg-[var(--c-surface-sunken)] text-[var(--c-text)]'
                  : 'text-[var(--c-text-secondary)] hover:bg-[var(--c-surface-hover)]'
              "
            >
              {{ item.label }}
            </RouterLink>
          </li>
        </ul>
      </nav>
    </aside>

    <section class="min-w-0 flex-1">
      <RouterView />
    </section>
  </div>
</template>
