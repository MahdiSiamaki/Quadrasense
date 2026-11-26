/**
 * What Help holds: guides to working with PI, and the integration API's reference.
 *
 * Open to everyone signed in (the owner's choice, 2026-11-10 and 2026-11-12): how to read a figure
 * or bring a file in is everyone's business, and reading how the API works is not a privilege -
 * calling it is, and that is decided per system in Settings > API clients.
 */
export interface HelpSection {
  to: string
  label: string
  description: string
  group: 'Guides' | 'Integration API'
}

export const HELP_SECTIONS: readonly HelpSection[] = [
  {
    to: '/help/importing-files',
    label: 'Importing files',
    description: 'CSV, ZIP, RAR or GZ - and how a file gets its date.',
    group: 'Guides',
  },
  {
    to: '/help/reading-the-numbers',
    label: 'Reading the numbers',
    description: 'Bindings, subscribers and handsets; estimates; what no count includes.',
    group: 'Guides',
  },
  {
    to: '/help/connecting-a-system',
    label: 'Connecting a system',
    description: 'How a system is given access, gets a token and calls PI.',
    group: 'Integration API',
  },
  {
    to: '/help/api-reference',
    label: 'API reference',
    description: 'Every route: what it needs, what it takes, what it answers.',
    group: 'Integration API',
  },
]
