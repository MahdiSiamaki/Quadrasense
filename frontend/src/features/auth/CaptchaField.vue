<script setup lang="ts">
import { useId } from 'vue'
import Button from '@/design-system/Button.vue'

/**
 * The Persian captcha's field (ADR-026; MCI red line 2-3 row 3): the picture, another on request, the
 * answer, and an on-screen keyboard holding exactly the characters a captcha may contain - so someone
 * without a Persian keyboard layout can still answer, as the red line asks. The only Persian in the
 * product, and the only part of it set right to left.
 *
 * The keys come from the server with each picture, so the keyboard can never offer a character the
 * captcha does not use, or lack one it does.
 */
const props = withDefaults(
  defineProps<{
    /** The picture, as a data URL; null while one is being drawn. */
    image: string | null
    /** Every character a captcha may hold. */
    alphabet: string
    modelValue: string
    loading?: boolean
    disabled?: boolean
  }>(),
  { loading: false, disabled: false },
)

const emit = defineEmits<{ 'update:modelValue': [value: string]; refresh: [] }>()

const id = useId()

/** Room for the five characters and a few slips; nothing longer is an answer. */
const MAX_LENGTH = 12

function press(key: string) {
  if (props.modelValue.length < MAX_LENGTH) emit('update:modelValue', props.modelValue + key)
}

function backspace() {
  // Every key is one character of the Basic Multilingual Plane, so one code unit is one character.
  emit('update:modelValue', props.modelValue.slice(0, -1))
}
</script>

<template>
  <fieldset class="flex min-w-0 flex-col gap-2.5 rounded-[var(--radius-md)] border p-3" :disabled="disabled">
    <legend class="px-1 text-xs font-medium text-[var(--c-text-secondary)]">Security check</legend>

    <div class="flex flex-wrap items-center gap-2">
      <!-- Drawn at 520 x 168 and shown at 312 x 101: at the drawn size's half, the characters came out
           thin on an ordinary screen; a person has to read what an OCR cannot. -->
      <div
        class="grid aspect-[260/84] w-[312px] max-w-full place-items-center overflow-hidden rounded-[var(--radius-sm)] border bg-[var(--c-surface-sunken)]"
      >
        <img
          v-if="image"
          :src="image"
          width="312"
          height="101"
          alt="Persian letters and digits to type into the box below"
          class="h-full w-full object-contain"
        />
        <span v-else class="text-xs text-[var(--c-text-muted)]">{{ loading ? 'Drawing a picture…' : 'No picture' }}</span>
      </div>
      <Button type="button" size="sm" :disabled="loading || disabled" @click="emit('refresh')">New picture</Button>
    </div>

    <div class="flex flex-col gap-1">
      <label :for="id" class="text-xs font-medium text-[var(--c-text-secondary)]">
        Type the characters in the picture, right to left
      </label>
      <input
        :id="id"
        :value="modelValue"
        dir="rtl"
        lang="fa"
        autocomplete="off"
        autocapitalize="off"
        spellcheck="false"
        inputmode="text"
        :maxlength="MAX_LENGTH"
        class="font-persian w-full rounded-[var(--radius-md)] border bg-[var(--c-surface)] px-2.5 py-1.5 text-lg tracking-widest text-[var(--c-text)] disabled:opacity-60"
        @input="emit('update:modelValue', ($event.target as HTMLInputElement).value)"
      />
    </div>

    <!-- Six keys a row on a phone: eight left 22 px keys at 320 px, under WCAG 2.5.8's 24 px target. -->
    <div role="group" aria-label="Persian keyboard" lang="fa" dir="rtl" class="grid grid-cols-6 gap-1 sm:grid-cols-8">
      <button
        v-for="key in alphabet"
        :key="key"
        type="button"
        class="font-persian grid h-9 place-items-center rounded-[var(--radius-sm)] border bg-[var(--c-surface)] text-lg text-[var(--c-text)] hover:bg-[var(--c-surface-hover)] disabled:opacity-60"
        @click="press(key)"
      >
        {{ key }}
      </button>
      <button
        type="button"
        aria-label="Delete the last character"
        class="col-span-2 grid h-9 place-items-center rounded-[var(--radius-sm)] border bg-[var(--c-surface)] text-sm text-[var(--c-text-secondary)] hover:bg-[var(--c-surface-hover)] disabled:opacity-60"
        @click="backspace"
      >
        ⌫
      </button>
    </div>
  </fieldset>
</template>
