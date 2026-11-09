<script setup lang="ts">
import { computed } from 'vue'
import Button from '@/design-system/Button.vue'
import SegmentedControl, { type SegmentOption } from '@/design-system/SegmentedControl.vue'
import AppearancePreview from './AppearancePreview.vue'
import { optionsOf, useAppearance, type AppearanceKey } from '@/lib/appearance'

/**
 * How the application looks, for this person.
 *
 * Nothing here has a Save button. A choice applies to the whole application at once, which is the
 * live preview, and it is written to the account shortly after, so it follows the person to another
 * browser. "Reset to defaults" is the way back, and it is always there, because the difference
 * between one setting and the default is hard to remember after a week.
 *
 * The choices are few on purpose. Each is a step that has been looked at, and an accent cannot be a
 * colour that fails contrast or that means danger, which free choice would allow. ADR-023.
 */
const { appearance, isDefault, saveState, set, reset } = useAppearance()

const LABELS: Partial<Record<AppearanceKey, Record<string, string>>> = {
  fontSize: { small: 'Small', medium: 'Medium', large: 'Large', larger: 'Larger' },
  fontWeight: { light: 'Light', regular: 'Regular', medium: 'Medium' },
  lineSpacing: { tight: 'Tight', normal: 'Normal', relaxed: 'Relaxed' },
  density: { compact: 'Compact', normal: 'Normal', comfortable: 'Comfortable' },
  corners: { square: 'Square', normal: 'Normal', round: 'Round' },
  motion: { system: 'System', full: 'Full', reduced: 'Reduced', none: 'None' },
}

function segments(key: AppearanceKey): SegmentOption<string>[] {
  return optionsOf(key).map((value) => ({ value, label: LABELS[key]?.[value] ?? value }))
}

const themes = [
  { value: 'system', label: 'System', hint: 'Follows your operating system, and changes with it.' },
  { value: 'light', label: 'Light', hint: 'Always light.' },
  { value: 'dark', label: 'Dark', hint: 'Always dark.' },
]

const typefaces = [
  { value: 'system', label: 'System', hint: 'What your computer uses. Nothing to download.' },
  { value: 'inter', label: 'Inter', hint: 'Drawn for screens and small sizes.' },
  { value: 'plex', label: 'IBM Plex Sans', hint: 'Plainer, with a little more character.' },
  {
    value: 'atkinson',
    label: 'Atkinson Hyperlegible',
    hint: 'Drawn so that letters are hard to confuse, for low vision.',
  },
]

const accents: Record<string, string> = {
  blue: 'Blue',
  sky: 'Sky',
  teal: 'Teal',
  violet: 'Violet',
  magenta: 'Magenta',
  slate: 'Slate',
}

const motionHint = computed(
  () =>
    ({
      system: 'Follows your operating system, and changes with it.',
      full: 'Everything the interface does, even if your system asks for less.',
      reduced: 'Nothing moves or turns. Fades stay, because they explain a change.',
      none: 'Everything happens at once. Spinners stand still too.',
    })[appearance.motion],
)

const saveMessage = computed(
  () =>
    ({
      idle: null,
      saving: 'Saving…',
      saved: 'Saved to your account.',
      failed: 'Kept in this browser, but it could not be saved to your account.',
    })[saveState.value],
)
</script>

<template>
  <div class="flex flex-col gap-5">
    <header class="flex flex-wrap items-start justify-between gap-x-6 gap-y-2">
      <div>
        <h2 class="text-lg font-semibold tracking-tight">Appearance</h2>
        <p class="mt-0.5 text-sm text-[var(--c-text-secondary)]">
          Saved to your account, so it follows you to any browser. It changes nothing for anyone
          else.
        </p>
      </div>

      <div class="flex items-center gap-3">
        <p
          class="text-xs"
          :class="saveState === 'failed' ? 'text-[var(--c-warning-text)]' : 'text-[var(--c-text-muted)]'"
          role="status"
        >
          {{ saveMessage }}
        </p>
        <Button size="sm" :disabled="isDefault" @click="reset">Reset to defaults</Button>
      </div>
    </header>

    <div class="grid items-start gap-5 lg:grid-cols-[minmax(0,1fr)_23rem]">
      <div class="flex min-w-0 flex-col gap-6">
        <fieldset class="flex flex-col gap-2">
          <legend class="mb-2 text-sm font-medium">Theme</legend>
          <div class="grid gap-3 sm:grid-cols-3">
            <label
              v-for="option in themes"
              :key="option.value"
              class="flex cursor-pointer flex-col gap-3 rounded-[var(--radius-lg)] border p-3 transition-colors hover:bg-[var(--c-surface-hover)] has-[:focus-visible]:outline-2 has-[:focus-visible]:outline-offset-2 has-[:focus-visible]:outline-[var(--c-accent)]"
              :class="
                appearance.theme === option.value
                  ? 'border-[var(--c-accent)] bg-[var(--c-surface-sunken)]'
                  : ''
              "
            >
              <!-- A miniature of the page in that theme: bar, card, a line of text. -->
              <span class="preview" :class="`preview--${option.value}`" aria-hidden="true">
                <span class="half half-light"><i /><b /><b /></span>
                <span class="half half-dark"><i /><b /><b /></span>
              </span>

              <span class="flex items-start gap-2">
                <input
                  type="radio"
                  name="theme"
                  :value="option.value"
                  :checked="appearance.theme === option.value"
                  class="mt-0.5 accent-[var(--c-accent)] focus-visible:outline-none"
                  @change="set('theme', option.value)"
                />
                <span>
                  <span class="block text-sm font-medium">{{ option.label }}</span>
                  <span class="block text-xs text-[var(--c-text-muted)]">{{ option.hint }}</span>
                </span>
              </span>
            </label>
          </div>
        </fieldset>

        <fieldset class="flex flex-col gap-2">
          <legend class="mb-2 text-sm font-medium">Typeface</legend>
          <div class="grid gap-3 sm:grid-cols-2">
            <label
              v-for="option in typefaces"
              :key="option.value"
              class="flex cursor-pointer items-start gap-2 rounded-[var(--radius-lg)] border p-3 transition-colors hover:bg-[var(--c-surface-hover)] has-[:focus-visible]:outline-2 has-[:focus-visible]:outline-offset-2 has-[:focus-visible]:outline-[var(--c-accent)]"
              :class="
                appearance.fontFamily === option.value
                  ? 'border-[var(--c-accent)] bg-[var(--c-surface-sunken)]'
                  : ''
              "
            >
              <input
                type="radio"
                name="typeface"
                :value="option.value"
                :checked="appearance.fontFamily === option.value"
                class="mt-0.5 accent-[var(--c-accent)] focus-visible:outline-none"
                @change="set('fontFamily', option.value)"
              />
              <span>
                <span class="block text-sm font-medium">{{ option.label }}</span>
                <span class="block text-xs text-[var(--c-text-muted)]">{{ option.hint }}</span>
              </span>
            </label>
          </div>
        </fieldset>

        <div class="grid gap-x-6 gap-y-5 sm:grid-cols-2">
          <div class="flex flex-col gap-1.5">
            <span class="text-sm font-medium">Text size</span>
            <SegmentedControl
              class="self-start"
              :model-value="appearance.fontSize"
              :options="segments('fontSize')"
              label="Text size"
              @update:model-value="set('fontSize', $event)"
            />
          </div>

          <div class="flex flex-col gap-1.5">
            <span class="text-sm font-medium">Text weight</span>
            <SegmentedControl
              class="self-start"
              :model-value="appearance.fontWeight"
              :options="segments('fontWeight')"
              label="Text weight"
              @update:model-value="set('fontWeight', $event)"
            />
          </div>

          <div class="flex flex-col gap-1.5">
            <span class="text-sm font-medium">Line spacing</span>
            <SegmentedControl
              class="self-start"
              :model-value="appearance.lineSpacing"
              :options="segments('lineSpacing')"
              label="Line spacing"
              @update:model-value="set('lineSpacing', $event)"
            />
          </div>

          <div class="flex flex-col gap-1.5">
            <span class="text-sm font-medium">Tables and forms</span>
            <SegmentedControl
              class="self-start"
              :model-value="appearance.density"
              :options="segments('density')"
              label="Density of tables and forms"
              @update:model-value="set('density', $event)"
            />
          </div>

          <div class="flex flex-col gap-1.5">
            <span class="text-sm font-medium">Corners</span>
            <SegmentedControl
              class="self-start"
              :model-value="appearance.corners"
              :options="segments('corners')"
              label="Corners"
              @update:model-value="set('corners', $event)"
            />
          </div>
        </div>

        <fieldset class="flex flex-col gap-2">
          <legend class="mb-2 text-sm font-medium">Accent colour</legend>
          <div class="flex flex-wrap gap-x-4 gap-y-2">
            <label
              v-for="(label, value) in accents"
              :key="value"
              class="flex cursor-pointer items-center gap-2 rounded-[var(--radius-md)] py-1 pr-2 pl-1 text-sm has-[:focus-visible]:outline-2 has-[:focus-visible]:outline-offset-2 has-[:focus-visible]:outline-[var(--c-accent)]"
            >
              <input
                type="radio"
                name="accent"
                class="sr-only"
                :value="value"
                :checked="appearance.accent === value"
                @change="set('accent', String(value))"
              />
              <span
                class="swatch"
                :class="{ 'is-chosen': appearance.accent === value }"
                :data-swatch="value"
                aria-hidden="true"
              />
              {{ label }}
            </label>
          </div>
          <p class="text-xs text-[var(--c-text-muted)]">
            None of them is red, amber or green: those mean danger, warning and success here.
          </p>
        </fieldset>

        <div class="flex flex-col gap-1.5">
          <span class="text-sm font-medium">Animation</span>
          <SegmentedControl
            class="self-start"
            :model-value="appearance.motion"
            :options="segments('motion')"
            label="Animation"
            @update:model-value="set('motion', $event)"
          />
          <p class="text-xs text-[var(--c-text-muted)]">{{ motionHint }}</p>
        </div>
      </div>

      <div class="min-w-0 lg:sticky lg:top-4">
        <AppearancePreview />
      </div>
    </div>
  </div>
</template>

<style scoped>
/*
  Each preview shows one theme whichever theme is active, so it cannot read the tokens: they are
  bound to :root and change with the theme. The values are copied from tokens.css - canvas,
  surface, border, muted text - so the miniature is the real palette, not an impression of it.
*/
.preview {
  display: flex;
  height: 4.5rem;
  overflow: hidden;
  border-radius: var(--radius-md);
  border: 1px solid var(--c-border);
}

.half {
  display: flex;
  flex: 1;
  flex-direction: column;
  gap: 0.3rem;
  padding: 0.5rem;
}

.half i {
  display: block;
  height: 0.5rem;
  border-radius: 2px;
}

.half b {
  display: block;
  height: 0.35rem;
  width: 70%;
  border-radius: 2px;
}

.half b + b {
  width: 45%;
}

.half-light {
  background: oklch(98.4% 0.002 250);
}

.half-light i {
  background: oklch(100% 0 0);
  box-shadow: 0 0 0 1px oklch(91.5% 0.005 250);
}

.half-light b {
  background: oklch(53% 0.011 250 / 0.45);
}

.half-dark {
  background: oklch(17.5% 0.008 260);
}

.half-dark i {
  background: oklch(21% 0.009 260);
  box-shadow: 0 0 0 1px oklch(29% 0.011 260);
}

.half-dark b {
  background: oklch(65% 0.011 260 / 0.45);
}

/* System shows both halves; light and dark only their own. */
.preview--light .half-dark,
.preview--dark .half-light {
  display: none;
}

/*
  A swatch shows its own colour whichever accent is active, so like the previews above it cannot
  read the token. The chosen one is ringed, so the choice does not rest on telling colours apart.
*/
.swatch {
  display: block;
  width: 1.25rem;
  height: 1.25rem;
  flex-shrink: 0;
  border-radius: 9999px;
  box-shadow: 0 0 0 1px var(--c-border-strong);
}

.swatch.is-chosen {
  box-shadow:
    0 0 0 2px var(--c-surface),
    0 0 0 4px var(--c-text);
}

.swatch[data-swatch='blue'] {
  background: oklch(53% 0.17 264);
}

.swatch[data-swatch='sky'] {
  background: oklch(50% 0.115 240);
}

.swatch[data-swatch='teal'] {
  background: oklch(50% 0.085 200);
}

.swatch[data-swatch='violet'] {
  background: oklch(53% 0.19 300);
}

.swatch[data-swatch='magenta'] {
  background: oklch(53% 0.18 345);
}

.swatch[data-swatch='slate'] {
  background: oklch(50% 0.035 255);
}
</style>
