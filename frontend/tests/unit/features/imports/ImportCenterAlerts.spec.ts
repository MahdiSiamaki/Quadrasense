import { describe, expect, it, vi, beforeEach } from 'vitest'
import { flushPromises, mount, RouterLinkStub } from '@vue/test-utils'
import { ref } from 'vue'
import type { UnqueuedFile, WorkerHealth } from '@/api/imports'
import { ApiError } from '@/api/client'

/*
 * The waits the Import Center has to explain, because nothing else does. A queue held for a
 * missing disk looks like a broken worker; a stored file with no job looks like nothing at all;
 * and a file that waits for its date has not failed, and must not read as if it had.
 */
const health = ref<WorkerHealth | null>(null)
const unqueued = ref<UnqueuedFile[]>([])
const queueFile = {
  mutate: vi.fn(),
  isPending: ref(false),
  variables: ref<{ file: UnqueuedFile; date?: string } | undefined>(undefined),
}

const idle = (data: unknown) => ({
  data: ref(data),
  isPending: ref(false),
  isFetching: ref(false),
  isError: ref(false),
  error: ref(null),
  refetch: vi.fn(),
})

vi.mock('@/features/imports/useImportQueries', () => ({
  useBlockages: () => idle([]),
  useFreshness: () => idle([]),
  useImportActions: () => ({ queueFile }),
  useImportHistory: () => idle({ items: [], total: 0, page: 1, pageSize: 25 }),
  useStageDurations: () => idle([]),
  useUnqueuedFiles: () => ({ data: unqueued, refetch: vi.fn() }),
  useWorkerHealth: () => ({ data: health }),
}))

const { default: ImportCenterPage } = await import('@/features/imports/ImportCenterPage.vue')

const workers = (extra: Partial<WorkerHealth> = {}): WorkerHealth => ({
  queued: 0,
  running: 0,
  retrying: 0,
  failedLast24Hours: 0,
  oldestQueuedAt: null,
  activeWorkers: 1,
  workerHostedInApi: true,
  stalled: false,
  staleLeases: 0,
  storeReachable: true,
  ...extra,
})

/** Its upload ended before it reached the queue: its name says its day, and it only needs queueing. */
const stranded: UnqueuedFile = {
  fileId: 3980,
  sourceCode: 'SQM',
  originalFileName: 'daily_subs_device_sim_info_2026-10-26.csv',
  archiveName: null,
  sizeBytes: 366_000_000,
  uploadedBy: 'admin',
  uploadedAt: '2026-11-03T08:30:54Z',
  dateState: 'settled',
  date: '2026-10-26',
  dateCandidates: [],
  message: 'daily_subs_device_sim_info_2026-10-26.csv is dated 2026-10-26.',
}

/** 03-04-2026: 3 April or 4 March. Stored, and nothing guessed. */
const ambiguous: UnqueuedFile = {
  ...stranded,
  fileId: 4001,
  originalFileName: 'subs_03-04-2026.csv',
  dateState: 'ambiguous',
  date: null,
  dateCandidates: ['2026-03-04', '2026-04-03'],
  message: 'The date in subs_03-04-2026.csv can be read two ways: 2026-03-04 or 2026-04-03.',
}

/** The catalogue as GSMA names the file inside its archive: no date in it at all. */
const undated: UnqueuedFile = {
  ...stranded,
  fileId: 4002,
  sourceCode: 'TAC',
  originalFileName: 'DeviceDatabase.csv',
  archiveName: 'catalogue.zip',
  dateState: 'missing',
  date: null,
  dateCandidates: [],
  message: 'There is no date in the name DeviceDatabase.csv.',
}

const page = async () => {
  const wrapper = mount(ImportCenterPage, {
    global: {
      stubs: { RouterLink: RouterLinkStub, UploadDropzone: true, TacVersionPanel: true, ImportHistoryTable: true },
    },
  })
  await flushPromises()
  return wrapper
}

const alerts = (w: Awaited<ReturnType<typeof page>>) => w.findAll('[role="alert"]').map((a) => a.text())

beforeEach(() => {
  health.value = workers()
  unqueued.value = []
  queueFile.mutate.mockReset()
  queueFile.isPending.value = false
  queueFile.variables.value = undefined
})

describe('Import Center', () => {
  it('says nothing when the store is there and every file has a job', async () => {
    expect(alerts(await page())).toEqual([])
  })

  it('says a held queue is paused, not broken, and that it resumes by itself', async () => {
    health.value = workers({ storeReachable: false, queued: 7 })

    const [alert] = alerts(await page())

    expect(alert).toContain('Imports are paused')
    expect(alert).toContain('7 queued jobs will start by themselves')
  })

  it('counts a single held job in the singular', async () => {
    health.value = workers({ storeReachable: false, queued: 1 })

    expect(alerts(await page())[0]).toContain('1 queued job will start')
  })

  it('still explains a hold with nothing queued', async () => {
    health.value = workers({ storeReachable: false })

    expect(alerts(await page())[0]).toContain('imports start again by themselves')
  })

  it('lists a stored file with no job, and queues it from the stored copy', async () => {
    unqueued.value = [stranded]

    const wrapper = await page()

    expect(alerts(wrapper)[0]).toContain('1 uploaded file has no import')
    expect(alerts(wrapper)[0]).toContain('daily_subs_device_sim_info_2026-10-26.csv')
    expect(alerts(wrapper)[0]).toContain('dated Oct 26, 2026')

    await wrapper.find('[role="alert"] button').trigger('click')

    // Its name says its day: nobody is asked, and no date is sent.
    expect(queueFile.mutate.mock.calls[0]?.[0]).toEqual({ file: stranded, date: undefined })
  })

  it('shows why a file could not be queued', async () => {
    unqueued.value = [stranded]
    queueFile.mutate.mockImplementation((_, options: { onError: (error: unknown) => void }) =>
      options.onError(new ApiError(409, { title: 'No', detail: 'It is not where it was recorded.' }, null)),
    )

    const wrapper = await page()
    await wrapper.find('[role="alert"] button').trigger('click')

    expect(alerts(wrapper)[0]).toContain('It is not where it was recorded.')
  })
})

describe('a file that waits for its date', () => {
  it('is asked about apart from a file that was left behind, and is not called a failure', async () => {
    unqueued.value = [stranded, ambiguous]

    const wrapper = await page()
    const [waiting, left] = alerts(wrapper)

    expect(waiting).toContain('1 uploaded file waits for its date')
    expect(waiting).toContain('subs_03-04-2026.csv')
    expect(waiting).not.toContain('daily_subs_device_sim_info_2026-10-26.csv')
    expect(waiting).toContain('nothing is guessed')
    expect(waiting).not.toMatch(/fail|error/i)

    expect(left).toContain('1 uploaded file has no import')
    expect(left).not.toContain('subs_03-04-2026.csv')
  })

  it('offers the dates its name can mean, and one press answers', async () => {
    unqueued.value = [ambiguous]

    const wrapper = await page()
    const panel = wrapper.find('#files-waiting-for-a-date')

    expect(panel.text()).toContain('The date in its name can be read two ways. Which is the day it describes?')
    expect(panel.findAll('button').map((b) => b.text())).toEqual(['Mar 4, 2026', 'Apr 3, 2026'])
    expect(panel.find('input[type="date"]').exists()).toBe(false)

    await panel.findAll('button')[1]!.trigger('click')

    expect(queueFile.mutate.mock.calls[0]?.[0]).toEqual({ file: ambiguous, date: '2026-04-03' })
  })

  it('asks for the date outright when the name offers none, and takes no day that has not come', async () => {
    unqueued.value = [undated]

    const wrapper = await page()
    const panel = wrapper.find('#files-waiting-for-a-date')

    // A snapshot's date is the dataset's own, and the file says which archive it came out of.
    expect(panel.text()).toContain("Its name has no date. What is the dataset's own date?")
    expect(panel.text()).toContain('from catalogue.zip')

    const input = panel.find('input[type="date"]')
    const submit = panel.find('button[type="submit"]')
    const tomorrow = new Date(Date.now() + 86_400_000).toISOString().slice(0, 10)

    expect(input.attributes('max')).toBe(tomorrow)
    expect(input.attributes('aria-label')).toBe('The date of DeviceDatabase.csv')
    expect(submit.attributes('disabled')).toBeDefined()

    await input.setValue('2026-11-09')
    expect(submit.attributes('disabled')).toBeUndefined()

    await panel.find('form').trigger('submit')

    expect(queueFile.mutate.mock.calls[0]?.[0]).toEqual({ file: undated, date: '2026-11-09' })
  })

  it('says what the server said when it refuses the date', async () => {
    unqueued.value = [undated]
    queueFile.mutate.mockImplementation((_, options: { onError: (error: unknown) => void }) =>
      options.onError(
        new ApiError(400, { title: "That date cannot be this file's", detail: '2062-10-17 has not happened yet.' }, null),
      ),
    )

    const wrapper = await page()
    const panel = wrapper.find('#files-waiting-for-a-date')
    await panel.find('input[type="date"]').setValue('2062-10-17')
    await panel.find('form').trigger('submit')

    expect(panel.text()).toContain('2062-10-17 has not happened yet.')
  })

  it('counts several in the plural', async () => {
    unqueued.value = [ambiguous, undated]

    expect(alerts(await page())[0]).toContain('2 uploaded files wait for their dates')
  })
})
