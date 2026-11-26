<script setup lang="ts">
import { RouterLink } from 'vue-router'

/** Help: what PI counts, in which unit, and what no count includes (CLAUDE.md, Counting rules; ADR-020). */
</script>

<template>
  <article class="flex max-w-3xl flex-col gap-6 text-sm text-[var(--c-text-secondary)]">
    <header>
      <h2 class="text-lg font-semibold tracking-tight text-[var(--c-text)]">Reading the numbers</h2>
      <p class="mt-1">
        Every figure in PI counts one of three things, and the same brand can be three different numbers at once.
        All three are right; they answer different questions. Each page says which it counts.
      </p>
    </header>

    <section>
      <h3 class="text-sm font-semibold text-[var(--c-text)]">The unit: a binding</h3>
      <p class="mt-1.5">
        A <strong class="text-[var(--c-text)]">binding</strong> is a number, a SIM and a handset seen together - an
        MSISDN, an IMSI and an IMEI. It is what the daily files add and remove, and what PI keeps.
      </p>
      <ul class="mt-2 list-disc space-y-1 pl-5">
        <li><strong class="text-[var(--c-text)]">Bindings</strong> - how many number, SIM and handset combinations.</li>
        <li><strong class="text-[var(--c-text)]">Subscribers</strong> - how many numbers.</li>
        <li><strong class="text-[var(--c-text)]">Handsets</strong> - how many IMEIs.</li>
      </ul>
      <p class="mt-2">
        A number whose SIM moved to a new phone is one subscriber with two bindings in its history; a phone used with
        one SIM and then another is one handset in two bindings.
      </p>
    </section>

    <section>
      <h3 class="text-sm font-semibold text-[var(--c-text)]">Estimated figures</h3>
      <p class="mt-1.5">
        Counts of distinct subscribers or handsets on the dashboards are estimates, accurate to about half a per
        cent, and labelled <em>estimated</em>. Counting tens of millions of distinct values exactly on every page
        would cost far more than the figure's last digits are worth.
      </p>
    </section>

    <section>
      <h3 class="text-sm font-semibold text-[var(--c-text)]">What no count includes</h3>
      <p class="mt-1.5">
        A row with <strong class="text-[var(--c-text)]">no handset</strong> is kept and left out of every count: an
        IMEI of <code>000000</code>, an IMEI that is not fourteen digits, or fourteen digits no handset could carry -
        a zero TAC, a shifted IMEI, a reporting body GSMA never assigned. Such rows are shown on
        <RouterLink to="/quality" class="font-medium text-[var(--c-accent)] hover:underline">Data quality</RouterLink>,
        and in the Explorer when a search names an IMEI or a TAC.
      </p>
      <p class="mt-2">
        A TAC that is only missing from the GSMA catalogue is a real handset whose model is not known yet. It is
        counted, under an unknown model.
      </p>
    </section>

    <section>
      <h3 class="text-sm font-semibold text-[var(--c-text)]">Dual-SIM handsets</h3>
      <p class="mt-1.5">
        A dual-SIM phone has two IMEIs, one for each radio, and handsets are counted by IMEI - so it counts as two.
        Nothing in the files says which two IMEIs belong to one phone. The Explorer's relationships view shows them as
        a pair when the same number has been seen on both and the makers' numbering links them: Xiaomi and Redmi give
        the second IMEI the next serial in the same TAC; Samsung gives it a twin TAC, one hundred higher, with the same
        serial.
      </p>
    </section>

    <section>
      <h3 class="text-sm font-semibold text-[var(--c-text)]">How current it is</h3>
      <p class="mt-1.5">
        The top bar says <em>data through</em> a date: the newest day imported. Figures describe the network as of
        that day, not as of today. A day missing from the files is listed on the Import Center.
      </p>
    </section>
  </article>
</template>
