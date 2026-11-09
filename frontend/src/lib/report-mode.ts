/**
 * Whether a component is being drawn into an export - an image or a printout - rather than on a live page.
 *
 * A report is read, not used: nothing in it can be hovered, scrolled or zoomed, and it is copied pixel for
 * pixel. So a chart in a report shows every day it was given (no zoom window, no slider), and draws its
 * canvas at the export's pixel ratio rather than the screen's, or the image would upscale a blurry bitmap.
 *
 * Provided once, by the report (`ReportView`); a component that does not ask behaves as on any page.
 */
import { inject, provide, type InjectionKey } from 'vue'

export interface ReportMode {
  /** Device pixels per CSS pixel the export is captured at. */
  readonly pixelRatio: number
}

/**
 * Device pixels per CSS pixel an export is captured at. The report's charts draw at it and the capture copies at
 * it: if the two differed, every canvas would be scaled once more, and blur.
 */
export const EXPORT_PIXEL_RATIO = 2

const REPORT_MODE: InjectionKey<ReportMode> = Symbol('report-mode')

/** Marks everything below the caller as part of a report. */
export function provideReportMode(mode: ReportMode): void {
  provide(REPORT_MODE, mode)
}

/** The report this component is drawn into, or null on a live page. */
export function useReportMode(): ReportMode | null {
  return inject(REPORT_MODE, null)
}

/** ECharts' init options for a chart in a report: its canvas drawn at the report's pixel ratio. */
export function reportInitOptions(report: ReportMode | null): { devicePixelRatio?: number } {
  return report ? { devicePixelRatio: report.pixelRatio } : {}
}
