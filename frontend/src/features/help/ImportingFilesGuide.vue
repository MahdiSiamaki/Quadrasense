<script setup lang="ts">
import { RouterLink } from 'vue-router'

/** Help: bringing a file in - what may be sent, what is kept, and how its date is decided (ADR-025). */

/** How names are read, as FileNameDate reads them: each row is one of its tests. */
const READ = [
  { name: 'daily_subs_device_sim_info_2026-10-17.csv', date: '17 October 2026' },
  { name: 'subs_20261017.csv', date: '17 October 2026' },
  { name: 'subs_17-10-2026.csv', date: '17 October 2026' },
  { name: 'export_20261017235959.csv', date: '17 October 2026: a timestamp\'s date' },
  { name: 'DeviceDatabaseTAC09Nov2026.csv', date: '9 November 2026' },
  { name: 'TAC November 9, 2026.csv', date: '9 November 2026' },
]

const NOT_READ = [
  { name: 'subs_03-04-2026.csv', why: '3 April or 4 March: both are offered, and you choose.' },
  { name: 'dump_subs_device_sim_info_20251227_20260125.csv', why: 'Two dates: a range is not a day.' },
  { name: 'subs_17-10-26.csv', why: 'A two-digit year has more than one reading.' },
  { name: 'subs_1405-08-19.csv', why: 'Jalali dates are not read.' },
  { name: 'DeviceDatabase.csv', why: 'No date - unless it is the only file of an archive whose name has one.' },
]
</script>

<template>
  <article class="flex max-w-3xl flex-col gap-6 text-sm text-[var(--c-text-secondary)]">
    <header>
      <h2 class="text-lg font-semibold tracking-tight text-[var(--c-text)]">Importing files</h2>
      <p class="mt-1">
        Files are brought in on the
        <RouterLink to="/imports" class="font-medium text-[var(--c-accent)] hover:underline">Import Center</RouterLink>:
        drop them on the daily subscriber changes or the GSMA TAC database, or choose them. Several can
        go at once; they upload one after another, and a broken connection resumes where it stopped.
      </p>
    </header>

    <section>
      <h3 class="text-sm font-semibold text-[var(--c-text)]">What can be sent</h3>
      <p class="mt-1.5">
        A CSV, or a CSV packed as <strong class="text-[var(--c-text)]">ZIP</strong>,
        <strong class="text-[var(--c-text)]">RAR</strong> or <strong class="text-[var(--c-text)]">GZ</strong>.
        What a file is, is told from its contents, not its name: a ZIP called <code>.csv</code> is unpacked,
        and a CSV called <code>.zip</code> is read as the CSV it is.
      </p>
      <ul class="mt-2 list-disc space-y-1 pl-5">
        <li>
          <strong class="text-[var(--c-text)]">What is kept is the file inside, not the archive.</strong>
          So the same file sent bare and sent packed is one file, imported once: the second time it says
          <em>Already imported</em>.
        </li>
        <li>
          From an archive, only <code>.csv</code> files are taken. Anything else in it - a readme, what a
          packing program adds - is listed as left out, with the reason.
        </li>
        <li>
          An archive is taken <strong class="text-[var(--c-text)]">whole or not at all</strong>: if one file in it
          is damaged, none is kept. Send it again once it is whole.
        </li>
        <li>
          Refused, with the reason: an archive with a password, one part of an archive split into several, an
          archive inside an archive, and 7z, tar, xz or bzip2. At most 100 files from one archive, 10 GB each and
          20 GB in all.
        </li>
      </ul>
    </section>

    <section>
      <h3 class="text-sm font-semibold text-[var(--c-text)]">How a file gets its date</h3>
      <p class="mt-1.5">
        A daily file's day is in its name and nowhere else, and a wrong day would be believed by every figure
        after it. So a name is read only where it can mean one date. These are read:
      </p>
      <div class="relative mt-2 overflow-x-auto rounded-[var(--radius-md)] border">
        <table class="w-full text-left text-xs">
          <thead class="border-b bg-[var(--c-surface-sunken)] text-2xs tracking-wide whitespace-nowrap text-[var(--c-text-muted)] uppercase">
            <tr>
              <th scope="col" class="px-3 py-1.5 font-medium">File name</th>
              <th scope="col" class="px-3 py-1.5 font-medium">Read as</th>
            </tr>
          </thead>
          <tbody class="divide-y">
            <tr v-for="row in READ" :key="row.name">
              <td class="px-3 py-1.5 font-mono wrap-anywhere">{{ row.name }}</td>
              <td class="px-3 py-1.5">{{ row.date }}</td>
            </tr>
          </tbody>
        </table>
      </div>

      <p class="mt-3">These are not, and the file waits for you instead:</p>
      <div class="relative mt-2 overflow-x-auto rounded-[var(--radius-md)] border">
        <table class="w-full text-left text-xs">
          <thead class="border-b bg-[var(--c-surface-sunken)] text-2xs tracking-wide whitespace-nowrap text-[var(--c-text-muted)] uppercase">
            <tr>
              <th scope="col" class="px-3 py-1.5 font-medium">File name</th>
              <th scope="col" class="px-3 py-1.5 font-medium">Why</th>
            </tr>
          </thead>
          <tbody class="divide-y">
            <tr v-for="row in NOT_READ" :key="row.name">
              <td class="px-3 py-1.5 font-mono wrap-anywhere">{{ row.name }}</td>
              <td class="px-3 py-1.5">{{ row.why }}</td>
            </tr>
          </tbody>
        </table>
      </div>

      <ul class="mt-3 list-disc space-y-1 pl-5">
        <li>
          <strong class="text-[var(--c-text)]">The archive's name counts</strong> when the file is the only one in
          it. GSMA sends <code>DeviceDatabaseTAC09Nov2026.rar</code> holding <code>DeviceDatabase.csv</code>: the
          catalogue is dated 9 November 2026 from the archive.
        </li>
        <li>No date after tomorrow is taken, from a name or from a person.</li>
      </ul>
    </section>

    <section>
      <h3 class="text-sm font-semibold text-[var(--c-text)]">A file that waits for its date</h3>
      <p class="mt-1.5">
        It is stored, so nothing has to be sent again, and it has no import yet. It is listed at the top of the
        Import Center: where its name can mean two dates they are two buttons, and where it has none you type the
        date. Choosing one imports it from the stored copy. Each import's page says what its date is and who gave
        it - its name, its archive's name, or a person.
      </p>
    </section>

    <section>
      <h3 class="text-sm font-semibold text-[var(--c-text)]">The GSMA catalogue</h3>
      <p class="mt-1.5">
        Upload GSMA's file as it arrives, <code>.rar</code> and all. It is loaded as a new version, compared with
        the active one, and labelled by its date. Nothing on any page changes until an administrator activates it,
        on the Import Center, after reading what it would change.
      </p>
    </section>

    <section>
      <h3 class="text-sm font-semibold text-[var(--c-text)]">From the server</h3>
      <p class="mt-1.5">
        A file too large to send over a slow connection can be carried to the server and handed over there - the
        same formats, the same checks, the same queue:
      </p>
      <pre class="mt-2 overflow-x-auto rounded-[var(--radius-md)] border bg-[var(--c-surface-sunken)] px-3 py-2 font-mono text-xs text-[var(--c-text)]"><code>--import-file &lt;file&gt; [--source SQM|TAC] [--date YYYY-MM-DD]</code></pre>
      <p class="mt-1.5">
        <code>--date</code> gives the date of a file whose name does not say it.
      </p>
    </section>
  </article>
</template>
