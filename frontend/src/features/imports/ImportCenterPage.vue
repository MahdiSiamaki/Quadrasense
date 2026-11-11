<script setup lang="ts">
import { computed, reactive, ref } from 'vue'
import Button from '@/design-system/Button.vue'
import Card from '@/design-system/Card.vue'
import AsyncBoundary from '@/design-system/AsyncBoundary.vue'
import Pagination from '@/design-system/Pagination.vue'
import UploadDropzone from './UploadDropzone.vue'
import ImportHistoryTable from './ImportHistoryTable.vue'
import TacVersionPanel from './TacVersionPanel.vue'
import { RouterLink } from 'vue-router'
import {
  useBlockages,
  useImportActions,
  useImportHistory,
  useUnqueuedFiles,
  useWorkerHealth,
  useFreshness,
  useStageDurations,
} from './useImportQueries'
import { formatBytes, formatDate, formatRelative, formatDateTime } from '@/lib/format'
import { ApiError } from '@/api/client'
import type { ImportHistoryQuery, UnqueuedFile } from '@/api/imports'

/**
 * Import Center: bring files in, and see what happened to the ones already brought in.
 *
 * The layout puts state before action, which is the reverse of the obvious arrangement. An
 * operator arriving here usually wants to know whether yesterday landed, not to upload
 * something — and if a day is missing, that changes which file they reach for. Upload sits in
 * the right-hand column, always visible, never the first thing read.
 */
const source = ref<'' | 'SQM' | 'TAC'>('')
const statusFilter = ref('')
const fileName = ref('')
const page = ref(1)

const query = computed<ImportHistoryQuery>(() => ({
  source: source.value || undefined,
  status: statusFilter.value || undefined,
  fileName: fileName.value || undefined,
  page: page.value,
  pageSize: 25,
}))

const history = useImportHistory(query)
const sqmTypicals = useStageDurations('SQM')
const tacTypicals = useStageDurations('TAC')
const health = useWorkerHealth()
const blockages = useBlockages()
const freshness = useFreshness()
const unqueued = useUnqueuedFiles()
const { queueFile } = useImportActions()

/**
 * A stored file with no import is one of two things, and they are told apart by its date.
 *
 * With a date, its upload ended before it reached the queue: it only needs queueing. Without one,
 * nothing went wrong - its name does not say which day it is, or says two, and a daily file filed
 * under the wrong day is believed by everything after it. So it waits, and is asked about here.
 */
const waitingForDate = computed(() => (unqueued.data.value ?? []).filter((f) => f.dateState !== 'settled'))
const leftBehind = computed(() => (unqueued.data.value ?? []).filter((f) => f.dateState === 'settled'))

/** The date typed for a file whose name offers nothing to choose from, by file. */
const typedDate = reactive<Record<number, string>>({})

/**
 * The last day a file may be given: tomorrow, counted in UTC as the server counts it - the files
 * are named in Tehran, where the day turns three and a half hours earlier.
 */
const latestDate = computed(() => new Date(Date.now() + 86_400_000).toISOString().slice(0, 10))

/** What is being asked, in the terms of the file's source. */
function question(file: UnqueuedFile): string {
  const what = file.sourceCode === 'TAC' ? "the dataset's own date" : 'the day it describes'
  switch (file.dateState) {
    case 'ambiguous':
      return `The date in its name can be read two ways. Which is ${what}?`
    case 'several':
      return `Its name holds more than one date. Which is ${what}?`
    case 'future':
      return `The date in its name has not happened yet, so it was not taken. What is ${what}?`
    default:
      return `Its name has no date. What is ${what}?`
  }
}

/** Why the last file could not be queued: refused, or no longer readable. */
const queueProblem = ref<string | null>(null)

function queueStored(file: UnqueuedFile, date?: string) {
  queueProblem.value = null
  queueFile.mutate(
    { file, date },
    {
      onError: (error) => {
        queueProblem.value =
          error instanceof ApiError && error.problem?.detail
            ? error.problem.detail
            : `${file.originalFileName} could not be queued.`
      },
    },
  )
}

const queueing = (file: UnqueuedFile) =>
  queueFile.isPending.value && queueFile.variables.value?.file.fileId === file.fileId

/** Status groupings an operator actually filters by, not the seventeen raw statuses. */
const STATUS_PRESETS = [
  { value: '', label: 'All' },
  { value: 'Completed,PartiallyCompleted', label: 'Succeeded' },
  { value: 'Failed,Quarantined', label: 'Failed' },
  // Every non-terminal status. Deduplicating (a daily file) and Enriching (a TAC file) were
  // missing, so a job in either stage disappeared from "In flight" while it was running.
  {
    value:
      'Uploaded,Queued,Retrying,Validating,Parsing,Normalizing,Deduplicating,Enriching,Importing,Aggregating,Finalizing',
    label: 'In flight',
  },
  { value: 'Duplicate', label: 'Duplicates' },
]

function resetPaging() {
  page.value = 1
}

function refreshAfterUpload() {
  void history.refetch()
  void unqueued.refetch()
}
</script>

<template>
  <div class="space-y-5">
    <header class="flex flex-wrap items-end justify-between gap-4">
      <div>
        <h1 class="text-xl font-semibold tracking-tight">Import Center</h1>
        <p class="mt-0.5 text-sm text-[var(--c-text-secondary)]">
          Daily subscriber changes and GSMA TAC snapshots. Every file is kept, every import is
          repeatable, and re-importing a day replaces it rather than adding to it.
        </p>
      </div>

      <!-- Worker state, stated plainly. A queue that is not draining is the single most
           useful thing to notice on this page, and it should not need a click. -->
      <div v-if="health.data.value" class="flex items-center gap-4 text-xs">
        <span class="flex items-center gap-1.5">
          <span
            class="size-1.5 rounded-full"
            :class="health.data.value.activeWorkers > 0 ? 'animate-pulse' : ''"
            :style="{
              backgroundColor:
                health.data.value.activeWorkers > 0 ? 'var(--c-success)' : 'var(--c-text-muted)',
            }"
            aria-hidden="true"
          />
          <span class="text-[var(--c-text-secondary)]">
            {{ health.data.value.activeWorkers }} worker{{ health.data.value.activeWorkers === 1 ? '' : 's' }}
            <template v-if="health.data.value.workerHostedInApi">(in the API)</template>
          </span>
        </span>
        <span class="text-[var(--c-text-secondary)]">
          {{ health.data.value.queued }} queued · {{ health.data.value.running }} running
        </span>
        <span v-if="health.data.value.staleLeases > 0" class="font-medium text-[var(--c-danger-text)]">
          {{ health.data.value.staleLeases }} stale lease(s)
        </span>
      </div>
    </header>

    <!--
      The state that used to be silent.

      A 319.6 MB file was uploaded successfully and sat at "Queued" forever, because no worker
      process was running. Every component reported success; nothing said why nothing happened.
      This is the one combination worth interrupting the page for: work waiting, and nothing
      alive to take it.
    -->
    <div
      v-if="health.data.value?.stalled"
      class="rounded-[var(--radius-md)] border border-[var(--c-warning)] p-4"
      role="alert"
    >
      <p class="text-sm font-semibold">
        {{ health.data.value.queued }} job{{ health.data.value.queued === 1 ? '' : 's' }} queued,
        and no import worker is running.
      </p>
      <p class="mt-1 text-sm text-[var(--c-text-secondary)]">
        Nothing will be imported until one starts. In development the API hosts the worker itself,
        so this usually means the API was started with
        <code class="text-xs">Import:RunWorkerInProcess=false</code>, or the worker
        process has stopped. Start one with:
      </p>
      <pre
        class="mt-2 overflow-x-auto rounded-[var(--radius-md)] bg-[var(--c-surface-sunken)] px-3 py-2 text-xs"
      ><code>dotnet run --project backend/src/Sqm.Ingestion</code></pre>
      <p
        v-if="health.data.value.oldestQueuedAt"
        class="mt-2 text-2xs text-[var(--c-text-muted)]"
      >
        The oldest has been waiting since {{ formatDateTime(health.data.value.oldestQueuedAt) }}.
      </p>
    </div>

    <!--
      The same silence with a worker running. On the laptop the file store is a USB disk: when it
      leaves, the worker holds rather than failing a day, and from here a hold looks like a broken
      worker unless the page says otherwise.
    -->
    <div
      v-if="health.data.value?.storeReachable === false"
      class="rounded-[var(--radius-md)] border border-[var(--c-warning)] p-4"
      role="alert"
    >
      <p class="text-sm font-semibold">Imports are paused: the file store cannot be reached.</p>
      <p class="mt-1 text-sm text-[var(--c-text-secondary)]">
        Check that the disk holding the uploaded files is connected. Nothing has failed:
        <template v-if="health.data.value.queued > 0">
          {{ health.data.value.queued }} queued job{{ health.data.value.queued === 1 ? '' : 's' }} will
          start by themselves when it returns.
        </template>
        <template v-else>imports start again by themselves when it returns.</template>
      </p>
    </div>

    <!--
      The other silent state. Five days were uploaded and sat at "Queued" with a worker running,
      because an earlier day had failed: days of a source import in order, so that none is folded
      over a gap. The rule is right; a wait nobody can see the reason for looks like a broken worker.
    -->
    <div
      v-for="b in blockages.data.value ?? []"
      :key="`${b.sourceCode}:${b.businessDate}`"
      class="rounded-[var(--radius-md)] border border-[var(--c-warning)] p-4"
      role="alert"
    >
      <p class="text-sm font-semibold">
        {{ b.waiting }} {{ b.sourceCode }} file{{ b.waiting === 1 ? ' is' : 's are' }} waiting behind
        {{ formatDate(b.businessDate) }}, whose import {{ b.status === 'CANCELLED' ? 'was cancelled' : 'failed' }}.
      </p>
      <p class="mt-1 text-sm text-[var(--c-text-secondary)]">
        Days of a source are imported in order, so that no day is applied over a gap. Open that day and
        choose <span class="font-medium">Import again</span>; the waiting files follow on their own.
      </p>
      <RouterLink :to="`/imports/${b.jobId}`" class="mt-2 inline-block text-sm font-medium text-[var(--c-accent)] hover:underline">
        Open {{ formatDate(b.businessDate) }}
      </RouterLink>
    </div>

    <!--
      A file whose name does not say its date (ADR-025). Nothing failed: it is stored, and it waits
      because a date is never guessed. Where the name can mean two dates they are the two buttons;
      where it offers none, the date is typed. The server holds the answer to the same rule, so a
      date the name cannot mean is refused there and said here.
    -->
    <div
      v-if="waitingForDate.length"
      id="files-waiting-for-a-date"
      class="scroll-mt-20 rounded-[var(--radius-md)] border border-[var(--c-warning)] p-4"
      role="alert"
    >
      <p class="text-sm font-semibold">
        {{ waitingForDate.length }} uploaded file{{ waitingForDate.length === 1 ? ' waits' : 's wait' }} for
        {{ waitingForDate.length === 1 ? 'its date' : 'their dates' }}.
      </p>
      <p class="mt-1 text-sm text-[var(--c-text-secondary)]">
        A file's date is read from its name. Where the name gives none, or can be read two ways, the file
        is stored and nothing is guessed. Say which date is its own, and it is imported from the stored
        copy: nothing has to be uploaded again.
      </p>
      <ul class="mt-2 divide-y">
        <li
          v-for="file in waitingForDate"
          :key="file.fileId"
          class="flex flex-wrap items-center justify-between gap-x-4 gap-y-2 py-2"
        >
          <div class="min-w-0">
            <p class="text-sm wrap-anywhere">
              {{ file.originalFileName }}
              <span v-if="file.archiveName" class="text-[var(--c-text-muted)]">· from {{ file.archiveName }}</span>
            </p>
            <p class="text-xs text-[var(--c-text-muted)]">
              {{ file.sourceCode }} · {{ formatBytes(file.sizeBytes) }} · uploaded by {{ file.uploadedBy }},
              <span :title="formatDateTime(file.uploadedAt)">{{ formatRelative(file.uploadedAt) }}</span>
            </p>
            <p class="mt-0.5 text-xs text-[var(--c-text-secondary)]">{{ question(file) }}</p>
          </div>

          <!-- What the name can mean: one press answers. -->
          <div v-if="file.dateCandidates.length" class="flex flex-wrap items-center gap-2">
            <Button
              v-for="candidate in file.dateCandidates"
              :key="candidate"
              size="sm"
              :pending="queueing(file) && queueFile.variables.value?.date === candidate"
              :disabled="queueFile.isPending.value"
              @click="queueStored(file, candidate)"
            >
              {{ formatDate(candidate) }}
            </Button>
          </div>

          <!-- The name offers nothing: any day that has happened. -->
          <form
            v-else
            class="flex items-center gap-2"
            @submit.prevent="typedDate[file.fileId] && queueStored(file, typedDate[file.fileId])"
          >
            <input
              v-model="typedDate[file.fileId]"
              type="date"
              required
              :max="latestDate"
              :aria-label="`The date of ${file.originalFileName}`"
              class="rounded-[var(--radius-md)] border bg-[var(--c-surface)] px-2 py-1 text-xs"
            />
            <Button
              size="sm"
              type="submit"
              :pending="queueing(file)"
              :disabled="queueFile.isPending.value || !typedDate[file.fileId]"
            >
              Queue import
            </Button>
          </form>
        </li>
      </ul>
      <p v-if="queueProblem" class="mt-2 text-sm text-[var(--c-danger-text)]" role="status">{{ queueProblem }}</p>
    </div>

    <!--
      The quietest of them. A file stored with no job blocks nothing and fails nothing, so its day
      simply never arrives: the file for 2026-10-26 sat like that for two days and was found by
      counting. Queued from the copy already stored, so nothing has to be sent again.
    -->
    <div
      v-if="leftBehind.length"
      class="rounded-[var(--radius-md)] border border-[var(--c-warning)] p-4"
      role="alert"
    >
      <p class="text-sm font-semibold">
        {{ leftBehind.length }} uploaded file{{ leftBehind.length === 1 ? ' has' : 's have' }}
        no import.
      </p>
      <p class="mt-1 text-sm text-[var(--c-text-secondary)]">
        The upload ended before the file reached the queue, so its day will not arrive by itself.
        The file is stored: queue it here, and nothing has to be uploaded again.
      </p>
      <ul class="mt-2 divide-y">
        <li
          v-for="file in leftBehind"
          :key="file.fileId"
          class="flex flex-wrap items-center justify-between gap-x-4 gap-y-1 py-1.5"
        >
          <div class="min-w-0">
            <p class="text-sm wrap-anywhere">{{ file.originalFileName }}</p>
            <p class="text-xs text-[var(--c-text-muted)]">
              {{ file.sourceCode }} · {{ formatBytes(file.sizeBytes) }} · uploaded by {{ file.uploadedBy }},
              <span :title="formatDateTime(file.uploadedAt)">{{ formatRelative(file.uploadedAt) }}</span>
              <template v-if="file.date"> · dated {{ formatDate(file.date) }}</template>
            </p>
          </div>
          <Button size="sm" :pending="queueing(file)" :disabled="queueFile.isPending.value" @click="queueStored(file)">
            Queue import
          </Button>
        </li>
      </ul>
      <p
        v-if="queueProblem && !waitingForDate.length"
        class="mt-2 text-sm text-[var(--c-danger-text)]"
        role="status"
      >
        {{ queueProblem }}
      </p>
    </div>

    <!-- Freshness, per source. Placed above the fold because "is the data current?" is the
         question this whole page exists to answer. -->
    <div v-if="freshness.data.value?.length" class="grid gap-3 sm:grid-cols-2">
      <Card v-for="row in freshness.data.value" :key="row.sourceCode">
        <div class="flex items-start justify-between gap-4">
          <div class="min-w-0">
            <p class="text-xs font-medium text-[var(--c-text-muted)]">
              {{ row.sourceCode }}
            </p>
            <p class="mt-0.5 text-lg font-semibold tracking-tight">
              {{ formatDate(row.latestBusinessDate) }}
            </p>
            <!-- A snapshot's date is its dataset's, from the file name: not a day of data. -->
            <p class="text-xs text-[var(--c-text-secondary)]">
              {{ row.isSnapshot ? 'dataset date of the newest snapshot' : 'latest day imported' }}<template v-if="row.latestImportedAt">
                · loaded
                <span :title="formatDateTime(row.latestImportedAt)">
                  {{ formatRelative(row.latestImportedAt) }}
                </span>
              </template>
            </p>
          </div>

          <div class="shrink-0 space-y-1 text-right">
            <p
              v-if="row.daysBehind !== null"
              class="text-xs font-medium"
              :style="{
                color:
                  row.daysBehind > 45
                    ? 'var(--c-danger-text)'
                    : row.daysBehind > 35
                      ? 'var(--c-warning-text)'
                      : 'var(--c-text-secondary)',
              }"
            >
              {{ row.daysBehind }} {{ row.isSnapshot ? 'days old' : 'days behind' }}
            </p>
            <p
              v-if="row.missingBusinessDates.length"
              class="text-xs text-[var(--c-warning-text)]"
              :title="row.missingBusinessDates.slice(0, 20).join(', ')"
            >
              {{ row.missingBusinessDates.length }} day(s) missing
            </p>
            <p v-if="row.failedLast7Days" class="text-xs text-[var(--c-danger-text)]">
              {{ row.failedLast7Days }} failed this week
            </p>
          </div>
        </div>
      </Card>
    </div>

    <!-- minmax(0, 1fr): the history table scrolls inside its card instead of widening the column. -->
    <div class="grid gap-5 lg:grid-cols-[minmax(0,1fr)_20rem]">
      <!-- History -->
      <Card title="Import history" flush>
        <template #actions>
          <div class="flex flex-wrap items-center gap-2">
            <input
              v-model="fileName"
              type="search"
              placeholder="Filter by file name"
              class="w-44 rounded-[var(--radius-md)] border bg-[var(--c-surface)] px-2 py-1 text-xs"
              @input="resetPaging"
            />
            <select
              v-model="source"
              class="rounded-[var(--radius-md)] border bg-[var(--c-surface)] px-2 py-1 text-xs"
              aria-label="Data source"
              @change="resetPaging"
            >
              <option value="">All sources</option>
              <option value="SQM">SQM</option>
              <option value="TAC">TAC</option>
            </select>
            <select
              v-model="statusFilter"
              class="rounded-[var(--radius-md)] border bg-[var(--c-surface)] px-2 py-1 text-xs"
              aria-label="Status"
              @change="resetPaging"
            >
              <option v-for="preset in STATUS_PRESETS" :key="preset.label" :value="preset.value">
                {{ preset.label }}
              </option>
            </select>
          </div>
        </template>

        <AsyncBoundary
          gap="none"
          :is-loading="history.isPending.value"
          :is-error="history.isError.value"
          :error="history.error.value"
          :is-empty="history.data.value?.items.length === 0"
          empty-message="Nothing has been imported yet."
          min-height="20rem"
          @retry="history.refetch()"
        >
          <ImportHistoryTable
            :rows="history.data.value?.items ?? []"
            :typicals="{ SQM: sqmTypicals.data.value, TAC: tacTypicals.data.value }"
          />
          <!-- The shared pager: the same range, keys and press as every other list. -->
          <Pagination
            v-if="history.data.value"
            :page="page"
            :page-size="history.data.value.pageSize"
            :total="history.data.value.total"
            :loading="history.isFetching.value"
            @update:page="page = $event"
          />
        </AsyncBoundary>

      </Card>

      <!-- Upload -->
      <div class="space-y-5">
        <!-- A file that arrives may wait for its date: the list of those is asked again with the history. -->
        <Card title="Daily subscriber changes" subtitle="One file per day, named with its date">
          <UploadDropzone
            source-code="SQM"
            hint="daily_subs_device_sim_info_YYYY-MM-DD.csv - the day is read from the name, however it writes the date."
            @uploaded="refreshAfterUpload"
          />
        </Card>

        <Card title="GSMA TAC database" subtitle="A full snapshot; activated separately">
          <UploadDropzone
            source-code="TAC"
            hint="As GSMA delivers it, .rar and all. Uploading does not change the dashboard: an administrator activates the version."
            @uploaded="refreshAfterUpload"
          />
        </Card>

        <TacVersionPanel />
      </div>
    </div>
  </div>
</template>
