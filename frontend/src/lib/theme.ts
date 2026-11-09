import { computed } from 'vue'
import { useAppearance } from './appearance'

export type ThemeMode = 'light' | 'dark' | 'system'

/**
 * The theme, for what only cares about the theme: the switch in the top bar.
 *
 * Three states, not two: "system" is the default and must stay distinct from an explicit light
 * choice, otherwise a user who has never touched the control gets frozen into whatever their OS
 * happened to be on first load. It is one of the settings in `appearance.ts`, which keeps it.
 */
export function useTheme() {
  const { appearance, isDark, set } = useAppearance()

  return {
    mode: computed(() => appearance.theme as ThemeMode),
    isDark,
    setMode: (next: ThemeMode) => set('theme', next),
    toggle: () => set('theme', isDark.value ? 'light' : 'dark'),
  }
}
