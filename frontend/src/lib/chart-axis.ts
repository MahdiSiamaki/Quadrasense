/**
 * The date labels under a time-series chart: the first and last days are always labelled, aligned to the
 * plot's edges, and a label that would overlap one of them is the one dropped.
 *
 * Aligning the end labels alone was not enough. ECharts labels a category axis every n-th day and adds the
 * first and last days besides; when the last day's label overlaps the one before it, ECharts drops the LAST
 * day's unless `showMaxLabel` is set - so the label left standing was a date centred a few days from the end,
 * which ran off the canvas: "2026-10-2" under the churn chart of a 268-day PNG export (2026-11-09). With
 * `showMinLabel`/`showMaxLabel` the end labels stay, aligned inside the plot, and their neighbour goes.
 *
 * One case is left: every date label has the same priority and the last day's is checked last, so when the
 * label two before it still reaches it, the last day goes unlabelled. Nothing is cut either way.
 */
export const edgeDateLabels = {
  hideOverlap: true,
  showMinLabel: true,
  showMaxLabel: true,
  alignMinLabel: 'left',
  alignMaxLabel: 'right',
} as const
