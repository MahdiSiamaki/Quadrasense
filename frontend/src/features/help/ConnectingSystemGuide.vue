<script setup lang="ts">
import { RouterLink } from 'vue-router'

/** Help: how another system reaches PI (ADR-024; docs/integration/PI-API-CATALOG.md, Conventions). */
const STEPS = [
  {
    title: 'An administrator registers the system',
    text: 'In Settings > API clients, someone with the integration permission gives the system a client id and the scopes it may use. PI issues its secret and shows it once; it goes straight into the system\'s own secret store.',
  },
  {
    title: 'The system asks for a token',
    text: 'With its client id and secret it asks the token route for a bearer token (OAuth 2.0 client credentials). A token lasts fifteen minutes; the system asks again when it runs out.',
  },
  {
    title: 'It calls PI with the token',
    text: 'Every request carries Authorization: Bearer and the token. What it may read is what its scopes allow; anything else is refused, and the refusal is recorded.',
  },
]

const RULES = [
  {
    title: 'Base path',
    text: '/api/integration/v1. A change that would break a caller becomes v2 beside it; v1 does not change under anyone.',
  },
  {
    title: 'Identifiers in the body',
    text: 'Numbers, SIMs and handsets travel in request bodies, never in URLs, so they never land in an address bar or a log line. Every read that names one is a POST.',
  },
  {
    title: 'Masked unless revealed',
    text: 'Without identifiers.reveal, every IMSI, ICCID, IMEI and number in an answer is masked - all but the identifier the system sent. Each answer says which it is.',
  },
  {
    title: 'Errors',
    text: 'Problem documents (RFC 7807), with each refused field named under errors. Every answer carries X-Correlation-Id: quote it when something needs looking into.',
  },
  {
    title: 'Limits',
    text: '600 requests a minute for each system, then 429 with Retry-After; 30 token requests a minute from one address; up to 50 identifiers in one batch.',
  },
  {
    title: 'Repeating a request',
    text: 'Reads are safe to repeat. A declared event is taken once for its event id. The event feed may deliver an event twice: tell them apart by id.',
  },
]
</script>

<template>
  <article class="flex max-w-3xl flex-col gap-6 text-sm text-[var(--c-text-secondary)]">
    <header>
      <h2 class="text-lg font-semibold tracking-tight text-[var(--c-text)]">Connecting a system</h2>
      <p class="mt-1">
        Other systems of the SIM and eSIM estate - CRM, BI, network-quality tools - read from PI through its integration
        API: who holds a number, which SIM and handset it is on, what changed, and how sound the data is. People use
        these pages; systems use the API. A person's sign-in opens nothing in the API, and a system's token opens no
        page.
      </p>
    </header>

    <section>
      <h3 class="text-sm font-semibold text-[var(--c-text)]">Three steps</h3>
      <ol class="mt-2 flex flex-col gap-3">
        <li v-for="(step, index) in STEPS" :key="step.title" class="flex gap-3">
          <span
            class="grid size-6 shrink-0 place-items-center rounded-full bg-[var(--c-surface-sunken)] text-xs font-semibold text-[var(--c-text)]"
            aria-hidden="true"
          >
            {{ index + 1 }}
          </span>
          <div>
            <p class="font-medium text-[var(--c-text)]">{{ step.title }}</p>
            <p class="mt-0.5">{{ step.text }}</p>
          </div>
        </li>
      </ol>
    </section>

    <section>
      <h3 class="text-sm font-semibold text-[var(--c-text)]">What every caller can rely on</h3>
      <dl class="mt-2 divide-y rounded-[var(--radius-md)] border">
        <div v-for="rule in RULES" :key="rule.title" class="grid gap-1 px-3 py-2 sm:grid-cols-[10rem_minmax(0,1fr)] sm:gap-4">
          <dt class="font-medium text-[var(--c-text)]">{{ rule.title }}</dt>
          <dd>{{ rule.text }}</dd>
        </div>
      </dl>
    </section>

    <section>
      <h3 class="text-sm font-semibold text-[var(--c-text)]">Where to go next</h3>
      <ul class="mt-2 list-disc space-y-1 pl-5">
        <li>
          <RouterLink to="/help/api-reference" class="font-medium text-[var(--c-accent)] hover:underline">The API reference</RouterLink>
          lists every route: the scope it needs, what it takes, what it answers, and a request to start from.
        </li>
        <li>
          <RouterLink to="/settings/api-clients" class="font-medium text-[var(--c-accent)] hover:underline">Settings &gt; API clients</RouterLink>
          is where a system is registered, if you hold the integration permission.
        </li>
      </ul>
    </section>
  </article>
</template>
