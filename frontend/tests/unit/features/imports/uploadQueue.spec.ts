import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises } from '@vue/test-utils'

/*
 * The upload queue, against a stand-in for the upload itself: each call is held open until the
 * test settles it, the way a real upload is open for minutes, so cancelling can happen mid-flight.
 */
interface Pending {
  sourceCode: string
  file: File
  onProgress: (fraction: number) => void
  signal: AbortSignal
  /** What the name says of the date, as the preflight answers it. */
  preflight: (answer: { dateState: string | null }) => void
  resolve: (jobId: number) => void
  /** Finishes the upload with any outcome the server can report. */
  settle: (result: unknown) => void
}

const pending: Pending[] = []

vi.mock('@/api/imports', () => ({
  uploadImportFile: (
    sourceCode: string,
    file: File,
    onProgress: (fraction: number) => void,
    signal: AbortSignal,
    _onRetry: unknown,
    onPreflight: (answer: unknown) => void,
  ) =>
    new Promise((resolve, reject) => {
      signal.addEventListener('abort', () =>
        reject(new DOMException('Upload cancelled', 'AbortError')),
      )
      pending.push({
        sourceCode,
        file,
        onProgress,
        signal,
        preflight: (answer) => onPreflight({ date: null, dateCandidates: [], ...answer }),
        resolve: (jobId) =>
          resolve({ kind: 'queued', jobId, fileName: file.name, sizeBytes: file.size, sha256: '' }),
        settle: resolve,
      })
    }),
}))

const { enqueue, cancel, cancelAll, useUploadQueue, resetUploadQueue } = await import(
  '@/features/imports/uploadQueue'
)

const file = (name: string, bytes = 1_000) => new File([new Uint8Array(bytes)], name)

beforeEach(() => {
  pending.length = 0
  resetUploadQueue()
})
afterEach(() => resetUploadQueue())

describe('the upload queue', () => {
  it('sends one file at a time, in order', async () => {
    const { items } = useUploadQueue('SQM')
    enqueue('SQM', [file('a.csv'), file('b.csv')])
    await flushPromises()

    expect(pending.map((p) => p.file.name)).toEqual(['a.csv'])
    expect(items.value.map((i) => i.status)).toEqual(['uploading', 'waiting'])

    pending[0]!.resolve(11)
    await flushPromises()
    expect(pending.map((p) => p.file.name)).toEqual(['a.csv', 'b.csv'])
    expect(items.value.map((i) => i.status)).toEqual(['queued', 'uploading'])
  })

  it('makes a file dropped during an upload wait its turn', async () => {
    const { items } = useUploadQueue('SQM')
    enqueue('SQM', [file('a.csv')])
    await flushPromises()
    enqueue('SQM', [file('b.csv')])
    await flushPromises()

    expect(pending.map((p) => p.file.name)).toEqual(['a.csv'])
    expect(items.value.map((i) => i.status)).toEqual(['uploading', 'waiting'])
  })

  it('stops a file mid-upload, says nothing was kept, and goes on with the next', async () => {
    const { items } = useUploadQueue('SQM')
    enqueue('SQM', [file('a.csv'), file('b.csv')])
    await flushPromises()

    pending[0]!.onProgress(0.4)
    cancel(items.value[0]!.id)
    await flushPromises()

    expect(pending[0]!.signal.aborted).toBe(true)
    expect(items.value[0]!.status).toBe('cancelled')
    expect(items.value[0]!.message).toBe('Cancelled - nothing was kept')
    expect(items.value[1]!.status).toBe('uploading')
  })

  it('takes a waiting file out of the line without sending any of it', async () => {
    const { items } = useUploadQueue('SQM')
    enqueue('SQM', [file('a.csv'), file('b.csv')])
    await flushPromises()

    cancel(items.value[1]!.id)
    pending[0]!.resolve(11)
    await flushPromises()

    expect(pending.map((p) => p.file.name)).toEqual(['a.csv'])
    expect(items.value[1]!.status).toBe('cancelled')
  })

  it('cancels everything of one source and nothing of the other', async () => {
    const sqm = useUploadQueue('SQM')
    const tac = useUploadQueue('TAC')
    enqueue('SQM', [file('a.csv'), file('b.csv'), file('c.csv')])
    enqueue('TAC', [file('tac.csv')])
    await flushPromises()

    cancelAll('SQM')
    await flushPromises()

    expect(sqm.items.value.map((i) => i.status)).toEqual(['cancelled', 'cancelled', 'cancelled'])
    expect(pending.filter((p) => p.sourceCode === 'SQM')).toHaveLength(1)
    expect(tac.items.value[0]!.status).toBe('uploading')
  })

  it('totals what is left of every unfinished file', async () => {
    const { totals } = useUploadQueue('SQM')
    enqueue('SQM', [file('a.csv', 1_000), file('b.csv', 3_000)])
    await flushPromises()

    pending[0]!.onProgress(0.5)
    expect(totals.value).toMatchObject({ files: 2, bytesLeft: 3_500, percent: 12.5 })

    cancelAll('SQM')
    await flushPromises()
    expect(totals.value).toBeNull()
  })

  it('measures a resumed upload from where it resumed, not from nothing', async () => {
    vi.useFakeTimers({ toFake: ['Date'] })
    try {
      vi.setSystemTime(0)
      const { items } = useUploadQueue('SQM')
      enqueue('SQM', [file('a.csv', 1_000_000)])
      await flushPromises()

      // Half of it was already on the server: the first report says 50%, then 1% more in 2 s.
      vi.setSystemTime(2_000)
      pending[0]!.onProgress(0.5)
      vi.setSystemTime(4_000)
      pending[0]!.onProgress(0.51)

      // 10,000 bytes in 2 s - not the 510,000 since the start, which read 2.6 MB/s on the VM.
      expect(items.value[0]!.rate).toBe(5_000)
    } finally {
      vi.useRealTimers()
    }
  })

  it('says while a file uploads that its name has no date, and stops saying it once it is in', async () => {
    const { items } = useUploadQueue('TAC')
    enqueue('TAC', [file('DeviceDatabase.csv')])
    await flushPromises()

    pending[0]!.preflight({ dateState: 'missing' })
    expect(items.value[0]!.note).toBe(
      'There is no date in this name. You will be asked for it when the upload finishes.',
    )

    pending[0]!.settle({
      kind: 'needsDate',
      fileId: 41,
      fileName: 'DeviceDatabase.csv',
      sizeBytes: 1_000,
      sha256: '',
      dateState: 'missing',
      dateCandidates: [],
      message: 'There is no date in the name DeviceDatabase.csv. It waits for someone to give it.',
    })
    await flushPromises()

    // Stored: an outcome, not a failure, and not the same as waiting its turn to upload.
    expect(items.value[0]!.status).toBe('needsDate')
    expect(items.value[0]!.message).toBe(
      'Stored, not queued yet. There is no date in the name DeviceDatabase.csv. It waits for someone to give it.',
    )
    expect(items.value[0]!.note).toBeUndefined()
  })

  it('says nothing of the date for a name that settles it, or for an archive', async () => {
    const { items } = useUploadQueue('SQM')
    enqueue('SQM', [file('daily_2026-10-17.csv')])
    await flushPromises()

    pending[0]!.preflight({ dateState: 'settled' })
    expect(items.value[0]!.note).toBeUndefined()

    pending[0]!.preflight({ dateState: null })
    expect(items.value[0]!.note).toBeUndefined()
  })

  it('says the date a queued file was given, and when it came from the archive rather than the file', async () => {
    const { items } = useUploadQueue('SQM')
    enqueue('SQM', [file('daily_2026-10-17.csv.gz')])
    await flushPromises()

    pending[0]!.settle({
      kind: 'queued',
      jobId: 77,
      fileName: 'daily_2026-10-17.csv',
      sizeBytes: 9,
      sha256: '',
      date: '2026-10-17',
      dateFrom: 'name',
    })
    await flushPromises()

    expect(items.value[0]!.message).toBe('Queued as import 77 · dated Oct 17, 2026')
  })

  it('lists what became of each file an archive held, and what was left out of it', async () => {
    const { items } = useUploadQueue('SQM')
    enqueue('SQM', [file('october.zip')])
    await flushPromises()

    const base = { fileId: 1, sizeBytes: 9, sha256: '', existingJobId: null, dateCandidates: [] }
    pending[0]!.settle({
      kind: 'archive',
      archive: 'october.zip',
      archiveKind: 'zip',
      files: [
        { ...base, fileName: 'a.csv', outcome: 'queued', jobId: 77, date: '2026-10-17', dateFrom: 'archive', dateState: 'settled', message: '' },
        { ...base, fileName: 'b.csv', outcome: 'waiting', jobId: null, date: null, dateFrom: null, dateState: 'missing', message: 'There is no date in the name b.csv.' },
        { ...base, fileName: 'c.csv', outcome: 'duplicate', jobId: null, existingJobId: 12, date: null, dateFrom: null, dateState: 'settled', message: '' },
      ],
      skipped: [{ name: 'readme.txt', reason: 'Not a .csv file.' }],
    })
    await flushPromises()

    const item = items.value[0]!
    expect(item.status).toBe('unpacked')
    expect(item.message).toBe('Unpacked: 3 files - 1 queued, 1 waiting for its date, 1 already imported; 1 left out')
    expect(item.lines).toEqual([
      { name: 'a.csv', tone: 'success', text: "Queued as import 77 · dated Oct 17, 2026, from the archive's name", jobId: 77 },
      { name: 'b.csv', tone: 'warning', text: 'Stored, not queued yet. There is no date in the name b.csv.', jobId: undefined },
      { name: 'c.csv', tone: 'warning', text: 'Already imported — see import 12', jobId: undefined },
      { name: 'readme.txt', tone: 'muted', text: 'Left out. Not a .csv file.' },
    ])
  })

  it('goes on to the next file after one that waits for its date', async () => {
    const { items } = useUploadQueue('SQM')
    enqueue('SQM', [file('subs.csv'), file('daily_2026-10-17.csv')])
    await flushPromises()

    pending[0]!.settle({ kind: 'needsDate', fileId: 1, fileName: 'subs.csv', sizeBytes: 9, sha256: '', dateState: 'missing', dateCandidates: [], message: 'x' })
    await flushPromises()

    expect(items.value.map((i) => i.status)).toEqual(['needsDate', 'uploading'])
  })

  it('outlives the page that started it', async () => {
    enqueue('SQM', [file('a.csv')])
    await flushPromises()

    // A component that mounts later - the Import Center opened again - sees the same upload.
    const later = useUploadQueue('SQM')
    expect(later.items.value.map((i) => [i.file.name, i.status])).toEqual([['a.csv', 'uploading']])
  })
})
