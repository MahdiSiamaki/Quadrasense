<script setup lang="ts">
import { computed, ref } from 'vue'
import { fieldsOf, type OpenApiDocument, type OpenApiSchema } from '@/lib/openapi'

/**
 * The fields of one object, each with its type, whether it is required, and what it means; an object
 * inside it opens below on request, to a few levels and never into a loop.
 *
 * A list rather than a table: a field's description is the part people read, and a table would
 * squeeze it into a column - or, scrolling sideways at phone width, hide it.
 */
const props = withDefaults(
  defineProps<{
    doc: OpenApiDocument
    schema: OpenApiSchema | undefined
    /** How far down this list is, from the route's own body or answer. */
    depth?: number
    /** The named schemas already open above, so a schema that holds itself stops. */
    trail?: readonly string[]
  }>(),
  { depth: 0, trail: () => [] },
)

const MAX_DEPTH = 4

const fields = computed(() => fieldsOf(props.doc, props.schema))

/**
 * What is open. A level is drawn only once it is opened: drawn ahead, closed, the larger answers
 * would put thousands of rows nobody asked for into the page.
 */
const opened = ref(new Set<string>())

function toggled(name: string, event: Event) {
  const next = new Set(opened.value)
  if ((event.target as HTMLDetailsElement).open) next.add(name)
  else next.delete(name)
  opened.value = next
}
</script>

<template>
  <ul v-if="fields.length" class="divide-y rounded-[var(--radius-md)] border">
    <li v-for="field in fields" :key="field.name" class="px-3 py-2">
      <div class="flex flex-wrap items-baseline gap-x-2 gap-y-0.5">
        <code class="font-mono text-xs font-semibold text-[var(--c-text)]">{{ field.name }}</code>
        <span class="font-mono text-2xs text-[var(--c-text-muted)] wrap-anywhere">{{ field.type }}</span>
        <span v-if="field.required" class="text-2xs font-medium text-[var(--c-warning-text)]">required</span>
      </div>
      <p v-if="field.description" class="mt-0.5 text-xs text-[var(--c-text-secondary)]">{{ field.description }}</p>

      <details
        v-if="field.nested && depth < MAX_DEPTH && !(field.nestedName && trail.includes(field.nestedName))"
        class="group mt-1.5"
        @toggle="toggled(field.name, $event)"
      >
        <summary class="cursor-pointer text-2xs font-medium text-[var(--c-accent)] select-none hover:underline">
          Fields of {{ field.nestedName ?? field.name }}
        </summary>
        <div v-if="opened.has(field.name)" class="mt-1.5">
          <SchemaFields
            :doc="doc"
            :schema="field.nested"
            :depth="depth + 1"
            :trail="field.nestedName ? [...trail, field.nestedName] : trail"
          />
        </div>
      </details>
    </li>
  </ul>
  <p v-else class="text-xs text-[var(--c-text-muted)]">No fields.</p>
</template>
