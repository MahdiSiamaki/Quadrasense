<script setup lang="ts">
import { computed, ref } from 'vue'
import Card from '@/design-system/Card.vue'
import Button from '@/design-system/Button.vue'
import Modal from '@/design-system/Modal.vue'
import AsyncBoundary from '@/design-system/AsyncBoundary.vue'
import IntegrationClientDialog from './IntegrationClientDialog.vue'
import IssuedSecretDialog from './IssuedSecretDialog.vue'
import { ApiError } from '@/api/client'
import {
  useIntegrationClients,
  useRotateIntegrationSecret,
  type IntegrationClient,
  type IssuedSecret,
} from '@/api/integration'
import { formatDateTime, formatRelative } from '@/lib/format'

/**
 * The systems that call PI's integration API (ADR-024): who they are, what each may ask for, and
 * whether it still does.
 *
 * Until 2026-11-12 this was an API call only (`/api/v1/integration/clients`); the routes, their
 * checks and their audit are unchanged - this page is what an administrator reads and presses.
 *
 * "Last token" is the column worth looking at. A system that has not asked for a token in weeks
 * is an integration nobody noticed stopping, or a secret that should be withdrawn.
 */
const clients = useIntegrationClients()
const rotate = useRotateIntegrationSecret()

/** The dialog: null closed, 'new' registering, a client changing it. */
const editing = ref<IntegrationClient | 'new' | null>(null)
const renewing = ref<IntegrationClient | null>(null)
const issued = ref<IssuedSecret | null>(null)
const renewProblem = ref<string | null>(null)

const scopes = computed(() => clients.data.value?.scopes ?? [])
const list = computed(() =>
  [...(clients.data.value?.clients ?? [])].sort(
    (a, b) => Number(b.isActive) - Number(a.isActive) || a.displayName.localeCompare(b.displayName),
  ),
)

function onIssued(secret: IssuedSecret) {
  editing.value = null
  issued.value = secret
}

async function renew() {
  if (!renewing.value) return
  renewProblem.value = null

  try {
    const secret = await rotate.mutateAsync(renewing.value.id)
    renewing.value = null
    issued.value = secret
  } catch (error) {
    renewProblem.value =
      error instanceof ApiError ? (error.problem?.detail ?? error.problem?.title ?? null) : 'Could not reach the server.'
  }
}
</script>

<template>
  <div class="flex flex-col gap-5">
    <header class="flex flex-wrap items-end justify-between gap-3">
      <div>
        <h2 class="text-lg font-semibold tracking-tight">API clients</h2>
        <p class="mt-0.5 text-sm text-[var(--c-text-secondary)]">
          The systems that call PI's integration API, what each may ask for, and when it last did.
        </p>
      </div>
      <Button variant="primary" @click="editing = 'new'">Register a system</Button>
    </header>

    <Card flush>
      <AsyncBoundary
        :is-loading="clients.isPending.value"
        :is-error="clients.isError.value"
        :error="clients.error.value"
        :is-empty="list.length === 0"
        empty-message="No system is registered yet. A system calls the integration API with a client id and a secret issued here."
        min-height="14rem"
        gap="none"
        @retry="clients.refetch()"
      >
        <!--
          relative: the visually hidden "Actions" label is absolutely positioned, and a scroll box that
          is not positioned does not contain it - at phone width it sat past the table's end and widened
          the whole page by 81px (measured, 2026-11-12).
        -->
        <div class="relative overflow-x-auto">
          <table class="w-full text-left text-sm">
            <thead class="border-b text-2xs tracking-wide whitespace-nowrap text-[var(--c-text-muted)] uppercase">
              <tr>
                <th scope="col" class="px-4 py-2 font-medium">System</th>
                <th scope="col" class="px-4 py-2 font-medium">May ask for</th>
                <th scope="col" class="px-4 py-2 font-medium">Status</th>
                <th scope="col" class="px-4 py-2 font-medium">Last token</th>
                <th scope="col" class="px-4 py-2 font-medium"><span class="sr-only">Actions</span></th>
              </tr>
            </thead>
            <tbody class="divide-y">
              <tr v-for="client in list" :key="client.id" class="align-top hover:bg-[var(--c-surface-hover)]">
                <td class="px-4 py-2.5">
                  <p class="font-medium">{{ client.displayName }}</p>
                  <p class="font-mono text-2xs text-[var(--c-text-muted)]">{{ client.clientId }}</p>
                  <p v-if="client.description" class="mt-0.5 max-w-prose text-2xs text-[var(--c-text-secondary)]">
                    {{ client.description }}
                  </p>
                </td>

                <td class="px-4 py-2.5">
                  <div class="flex flex-wrap gap-1">
                    <span
                      v-for="scope in client.scopes"
                      :key="scope"
                      class="rounded-full bg-[var(--c-surface-sunken)] px-1.5 py-0.5 font-mono text-2xs text-[var(--c-text-secondary)]"
                      :title="scopes.find((s) => s.scope === scope)?.description"
                    >
                      {{ scope }}
                    </span>
                  </div>
                </td>

                <td class="px-4 py-2.5">
                  <span
                    class="inline-flex items-center gap-1.5 text-xs"
                    :style="{ color: client.isActive ? 'var(--c-text-secondary)' : 'var(--c-text-muted)' }"
                  >
                    <span
                      class="size-1.5 rounded-full"
                      :style="{ backgroundColor: client.isActive ? 'var(--c-success)' : 'var(--c-text-muted)' }"
                      aria-hidden="true"
                    />
                    {{ client.isActive ? 'Active' : 'Deactivated' }}
                  </span>
                  <p class="mt-0.5 text-2xs text-[var(--c-text-muted)]" :title="formatDateTime(client.secretSetAt)">
                    secret set {{ formatRelative(client.secretSetAt) }}
                  </p>
                </td>

                <td class="tabular px-4 py-2.5 text-xs text-[var(--c-text-secondary)]">
                  <span v-if="client.lastTokenAt" :title="formatDateTime(client.lastTokenAt)">
                    {{ formatRelative(client.lastTokenAt) }}
                  </span>
                  <span v-else class="text-[var(--c-text-muted)]">never</span>
                </td>

                <td class="px-4 py-2.5">
                  <div class="flex justify-end gap-1.5">
                    <Button size="sm" :aria-label="`Change ${client.displayName}`" @click="editing = client">
                      Change
                    </Button>
                    <Button
                      size="sm"
                      :disabled="!client.isActive"
                      :aria-label="`Issue a new secret for ${client.displayName}`"
                      :title="client.isActive ? undefined : 'Activate it first: a deactivated system obtains no token.'"
                      @click="(renewing = client), (renewProblem = null)"
                    >
                      New secret
                    </Button>
                  </div>
                </td>
              </tr>
            </tbody>
          </table>
        </div>
      </AsyncBoundary>
    </Card>

    <IntegrationClientDialog
      :open="editing !== null"
      :client="editing === 'new' ? null : editing"
      :scopes="scopes"
      @close="editing = null"
      @issued="onIssued"
    />

    <!-- Withdrawing a secret breaks the system until it is given the new one: asked, not assumed. -->
    <Modal
      :open="renewing !== null"
      size="sm"
      :title="`A new secret for ${renewing?.displayName ?? ''}`"
      description="The current secret stops working now, and so does every token issued with it. The system can call nothing until it holds the new one."
      :busy="rotate.isPending.value"
      @close="renewing = null"
      @submit="renew"
    >
      <p v-if="renewProblem" class="text-xs text-[var(--c-danger-text)]" role="alert">{{ renewProblem }}</p>
      <template #actions>
        <Button :disabled="rotate.isPending.value" @click="renewing = null">Cancel</Button>
        <Button type="submit" variant="danger" :pending="rotate.isPending.value">Issue a new secret</Button>
      </template>
    </Modal>

    <IssuedSecretDialog :issued="issued" @close="issued = null" />
  </div>
</template>
