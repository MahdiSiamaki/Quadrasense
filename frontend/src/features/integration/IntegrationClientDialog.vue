<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import Modal from '@/design-system/Modal.vue'
import Button from '@/design-system/Button.vue'
import TextField from '@/design-system/TextField.vue'
import Toggle from '@/design-system/Toggle.vue'
import { ApiError } from '@/api/client'
import {
  useCreateIntegrationClient,
  useUpdateIntegrationClient,
  type IntegrationClient,
  type IntegrationScope,
  type IssuedSecret,
} from '@/api/integration'

/**
 * Registers a system, or changes one.
 *
 * The client id is chosen once and never changed: it is what the system sends with every token
 * request, and renaming it would break that system without a word. Everything else - the name a
 * person reads, why it is registered, what it may ask for, whether it may ask at all - can change.
 *
 * Scopes are listed with what each one allows, because a scope's name says little: whoever ticks
 * `inventory.reveal` should read that it shows whole numbers, IMSIs and IMEIs.
 */
const props = defineProps<{
  open: boolean
  /** The client to change; none registers a new one. */
  client: IntegrationClient | null
  scopes: IntegrationScope[]
}>()

const emit = defineEmits<{ close: []; issued: [secret: IssuedSecret] }>()

const create = useCreateIntegrationClient()
const update = useUpdateIntegrationClient()

const clientId = ref('')
const displayName = ref('')
const description = ref('')
const chosen = ref<string[]>([])
const active = ref(true)

watch(
  () => props.open,
  (open) => {
    if (!open) return
    clientId.value = props.client?.clientId ?? ''
    displayName.value = props.client?.displayName ?? ''
    description.value = props.client?.description ?? ''
    chosen.value = [...(props.client?.scopes ?? [])]
    active.value = props.client?.isActive ?? true
    create.reset()
    update.reset()
  },
)

const editing = computed(() => props.client !== null)
const pending = computed(() => create.isPending.value || update.isPending.value)
const error = computed(() => create.error.value ?? update.error.value)

const fieldErrors = computed(() => {
  const e = error.value
  if (!(e instanceof ApiError)) return {} as Record<string, string>
  return Object.fromEntries(e.fieldErrors.map((f) => [f.field, f.message]))
})

const unexpected = computed(() => {
  const e = error.value
  if (!e) return null
  if (e instanceof ApiError && e.status === 400) return null
  if (e instanceof ApiError) return e.problem?.detail ?? e.problem?.title ?? null
  return 'Could not reach the server.'
})

/** The id's own rule, as the server holds it, said before the server has to. */
const CLIENT_ID = /^[a-z][a-z0-9-]{2,47}$/

const canSubmit = computed(
  () =>
    (editing.value || CLIENT_ID.test(clientId.value.trim())) &&
    displayName.value.trim().length > 0 &&
    chosen.value.length > 0 &&
    !pending.value,
)

/** Turning a client off ends every token it holds: said where the switch is, not after. */
const deactivating = computed(() => editing.value && props.client?.isActive === true && !active.value)

function toggleScope(scope: string) {
  chosen.value = chosen.value.includes(scope) ? chosen.value.filter((s) => s !== scope) : [...chosen.value, scope]
}

async function submit() {
  if (!canSubmit.value) return

  try {
    if (props.client) {
      await update.mutateAsync({
        id: props.client.id,
        change: {
          displayName: displayName.value.trim(),
          description: description.value.trim(),
          scopes: chosen.value,
          isActive: active.value,
        },
      })
      emit('close')
      return
    }

    const issued = await create.mutateAsync({
      clientId: clientId.value.trim(),
      displayName: displayName.value.trim(),
      description: description.value.trim(),
      scopes: chosen.value,
    })
    emit('issued', issued)
  } catch {
    // Rendered from the mutation's error.
  }
}
</script>

<template>
  <Modal
    :open="open"
    :title="editing ? `Change ${client?.displayName}` : 'Register a system'"
    :description="
      editing
        ? 'Its client id stays as it is: the system sends it with every request for a token.'
        : 'It gets a client id and a secret. The secret is shown once, when it is issued.'
    "
    :busy="pending"
    @close="emit('close')"
    @submit="submit"
  >
    <div class="flex flex-col gap-4">
      <div class="grid gap-3.5 sm:grid-cols-2">
        <TextField
          v-if="!editing"
          v-model="clientId"
          label="Client id"
          required
          autofocus
          autocomplete="off"
          :maxlength="48"
          :error="fieldErrors['clientId'] ?? null"
          hint="Lower-case letters, digits and hyphens: lodestone. Cannot be changed later."
        />
        <TextField
          v-model="displayName"
          label="Name"
          required
          :autofocus="editing"
          :maxlength="100"
          :error="fieldErrors['displayName'] ?? null"
          hint="What people read: Lodestone CRM."
        />
      </div>

      <TextField
        v-model="description"
        label="Why it is registered"
        optional-note="optional"
        :maxlength="500"
        :error="fieldErrors['description'] ?? null"
        hint="Who asked for it, and what it reads. The audit log names the client; this says why."
      />

      <fieldset>
        <legend class="text-xs font-medium text-[var(--c-text-secondary)]">What it may ask for</legend>
        <p v-if="fieldErrors['scopes']" class="mt-1 text-xs text-[var(--c-danger-text)]" role="alert">
          {{ fieldErrors['scopes'] }}
        </p>

        <div class="mt-2 flex flex-col gap-1.5">
          <label
            v-for="scope in scopes"
            :key="scope.scope"
            class="flex cursor-pointer items-start gap-2.5 rounded-[var(--radius-md)] border px-2.5 py-2 hover:bg-[var(--c-surface-hover)]"
            :style="
              chosen.includes(scope.scope)
                ? { borderColor: 'var(--c-accent)', backgroundColor: 'var(--c-accent-subtle)' }
                : undefined
            "
          >
            <input
              type="checkbox"
              class="mt-0.5"
              :checked="chosen.includes(scope.scope)"
              @change="toggleScope(scope.scope)"
            />
            <span class="min-w-0">
              <span class="block font-mono text-xs font-medium">{{ scope.scope }}</span>
              <span class="block text-xs text-[var(--c-text-muted)]">{{ scope.description }}</span>
            </span>
          </label>
        </div>

        <p v-if="chosen.length === 0" class="mt-2 text-xs text-[var(--c-warning-text)]">
          With no scope, a system can obtain a token and read nothing with it.
        </p>
      </fieldset>

      <template v-if="editing">
        <Toggle
          v-model="active"
          label="Active"
          description="A deactivated system can obtain no token, and the tokens it holds stop working at once."
          tone="success"
        />
        <p v-if="deactivating" class="text-xs text-[var(--c-warning-text)]" role="status">
          Saving this ends every token {{ client?.displayName }} holds now.
        </p>
      </template>

      <p
        v-if="unexpected"
        class="rounded-[var(--radius-md)] border px-2.5 py-2 text-xs"
        :style="{
          borderColor: 'var(--c-danger)',
          backgroundColor: 'var(--c-danger-subtle)',
          color: 'var(--c-danger-text)',
        }"
        role="alert"
      >
        {{ unexpected }}
      </p>
    </div>

    <template #actions>
      <Button :disabled="pending" @click="emit('close')">Cancel</Button>
      <Button type="submit" variant="primary" :pending="pending" :disabled="!canSubmit">
        {{ editing ? 'Save' : 'Register and issue a secret' }}
      </Button>
    </template>
  </Modal>
</template>
