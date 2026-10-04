import type { TransformedChartData } from '$lib/utils/chart-data-transform';

/**
 * Merge historical chart data into initial chart data.
 * Historical data contains older records that should be prepended to arrays.
 */
export function mergeChartData(
	initial: TransformedChartData,
	historical: TransformedChartData | null
): TransformedChartData {
	if (!historical) return initial;

	// Helper to merge arrays by time, avoiding duplicates
	const timeValue = (value: unknown) => (value instanceof Date ? value.getTime() : value);
	const mergeByTime = <T,>(
		initialArr: T[],
		historicalArr: T[],
		timeOf: (item: T) => unknown
	): T[] => {
		if (!initialArr || !historicalArr) return initialArr || historicalArr || [];
		const initialTimes = new Set(initialArr.map((item) => timeValue(timeOf(item))));
		const uniqueHistorical = historicalArr.filter((item) => {
			const time = timeValue(timeOf(item));
			return !initialTimes.has(time);
		});
		return [...uniqueHistorical, ...initialArr];
	};

	// Helper to merge span arrays by id, falling back to startTime dedup.
	// Spans that straddle the initial/historical boundary can appear in both
	// datasets with the same id but different startTime, so time-based dedup
	// alone would let duplicates through and cause Svelte each_key_duplicate.
	const mergeSpansById = <T extends { id?: unknown }>(
		initialArr: T[],
		historicalArr: T[]
	): T[] => {
		if (!initialArr || !historicalArr) return initialArr || historicalArr || [];
		const seenIds = new Set(
			initialArr.map((item) => item.id).filter((id: unknown) => id != null)
		);
		const uniqueHistorical = historicalArr.filter((item) => {
			if (item.id != null) {
				if (seenIds.has(item.id)) return false;
				seenIds.add(item.id);
				return true;
			}
			// No id — fall back to startTime dedup
			return true;
		});
		return [...uniqueHistorical, ...initialArr];
	};

	// Every field is listed explicitly rather than spreading `initial`. A spread
	// satisfies the return type on its own, so a collection left out of the merge
	// keeps only the initial window's rows instead of failing to compile.
	return {
		// Time series
		iobSeries: mergeByTime(initial.iobSeries, historical.iobSeries, (p) => p.time),
		cobSeries: mergeByTime(initial.cobSeries, historical.cobSeries, (p) => p.time),
		basalSeries: mergeByTime(initial.basalSeries, historical.basalSeries, (p) => p.timestamp),
		glucoseData: mergeByTime(initial.glucoseData, historical.glucoseData, (p) => p.time),
		heartRateSeries: mergeByTime(initial.heartRateSeries, historical.heartRateSeries, (p) => p.time),
		stepSeries: mergeByTime(initial.stepSeries, historical.stepSeries, (p) => p.time),

		// Merge markers (keyed by time)
		bolusMarkers: mergeByTime(initial.bolusMarkers, historical.bolusMarkers, (p) => p.time),
		carbMarkers: mergeByTime(initial.carbMarkers, historical.carbMarkers, (p) => p.time),
		deviceEventMarkers: mergeByTime(initial.deviceEventMarkers, historical.deviceEventMarkers, (p) => p.time),
		bgCheckMarkers: mergeByTime(initial.bgCheckMarkers, historical.bgCheckMarkers, (p) => p.time),

		// Merge markers and spans keyed by id in {#each} blocks — must dedup by id
		systemEventMarkers: mergeSpansById(initial.systemEventMarkers, historical.systemEventMarkers),
		trackerMarkers: mergeSpansById(initial.trackerMarkers, historical.trackerMarkers),
		basalInjectionMarkers: mergeSpansById(
			initial.basalInjectionMarkers,
			historical.basalInjectionMarkers
		),
		pumpModeSpans: mergeSpansById(initial.pumpModeSpans, historical.pumpModeSpans),
		profileSpans: mergeSpansById(initial.profileSpans, historical.profileSpans),
		overrideSpans: mergeSpansById(initial.overrideSpans, historical.overrideSpans),
		activitySpans: mergeSpansById(initial.activitySpans, historical.activitySpans),
		tempBasalSpans: mergeSpansById(initial.tempBasalSpans, historical.tempBasalSpans),
		basalDeliverySpans: mergeSpansById(
			initial.basalDeliverySpans,
			historical.basalDeliverySpans
		),

		// Thresholds are profile-derived and identical across both halves, except
		// glucoseYMax, which the server sizes to the max SGV it was asked for — the
		// streamed half can carry a higher excursion, and the chart's yDomain clips
		// anything above it.
		defaultBasalRate: initial.defaultBasalRate,
		thresholds: {
			...initial.thresholds,
			glucoseYMax: Math.max(
				initial.thresholds.glucoseYMax,
				historical.thresholds.glucoseYMax
			),
		},

		// Take the max values from either dataset
		maxIob: Math.max(initial.maxIob ?? 0, historical.maxIob ?? 0),
		maxCob: Math.max(initial.maxCob ?? 0, historical.maxCob ?? 0),
		maxBasalRate: Math.max(initial.maxBasalRate ?? 0, historical.maxBasalRate ?? 0),
	};
}

/**
 * Swap the window starting at `startTime` for a fresh fetch of it, leaving older
 * rows alone. Unlike `mergeChartData`, rows inside the window that the server no
 * longer returns (a deleted or moved treatment) do not survive. A treatment moved
 * to before `startTime` is therefore dropped until the next full load.
 */
export function replaceWindow(
	current: TransformedChartData,
	recent: TransformedChartData,
	startTime: number
): TransformedChartData {
	const ms = (value: unknown) => (value instanceof Date ? value.getTime() : Number(value));
	const swap = <T,>(older: T[], fresh: T[], timeOf: (item: T) => unknown): T[] => [
		...older.filter((item) => ms(timeOf(item)) < startTime),
		...[...fresh].sort((a, b) => ms(timeOf(a)) - ms(timeOf(b))),
	];
	// A span that began before the window and is still running comes back in the
	// fresh fetch under the same id, so ids in the fresh set replace the old row too.
	const swapSpans = <T extends { id?: unknown; startTime: Date }>(
		older: T[],
		fresh: T[]
	): T[] => {
		const freshIds = new Set(fresh.map((s) => s.id).filter((id) => id != null));
		return [
			...older.filter(
				(s) => s.startTime.getTime() < startTime && !(s.id != null && freshIds.has(s.id))
			),
			...[...fresh].sort((a, b) => a.startTime.getTime() - b.startTime.getTime()),
		];
	};
	const peak = (floor: number, values: Iterable<number | undefined>) =>
		Math.max(floor, ...Array.from(values, (v) => v ?? 0));

	const glucoseData = swap(current.glucoseData, recent.glucoseData, (p) => p.time);
	const glucoseYMax = peak(
		recent.thresholds.glucoseYMax,
		glucoseData.map((p) => p.sgv)
	);
	const iobSeries = swap(current.iobSeries, recent.iobSeries, (p) => p.time);
	const cobSeries = swap(current.cobSeries, recent.cobSeries, (p) => p.time);
	const basalSeries = swap(current.basalSeries, recent.basalSeries, (p) => p.timestamp);

	return {
		iobSeries,
		cobSeries,
		basalSeries,
		glucoseData,
		heartRateSeries: swap(current.heartRateSeries, recent.heartRateSeries, (p) => p.time),
		stepSeries: swap(current.stepSeries, recent.stepSeries, (p) => p.time),
		bolusMarkers: swap(current.bolusMarkers, recent.bolusMarkers, (p) => p.time),
		carbMarkers: swap(current.carbMarkers, recent.carbMarkers, (p) => p.time),
		deviceEventMarkers: swap(current.deviceEventMarkers, recent.deviceEventMarkers, (p) => p.time),
		bgCheckMarkers: swap(current.bgCheckMarkers, recent.bgCheckMarkers, (p) => p.time),
		systemEventMarkers: swap(current.systemEventMarkers, recent.systemEventMarkers, (p) => p.time),
		trackerMarkers: swap(current.trackerMarkers, recent.trackerMarkers, (p) => p.time),
		basalInjectionMarkers: swap(
			current.basalInjectionMarkers,
			recent.basalInjectionMarkers,
			(p) => p.time
		),
		pumpModeSpans: swapSpans(current.pumpModeSpans, recent.pumpModeSpans),
		profileSpans: swapSpans(current.profileSpans, recent.profileSpans),
		overrideSpans: swapSpans(current.overrideSpans, recent.overrideSpans),
		activitySpans: swapSpans(current.activitySpans, recent.activitySpans),
		tempBasalSpans: swapSpans(current.tempBasalSpans, recent.tempBasalSpans),
		basalDeliverySpans: swapSpans(current.basalDeliverySpans, recent.basalDeliverySpans),
		defaultBasalRate: current.defaultBasalRate,
		thresholds: { ...current.thresholds, glucoseYMax },
		maxIob: peak(recent.maxIob ?? 0, iobSeries.map((p) => p.value)),
		maxCob: peak(recent.maxCob ?? 0, cobSeries.map((p) => p.value)),
		maxBasalRate: peak(recent.maxBasalRate ?? 0, basalSeries.map((p) => p.rate ?? undefined)),
	};
}
