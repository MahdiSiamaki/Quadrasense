<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue'
import { useQuery } from '@tanstack/vue-query'
import { api } from '@/api/client'
import AsyncBoundary from '@/design-system/AsyncBoundary.vue'
import Button from '@/design-system/Button.vue'
import SchemaFields from './SchemaFields.vue'
import {
  bodyOf,
  curlOf,
  refName,
  routesOf,
  scopesOf,
  withoutNull,
  type OpenApiDocument,
  type OpenApiResponse,
  type OpenApiSchema,
  type Route,
} from '@/lib/openapi'

/**
 * The integration API's reference, drawn from the document the running server publishes
 * (`GET /api/v1/integration/openapi`) - so it can never describe a route the server does not have,
 * or miss one it does. In PI's own look, by the owner's choice of 2026-11-12, and without a console:
 * a system's secret is never typed into a browser page. Each route gives a request to start from
 * instead, to run where the secret already is.
 */
const BASE = import.meta.env.VITE_API_BASE_URL ?? ''

const openapi = useQuery({
  queryKey: ['help', 'openapi'],
  queryFn: ({ signal }) => api.get<OpenApiDocument>('/api/v1/integration/openapi', undefined, signal),
  staleTime: 5 * 60_000,
})

const doc = computed(() => openapi.data.value ?? null)
const routes = computed(() => (doc.value ? routesOf(doc.value) : []))

/*
  An address naming a route - copied from the address bar once the index has been followed - opens
  at that route. The browser cannot do it: it looks for the anchor as the page loads, and the routes
  are drawn only when the document arrives. Measured 2026-11-26: #IntegrationHistory opened at the
  top, the route 3,198 px below. Once only: a refetch does not pull the reader back to it.
*/
watch(
  () => routes.value.length > 0,
  async (drawn) => {
    // A route's id is its operation id - the endpoint's name in the API, `IntegrationHistory` - or
    // letters, digits and dashes (`routesOf`): nothing in it is ever encoded.
    const id = window.location.hash.slice(1)
    if (!drawn || !id) return
    await nextTick()
    document.getElementById(id)?.scrollIntoView({ block: 'start' })
  },
  { immediate: true },
)

const groups = computed(() => {
  const byTag = new Map<string, Route[]>()
  for (const route of routes.value) byTag.set(route.tag, [...(byTag.get(route.tag) ?? []), route])
  return [...byTag.entries()].map(([tag, items]) => ({
    tag,
    // "Integration: inventory" is the document's grouping; on its own page "Inventory" says it.
    title: tag.replace(/^Integration:\s*/, '').replace(/^./, (first) => first.toUpperCase()),
    description: doc.value?.tags?.find((t) => t.name === tag)?.description ?? null,
    routes: items,
  }))
})

const flow = computed(() => {
  const schemes = Object.values(doc.value?.components?.securitySchemes ?? {})
  const scheme = schemes.find((s) => s.flows?.clientCredentials)
  return scheme ? { description: scheme.description ?? null, ...scheme.flows!.clientCredentials! } : null
})

const scopes = computed(() =>
  Object.entries(flow.value?.scopes ?? {}).sort(([a], [b]) => a.localeCompare(b)),
)

/** What a route asks of the caller's token, in words. */
function needs(route: Route): { text: string; scopes: string[] } {
  const required = scopesOf(route.operation)
  if (required === null) return { text: 'No token: this is where a token comes from.', scopes: [] }
  if (required.length === 0) return { text: 'Any registered system\'s token.', scopes: [] }
  return { text: required.length === 1 ? 'A token with the scope' : 'A token with the scopes', scopes: required }
}

/** A response's media type and schema; a problem document is named as one. */
function answer(response: OpenApiResponse): { schema: OpenApiSchema | undefined; name: string | null; problem: boolean } {
  const [mediaType, media] = Object.entries(response.content ?? {})[0] ?? []
  if (!mediaType) return { schema: undefined, name: null, problem: false }

  const schema = media?.schema
  const plain = schema ? withoutNull(schema).schema : undefined
  const target = plain?.type === 'array' ? plain.items : plain
  const named = target ? withoutNull(target).schema.$ref : undefined

  return {
    schema,
    name: named ? (plain?.type === 'array' ? `a list of ${refName(named)}` : refName(named)) : null,
    problem: mediaType === 'application/problem+json',
  }
}

const TONE: Record<string, string> = {
  GET: 'var(--c-success-text)',
  POST: 'var(--c-accent)',
  PUT: 'var(--c-warning-text)',
  PATCH: 'var(--c-warning-text)',
  DELETE: 'var(--c-danger-text)',
}

const copied = ref<string | null>(null)

async function copy(route: Route) {
  if (!doc.value) return
  try {
    await navigator.clipboard.writeText(curlOf(doc.value, route))
    copied.value = route.id
  } catch {
    // No clipboard here (a page over plain HTTP): the command can be selected and copied by hand.
    copied.value = null
  }
}
</script>

<template>
  <AsyncBoundary
    :is-loading="openapi.isPending.value"
    :is-error="openapi.isError.value"
    :error="openapi.error.value"
    :is-empty="routes.length === 0"
    empty-message="The server published no routes."
    min-height="20rem"
    @retry="openapi.refetch()"
  >
    <article v-if="doc" class="flex max-w-4xl flex-col gap-6 text-sm text-[var(--c-text-secondary)]">
      <header class="flex flex-wrap items-start justify-between gap-3">
        <div class="min-w-0">
          <h2 class="text-lg font-semibold tracking-tight text-[var(--c-text)]">API reference</h2>
          <p class="mt-1">
            {{ doc.info.title }}, version {{ doc.info.version }} - read from the document the server publishes, so it
            is the API as it runs.
          </p>
          <!-- The document's own words, ending as it ends them: put inside a sentence of ours, its full stop doubled. -->
          <p v-if="doc.info.description" class="mt-2">{{ doc.info.description }}</p>
        </div>
        <a
          :href="`${BASE}/api/v1/integration/openapi`"
          download="pi-integration-v1.json"
          class="shrink-0 rounded-[var(--radius-md)] border px-3 py-1.5 text-xs font-medium text-[var(--c-text)] hover:bg-[var(--c-surface-hover)]"
        >
          Download the OpenAPI document
        </a>
      </header>

      <section class="rounded-[var(--radius-md)] border p-4">
        <h3 class="text-sm font-semibold text-[var(--c-text)]">Before the first call</h3>
        <ul class="mt-2 list-disc space-y-1 pl-5">
          <li v-if="flow">
            A token comes from <code class="text-[var(--c-text)]">POST {{ flow.tokenUrl }}</code><template v-if="flow.description"> - {{ flow.description }}</template>
          </li>
          <li>
            The requests below use <code>$PI</code> for the server's address, <code>$TOKEN</code> for a token, and
            <code>$CLIENT_ID</code> and <code>$CLIENT_SECRET</code> for the system's own; anything in angle brackets is
            yours to fill in.
          </li>
        </ul>

        <h4 class="mt-4 text-xs font-semibold tracking-wide text-[var(--c-text-muted)] uppercase">Scopes</h4>
        <dl class="mt-1.5 divide-y rounded-[var(--radius-md)] border">
          <div v-for="[scope, meaning] in scopes" :key="scope" class="grid gap-0.5 px-3 py-1.5 sm:grid-cols-[11rem_minmax(0,1fr)] sm:gap-4">
            <dt class="font-mono text-xs font-medium text-[var(--c-text)]">{{ scope }}</dt>
            <dd class="text-xs">{{ meaning }}</dd>
          </div>
        </dl>
      </section>

      <nav aria-label="Routes" class="rounded-[var(--radius-md)] border p-4">
        <h3 class="text-sm font-semibold text-[var(--c-text)]">Routes</h3>
        <div class="mt-2 grid gap-4 sm:grid-cols-2">
          <div v-for="group in groups" :key="group.tag">
            <p class="text-2xs font-semibold tracking-wide text-[var(--c-text-muted)] uppercase">{{ group.title }}</p>
            <ul class="mt-1 space-y-0.5">
              <li v-for="route in group.routes" :key="route.id" class="flex items-baseline gap-2">
                <span class="w-12 shrink-0 font-mono text-2xs font-semibold" :style="{ color: TONE[route.method] }">{{ route.method }}</span>
                <a :href="`#${route.id}`" class="min-w-0 font-mono text-xs wrap-anywhere text-[var(--c-text)] hover:underline">
                  {{ route.path.replace('/api/integration/v1', '') }}
                </a>
              </li>
            </ul>
          </div>
        </div>
      </nav>

      <section v-for="group in groups" :key="group.tag" class="flex flex-col gap-4">
        <h3 class="border-b pb-1.5 text-base font-semibold text-[var(--c-text)]">{{ group.title }}</h3>
        <p v-if="group.description">{{ group.description }}</p>

        <section
          v-for="route in group.routes"
          :id="route.id"
          :key="route.id"
          class="flex scroll-mt-20 flex-col gap-3 rounded-[var(--radius-md)] border p-4"
        >
          <div class="flex flex-wrap items-baseline gap-x-2 gap-y-1">
            <span class="font-mono text-xs font-semibold" :style="{ color: TONE[route.method] }">{{ route.method }}</span>
            <code class="font-mono text-sm font-medium text-[var(--c-text)] wrap-anywhere">{{ route.path }}</code>
          </div>
          <p v-if="route.operation.summary" class="text-[var(--c-text)]">{{ route.operation.summary }}</p>
          <p v-if="route.operation.description">{{ route.operation.description }}</p>

          <p class="text-xs">
            <span class="text-[var(--c-text-muted)]">Needs:</span> {{ needs(route).text }}
            <code
              v-for="scope in needs(route).scopes"
              :key="scope"
              class="ml-1 rounded-full bg-[var(--c-surface-sunken)] px-1.5 py-0.5 font-mono text-2xs text-[var(--c-text)]"
            >{{ scope }}</code>
          </p>

          <div v-if="route.operation.parameters?.length">
            <h4 class="text-xs font-semibold tracking-wide text-[var(--c-text-muted)] uppercase">Parameters</h4>
            <ul class="mt-1.5 divide-y rounded-[var(--radius-md)] border">
              <li v-for="parameter in route.operation.parameters" :key="parameter.name" class="px-3 py-2">
                <div class="flex flex-wrap items-baseline gap-x-2">
                  <code class="font-mono text-xs font-semibold text-[var(--c-text)]">{{ parameter.name }}</code>
                  <span class="text-2xs text-[var(--c-text-muted)]">in the {{ parameter.in }}</span>
                  <span v-if="parameter.required" class="text-2xs font-medium text-[var(--c-warning-text)]">required</span>
                </div>
                <p v-if="parameter.description" class="mt-0.5 text-xs">{{ parameter.description }}</p>
              </li>
            </ul>
          </div>

          <div v-if="bodyOf(route.operation)">
            <h4 class="text-xs font-semibold tracking-wide text-[var(--c-text-muted)] uppercase">
              Request body <span class="font-mono font-normal normal-case">{{ bodyOf(route.operation)!.mediaType }}</span>
            </h4>
            <div class="mt-1.5">
              <SchemaFields :doc="doc" :schema="bodyOf(route.operation)!.schema" />
            </div>
          </div>

          <div>
            <h4 class="text-xs font-semibold tracking-wide text-[var(--c-text-muted)] uppercase">Answers</h4>
            <ul class="mt-1.5 flex flex-col gap-2">
              <li v-for="(response, status) in route.operation.responses" :key="status">
                <p class="text-xs">
                  <span class="font-mono font-semibold text-[var(--c-text)]">{{ status }}</span>
                  {{ response.description }}<template v-if="answer(response).problem"> - a problem document, with the reason</template><template v-else-if="answer(response).name"> - {{ answer(response).name }}</template>
                </p>
                <details v-if="!answer(response).problem && answer(response).schema" class="mt-1">
                  <summary class="cursor-pointer text-2xs font-medium text-[var(--c-accent)] select-none hover:underline">
                    Fields
                  </summary>
                  <div class="mt-1.5">
                    <SchemaFields :doc="doc" :schema="answer(response).schema" />
                  </div>
                </details>
              </li>
            </ul>
          </div>

          <div>
            <div class="flex items-center justify-between gap-2">
              <h4 class="text-xs font-semibold tracking-wide text-[var(--c-text-muted)] uppercase">A request to start from</h4>
              <Button size="sm" :aria-label="`Copy the request for ${route.method} ${route.path}`" @click="copy(route)">
                {{ copied === route.id ? 'Copied' : 'Copy' }}
              </Button>
            </div>
            <pre class="mt-1.5 overflow-x-auto rounded-[var(--radius-md)] border bg-[var(--c-surface-sunken)] px-3 py-2 font-mono text-2xs leading-relaxed text-[var(--c-text)]"><code>{{ curlOf(doc, route) }}</code></pre>
          </div>
        </section>
      </section>
    </article>
  </AsyncBoundary>
</template>
