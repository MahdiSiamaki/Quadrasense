import { computed, reactive, readonly, ref, watch } from 'vue'
import contract from './appearance-contract.json'
import { api, ApiError } from '@/api/client'

/**
 * How the application looks, for this person.
 *
 * One model, nine settings, each a value from `appearance-contract.json`: the same file the API
 * validates against, so a value cannot exist on one side only. Each setting becomes one attribute
 * on `<html>`, and `design-system/appearance.css` does the rest by changing tokens, so no component
 * knows about any of this. ADR-023.
 *
 * It lives in two places on purpose. A copy in this browser is what `public/theme-init.js` reads
 * before the first paint, because the server cannot answer in time; the server's copy is what
 * follows a person to another browser. The server's copy wins once it is known.
 */
export type AppearanceKey = keyof typeof contract
export type Appearance = Record<AppearanceKey, string>

// public/theme-init.js repeats these keys and apply()'s rules: change one, change the other.
const STORAGE_KEY = 'sqm.appearance'
const OWNER_KEY = 'sqm.appearance.owner'
const LEGACY_THEME_KEY = 'sqm.theme'

const SAVE_DELAY_MS = 600

const KEYS = Object.keys(contract) as AppearanceKey[]

export const DEFAULTS = Object.fromEntries(
  KEYS.map((key) => [key, contract[key].default]),
) as Appearance

/** The values a setting may take, in the order they are offered. */
export function optionsOf(key: AppearanceKey): readonly string[] {
  return contract[key].values
}

/** Keeps what the contract knows, and fills in the rest. */
export function normalise(source: Partial<Record<string, unknown>> | null | undefined): Appearance {
  const result = { ...DEFAULTS }
  for (const key of KEYS) {
    const value = source?.[key]
    if (typeof value === 'string' && contract[key].values.includes(value)) result[key] = value
  }
  return result
}

function read(key: string): string | null {
  try {
    return localStorage.getItem(key)
  } catch {
    // Private windows and locked-down browsers throw on storage access. A preference is not worth
    // breaking the app over.
    return null
  }
}

function write(key: string, value: string | null) {
  try {
    if (value === null) localStorage.removeItem(key)
    else localStorage.setItem(key, value)
  } catch {
    // Same reasoning: best-effort.
  }
}

function load(): Appearance {
  const stored = read(STORAGE_KEY)

  if (stored) {
    try {
      return normalise(JSON.parse(stored) as Record<string, unknown>)
    } catch {
      return { ...DEFAULTS }
    }
  }

  // The theme alone was kept before there was more to keep.
  return normalise({ theme: read(LEGACY_THEME_KEY) })
}

const state = reactive<Appearance>(load())

/** Whose settings the copy in this browser is. Null: nobody's yet. */
let owner: number | null = Number(read(OWNER_KEY)) || null

/** Set while someone is signed in, which is when a change is worth sending. */
let signedIn = false

const isDark = ref(false)
const prefersDark =
  typeof window !== 'undefined' ? window.matchMedia('(prefers-color-scheme: dark)') : null
const prefersLessMotion =
  typeof window !== 'undefined' ? window.matchMedia('(prefers-reduced-motion: reduce)') : null

/**
 * Charts read the tokens once, into a canvas. This counts the changes that make them stale.
 *
 * Replaces each chart watching the theme: a chart must redraw for an accent or a typeface too, and
 * one signal means the tenth chart does not have to remember the list.
 */
const chartRevision = ref(0)

function resolvedMotion(): 'full' | 'reduced' | 'none' {
  if (state.motion === 'none' || state.motion === 'reduced' || state.motion === 'full') {
    return state.motion
  }
  return prefersLessMotion?.matches ? 'reduced' : 'full'
}

function apply() {
  const dark = state.theme === 'dark' || (state.theme === 'system' && (prefersDark?.matches ?? false))
  const root = document.documentElement
  const theme = dark ? 'dark' : 'light'

  isDark.value = dark

  const attributes: Record<string, string> = {
    theme,
    font: state.fontFamily,
    text: state.fontSize,
    weight: state.fontWeight,
    leading: state.lineSpacing,
    density: state.density,
    accent: state.accent,
    corners: state.corners,
    motion: resolvedMotion(),
  }

  // public/theme-init.js has usually set them before the first paint already.
  if (Object.entries(attributes).every(([name, value]) => root.dataset[name] === value)) return

  // The new colours apply with every transition off (tokens.css), in one style pass; a second
  // pass turns transitions back on, and since nothing changes in it, none starts. Both passes are
  // synchronous: a timer could let a frame render with the theme icon's own morph switched off.
  root.dataset['themeSwitching'] = ''
  Object.assign(root.dataset, attributes)
  void document.body?.offsetWidth
  delete root.dataset['themeSwitching']
  void document.body?.offsetWidth

  chartRevision.value += 1
}

const FONT_FACES: Record<string, string> = {
  inter: 'Inter Variable',
  plex: 'IBM Plex Sans Variable',
  atkinson: 'Atkinson Hyperlegible Next Variable',
}

/**
 * A canvas draws with whatever is loaded at that moment, and a browser fetches a face only once
 * something on the page asks for it. So a chart drawn straight after choosing a typeface would stay
 * in the old one; this redraws them when the new one has arrived.
 */
function redrawChartsWhenFontLoads() {
  const face = FONT_FACES[state.fontFamily]
  if (!face || typeof document === 'undefined' || !document.fonts) return

  void document.fonts.load(`13px "${face}"`).then(() => {
    chartRevision.value += 1
  })
}

// ------------------------------------------------------------------ saving

type SaveState = 'idle' | 'saving' | 'saved' | 'failed'

const saveState = ref<SaveState>('idle')
let saveTimer: ReturnType<typeof setTimeout> | null = null

async function save() {
  if (!signedIn) return

  saveState.value = 'saving'

  try {
    await api.put<void>('/api/v1/auth/me/appearance', { ...state })
    saveState.value = 'saved'
  } catch (error) {
    // The look has changed on this screen and in this browser either way. A session that has just
    // ended is the sign-in page's business, not a failure to report here.
    saveState.value = error instanceof ApiError && error.status === 401 ? 'idle' : 'failed'
  }
}

function saveSoon() {
  if (saveTimer) clearTimeout(saveTimer)
  saveTimer = setTimeout(() => {
    saveTimer = null
    void save()
  }, SAVE_DELAY_MS)
}

// ------------------------------------------------------------------ changes

let adopting = false

watch(
  state,
  () => {
    write(STORAGE_KEY, JSON.stringify(state))
    apply()
    redrawChartsWhenFontLoads()
    if (!adopting) saveSoon()
  },
  { flush: 'sync' },
)

prefersDark?.addEventListener('change', () => {
  // Only relevant while following the system; an explicit choice wins.
  if (state.theme === 'system') apply()
})

prefersLessMotion?.addEventListener('change', () => {
  if (state.motion === 'system') apply()
})

apply()
redrawChartsWhenFontLoads()

function replace(next: Appearance) {
  adopting = true
  Object.assign(state, next)
  adopting = false
}

function setOwner(userId: number | null) {
  owner = userId
  write(OWNER_KEY, userId === null ? null : String(userId))
}

/**
 * Called when the signed-in person is known.
 *
 * Their saved settings win. With none saved, what is in this browser becomes theirs if it is not
 * somebody else's: that is how a theme chosen before settings followed a person keeps working. If
 * it is somebody else's, on a shared machine, they start from the defaults.
 */
export function adoptAppearance(userId: number, saved: Record<string, unknown> | null) {
  const changedHands = owner !== null && owner !== userId

  signedIn = true
  setOwner(userId)

  if (saved) {
    replace(normalise(saved))
    return
  }

  if (changedHands) {
    replace({ ...DEFAULTS })
    return
  }

  if (KEYS.some((key) => state[key] !== DEFAULTS[key])) void save()
}

/** Signed out: this browser keeps the look, and nothing is sent. */
export function releaseAppearance() {
  signedIn = false
  if (saveTimer) clearTimeout(saveTimer)
  saveTimer = null
  saveState.value = 'idle'
}

/** The settings, and the ways to change them. */
export function useAppearance() {
  return {
    appearance: readonly(state) as Readonly<Appearance>,
    isDark: readonly(isDark),
    saveState: readonly(saveState),
    isDefault: computed(() => KEYS.every((key) => state[key] === DEFAULTS[key])),
    set: (key: AppearanceKey, value: string) => {
      if (contract[key].values.includes(value)) state[key] = value
    },
    reset: () => {
      Object.assign(state, DEFAULTS)
    },
  }
}

/** Goes up whenever a chart has to read the tokens again. */
export function useChartRevision() {
  return readonly(chartRevision)
}
