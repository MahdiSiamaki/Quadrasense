<script setup lang="ts">
import { ref } from 'vue'
import BarChart from '@/design-system/BarChart.vue'
import Button from '@/design-system/Button.vue'
import Card from '@/design-system/Card.vue'
import TextField from '@/design-system/TextField.vue'
import Toggle from '@/design-system/Toggle.vue'

/**
 * The application in miniature: the real components, not pictures of them.
 *
 * Every setting applies to the whole page the moment it is chosen, so this is not a mock-up that
 * could disagree with the result. It exists because the settings page itself has no table, no form
 * and no chart, and those are what density and the type scale are for.
 *
 * The figures are invented, and say so by being round.
 */
const sample = ref('Galaxy A55')
const on = ref(true)

const rows = [
  { vendor: 'Samsung', bindings: '49,000,000', share: '44%' },
  { vendor: 'Xiaomi', bindings: '27,000,000', share: '24%' },
  { vendor: 'Apple', bindings: '12,000,000', share: '11%' },
]

const bars = [
  { key: 'Samsung', count: 49_000_000, percent: 44 },
  { key: 'Xiaomi', count: 27_000_000, percent: 24 },
  { key: 'Apple', count: 12_000_000, percent: 11 },
]
</script>

<template>
  <Card title="Preview" subtitle="The real components, as they look now. The figures are made up.">
    <div class="flex flex-col gap-4">
      <div>
        <p class="text-lg font-semibold tracking-tight">Device population</p>
        <p class="mt-0.5 text-sm text-[var(--c-text-secondary)]">
          Bindings on the network, by vendor. Text at this size is what most pages are made of.
        </p>
      </div>

      <div class="overflow-x-auto rounded-[var(--radius-md)] border">
        <table class="w-full text-sm">
          <thead class="bg-[var(--c-surface-sunken)] text-left text-xs text-[var(--c-text-muted)]">
            <tr>
              <th scope="col" class="px-3 py-2 font-medium">Vendor</th>
              <th scope="col" class="px-3 py-2 text-right font-medium">Bindings</th>
              <th scope="col" class="px-3 py-2 text-right font-medium">Share</th>
            </tr>
          </thead>
          <tbody class="divide-y">
            <tr v-for="row in rows" :key="row.vendor">
              <td class="px-3 py-2">{{ row.vendor }}</td>
              <td class="px-3 py-2 text-right">{{ row.bindings }}</td>
              <td class="px-3 py-2 text-right">{{ row.share }}</td>
            </tr>
          </tbody>
        </table>
      </div>

      <BarChart :data="bars" height="auto" unit="bindings" />

      <form class="flex flex-col gap-3" @submit.prevent>
        <TextField v-model="sample" label="Model" hint="A field, with its hint." />
        <Toggle v-model="on" label="A switch" description="With what it does, beneath it." />
        <div class="flex flex-wrap gap-2">
          <Button variant="primary">Primary</Button>
          <Button>Secondary</Button>
        </div>
      </form>
    </div>
  </Card>
</template>
