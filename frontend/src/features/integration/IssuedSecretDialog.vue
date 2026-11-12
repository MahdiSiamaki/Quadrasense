<script setup lang="ts">
import { ref, watch } from 'vue'
import Modal from '@/design-system/Modal.vue'
import Button from '@/design-system/Button.vue'
import type { IssuedSecret } from '@/api/integration'

/**
 * A secret, the one time it can be seen.
 *
 * PI keeps only the secret's hash (ADR-024), so this dialog is the secret's whole life on this side:
 * when it closes, the page lets go of it and nothing can show it again - a lost secret is replaced,
 * not recovered. The dialog says so before it is closed, not after.
 *
 * Copying goes through the clipboard where the browser allows it; where it does not - a page served
 * over plain HTTP - the field selects itself on focus, and the person copies by hand.
 */
const props = defineProps<{ issued: IssuedSecret | null }>()
const emit = defineEmits<{ close: [] }>()

const copied = ref<'id' | 'secret' | null>(null)

watch(
  () => props.issued,
  () => (copied.value = null),
)

async function copy(what: 'id' | 'secret', text: string) {
  try {
    await navigator.clipboard.writeText(text)
    copied.value = what
  } catch {
    // No clipboard here: the field is selected on focus, for a copy by hand.
    copied.value = null
  }
}

function select(event: FocusEvent) {
  ;(event.target as HTMLInputElement).select()
}
</script>

<template>
  <Modal
    :open="issued !== null"
    :title="`The secret for ${issued?.client.displayName ?? ''}`"
    description="Shown this once. PI keeps only a hash of it, so it cannot be shown again: a lost secret is replaced, not recovered."
    @close="emit('close')"
    @submit="emit('close')"
  >
    <div v-if="issued" class="flex flex-col gap-3.5">
      <div>
        <label for="issued-client-id" class="text-xs font-medium text-[var(--c-text-secondary)]">client_id</label>
        <div class="mt-1 flex items-center gap-2">
          <input
            id="issued-client-id"
            :value="issued.client.clientId"
            readonly
            class="min-w-0 flex-1 rounded-[var(--radius-md)] border bg-[var(--c-surface-sunken)] px-2.5 py-1.5 font-mono text-xs"
            @focus="select"
          />
          <Button size="sm" @click="copy('id', issued.client.clientId)">
            {{ copied === 'id' ? 'Copied' : 'Copy' }}
          </Button>
        </div>
      </div>

      <div>
        <label for="issued-secret" class="text-xs font-medium text-[var(--c-text-secondary)]">client_secret</label>
        <div class="mt-1 flex items-center gap-2">
          <input
            id="issued-secret"
            :value="issued.clientSecret"
            readonly
            autocomplete="off"
            spellcheck="false"
            class="min-w-0 flex-1 rounded-[var(--radius-md)] border bg-[var(--c-surface-sunken)] px-2.5 py-1.5 font-mono text-xs"
            @focus="select"
          />
          <Button size="sm" variant="primary" @click="copy('secret', issued.clientSecret)">
            {{ copied === 'secret' ? 'Copied' : 'Copy' }}
          </Button>
        </div>
      </div>

      <p
        class="rounded-[var(--radius-md)] border px-2.5 py-2 text-xs"
        :style="{
          borderColor: 'var(--c-warning)',
          backgroundColor: 'var(--c-warning-subtle)',
          color: 'var(--c-warning-text)',
        }"
        role="note"
      >
        Put it straight into the system's own secret store. Not in a message, a ticket or a document:
        whoever holds it is that system, to PI.
      </p>
    </div>

    <template #actions>
      <Button type="submit" variant="primary">I have stored it</Button>
    </template>
  </Modal>
</template>
