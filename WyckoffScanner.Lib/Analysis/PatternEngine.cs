using System;
using WyckoffScanner.Lib.Models;

namespace WyckoffScanner.Lib.Analysis;

/// <summary>Best candidate found by a <see cref="PatternEngine"/>. Flag is the accumulation-style stage (1..3).</summary>
internal sealed class Candidate
{
    public SellingExhaustion Exhaustion { get; init; } = null!;
    public AutomaticRally? Rally { get; set; }
    public List<AccumulationZone> Zones { get; set; } = new();
    public SpringInfo? Spring { get; set; }
    public SosInfo? Sos { get; set; }
    public WyckoffFlag Flag { get; set; } = WyckoffFlag.None;

    /// <summary>Sharp (V-style bounce) or Rounded (gradual volume fade, saucer shape).</summary>
    public ClimaxStyle Style => Exhaustion.Style;

    public int Rank =>
        (int)Flag * 10 + 1 + (Rally != null ? 1 : 0) + (Zones.Count > 0 ? 1 : 0) +
        (Spring != null ? 1 : 0) + (Sos != null ? 1 : 0);
}


/// <summary>
/// The five-phase detector (selling exhaustion -> automatic rally -> accumulation -> spring -> sign of strength).
/// It always looks for the bullish sequence in the series it is given. Distribution is found by feeding it a
/// price-inverted series (see <see cref="PriceSeries"/>) with options from <see cref="AnalyzerOptions.ForDistribution"/>.
///
/// Phase 1/2 come in two styles that share everything from phase 3 onwards:
///   Sharp   - big range expansion, fading daily swings, climax volume, then a quick bounce on moderate volume.
///   Rounded - volume peaks mid-decline and fades steadily into the low, the fall decelerates, price forms a bowl
///             (parabola fit) and recovers gradually with higher lows.
/// Both styles are evaluated and the most advanced candidate wins.
/// Stateless, so one instance can be used from many threads.
/// </summary>
internal sealed class PatternEngine
{
    private readonly AnalyzerOptions _o;

    public PatternEngine(AnalyzerOptions options) => _o = options;

    public Candidate? FindBest(PriceSeries s)
    {
        Candidate? best = null;

        foreach (int low in FindPivotLows(s, _o.PivotWindow))
        {
            var c = EvaluateCandidate(s, low, ClimaxStyle.Sharp);
            if (c != null && IsBetter(c, best))
                best = c;
        }

        if (_o.DetectRoundedClimax)
        {
            foreach (int low in FindPivotLows(s, _o.RoundedPivotWindow))
            {
                var c = EvaluateCandidate(s, low, ClimaxStyle.Rounded);
                if (c != null && IsBetter(c, best))
                    best = c;
            }
        }

        return best;
    }

    /// <summary>Higher rank wins; on equal rank the later low wins (same low: the first one found, i.e. sharp, is kept).</summary>
    private static bool IsBetter(Candidate c, Candidate? best)
    {
        if (best == null) 
            return true;
        if (c.Rank != best.Rank) 
            return c.Rank > best.Rank;
        return c.Exhaustion.LowIndex > best.Exhaustion.LowIndex;
    }

    // ------------------------------------------------------------------------------------------------
    // Candidate evaluation
    // ------------------------------------------------------------------------------------------------

    private Candidate? EvaluateCandidate(PriceSeries s, int lowIdx, ClimaxStyle style)
    {
        var ex = style == ClimaxStyle.Sharp
            ? DetectSellingExhaustion(s, lowIdx)
            : DetectRoundedExhaustion(s, lowIdx);
        if (ex == null)
            return null;

        var cand = new Candidate { Exhaustion = ex };

        var ar = style == ClimaxStyle.Sharp
            ? DetectAutomaticRally(s, ex)
            : DetectGradualRecovery(s, ex);
        if (ar == null)
            return cand;

        // Invalidation is checked through the latest bar, not just through the date on which
        // the rally was identified. This prevents a later decisive breakdown from leaving an
        // old accumulation candidate active.
        double floor = s.Low[lowIdx] * (1 - _o.StructureBreakdownPct);
        for (int i = ar.Index + 1; i < s.Count; i++)
        {
            if (s.Close[i] < floor)
                return null;
        }

        cand.Rally = ar;
        cand.Zones = DetectAccumulation(s, ex, ar);
        if (cand.Zones.Count == 0)
            return cand;

        // Evaluate springs only after an accepted accumulation zone. Keep the most recent
        // equally-ranked spring so the returned event represents the latest structure.
        WyckoffFlag bestFlag = WyckoffFlag.None;
        foreach (var spring in DetectSprings(s, cand.Zones))
        {
            var sos = DetectSignOfStrength(s, spring);
            var flag = ClassifySpring(s, spring, sos);
            // Only expose a sign-of-strength event when it is still a valid current signal.
            // A failed/aged breakout must not hide an otherwise valid recent spring.
            if (flag != WyckoffFlag.SosDetected)
                sos = null;

            if (flag > bestFlag || (flag == bestFlag && spring.Index >= (cand.Spring?.Index ?? -1)))
            {
                bestFlag = flag;
                cand.Spring = spring;
                cand.Sos = sos;
            }
        }

        int last = s.Count - 1;
        // A recent box is only "progressing" when price is still inside/near that box.
        // A fresh box followed by a large move away should not be labelled as active accumulation.
        var latestZone = cand.Zones[^1];
        bool recentBox = latestZone.End >= last - _o.AccumulationMaxAgeBars;
        bool priceNearBox = s.Close[last] >= latestZone.Low * (1 - _o.StructureBreakdownPct) &&
                            s.Close[last] <= latestZone.High * (1 + _o.MaxAccumulationRangePct);
        if (bestFlag == WyckoffFlag.None && recentBox && priceNearBox)
            bestFlag = WyckoffFlag.LikelyProgressing;

        cand.Flag = bestFlag;
        return cand;
    }

    private WyckoffFlag ClassifySpring(PriceSeries s, SpringInfo spring, SosInfo? sos)
    {
        int last = s.Count - 1;

        if (sos != null)
        {
            bool recent = last - sos.Index <= _o.SosMaxAgeBars;
            bool holding = s.Close[last] >= sos.Resistance * (1 - _o.SosHoldTolerancePct);
            if (recent && holding)
                return WyckoffFlag.SosDetected;
            // If the breakout is no longer valid, fall back to the spring's own status.
        }

        bool springRecent = last - spring.Index <= _o.SpringMaxAgeBars;

        // Use the structure-breakdown threshold here. MaxSpringPiercePct describes the
        // depth of the spring wick, not how far price may subsequently fall and remain valid.
        double invalidationFloor = spring.Support * (1 - _o.StructureBreakdownPct);
        bool notBroken = s.Close[last] >= invalidationFloor;
        return springRecent && notBroken ? WyckoffFlag.SpringDetected : WyckoffFlag.None;
    }

    // ------------------------------------------------------------------------------------------------
    // Swing lows
    // ------------------------------------------------------------------------------------------------

    private static IEnumerable<int> FindPivotLows(PriceSeries s, int w)
    {
        for (int i = w; i < s.Count - w; i++)
        {
            bool isLow = true;
            for (int k = 1; k <= w && isLow; k++)
            {
                if (s.Low[i - k] <= s.Low[i]) isLow = false;   // strictly lower on the left
                if (s.Low[i + k] < s.Low[i]) isLow = false;    // not lower on the right
            }
            if (isLow) 
                yield return i;
        }
    }

    // ------------------------------------------------------------------------------------------------
    // 1a. Selling exhaustion (SHARP): a sizeable decline whose daily price swings were large and then gradually
    //     shrank into the low, with a volume climax around the peak of the selling.
    // ------------------------------------------------------------------------------------------------

    private SellingExhaustion? DetectSellingExhaustion(PriceSeries s, int low)
    {
        int start = Math.Max(0, low - _o.MaxDeclineLookbackBars);
        if (low - start < _o.MinDeclineBars) 
            return null;

        int high = start;
        for (int i = start; i < low; i++)
            if (s.High[i] >= s.High[high]) high = i;
        if (low - high < _o.MinDeclineBars) 
            return null;

        double decline = (s.High[high] - s.Low[low]) / s.High[high];
        if (decline < _o.MinDeclinePct) 
            return null;

        int peakMax = low - _o.MinTailBars;
        if (peakMax < high + 1) 
            return null;

        int peak = high + 1;
        for (int i = high + 1; i <= peakMax; i++)
        {
            if (s.SmoothedTrPct[i] > s.SmoothedTrPct[peak]) 
                peak = i;
        }
        
        double peakIntensity = s.SmoothedTrPct[peak];
        if (peakIntensity < _o.LargeSellingMultiple * s.MedianTrPct) 
            return null;       // selling wasn't "large"

        int endFrom = Math.Max(peak + 1, low - 2);
        double endIntensity = PriceSeries.Mean(s.TrPct, endFrom, low);
        if (endIntensity > _o.MaxExhaustionDecayRatio * peakIntensity) 
            return null;    // didn't fade enough
        if (PriceSeries.Slope(s.SmoothedTrPct, peak, low) >= 0) 
            return null;            // not a gradual decrease

        int vFrom = Math.Max(0, peak - 1), vTo = Math.Min(s.Count - 1, peak + 1);
        double climaxVol = 0;
        for (int i = vFrom; i <= vTo; i++) 
            climaxVol = Math.Max(climaxVol, s.Volume[i]);
        
        double volRatio = climaxVol / s.MedianVolume;
        if (_o.ClimaxVolumeMultiple > 0 && volRatio < _o.ClimaxVolumeMultiple) 
            return null;

        return new SellingExhaustion(high, peak, low, decline, peakIntensity, endIntensity, climaxVol, volRatio);
    }

    // ------------------------------------------------------------------------------------------------
    // 1b. Selling exhaustion (ROUNDED): a longer decline where volume peaks mid-leg and then fades steadily into the
    //     low, the fall decelerates, and the closes from the decline high through the early recovery trace a bowl.
    // ------------------------------------------------------------------------------------------------

    private SellingExhaustion? DetectRoundedExhaustion(PriceSeries s, int low)
    {
        int start = Math.Max(0, low - _o.MaxDeclineLookbackBars);
        if (low - start < _o.RoundedMinDeclineBars) 
            return null;

        int high = start;
        for (int i = start; i < low; i++)
            if (s.High[i] >= s.High[high]) high = i;

        int len = low - high;
        if (len < _o.RoundedMinDeclineBars) 
            return null;

        double decline = (s.High[high] - s.Low[low]) / s.High[high];
        if (decline < _o.MinDeclinePct) 
            return null;

        // Volume peak somewhere in the decline, leaving a long enough tail for the fade.
        int peakMax = low - _o.RoundedMinTailBars;
        if (peakMax < high + 1) 
            return null;

        int peak = high + 1;
        for (int i = high + 1; i <= peakMax; i++)
        {
            if (s.SmoothedVolume[i] > s.SmoothedVolume[peak]) 
                peak = i;
        }

        double peakVol = s.SmoothedVolume[peak];
        if (peakVol < _o.RoundedClimaxVolumeMultiple * s.MedianVolume) 
            return null;

        // Volume must fade steadily into the low.
        int endFrom = Math.Max(peak + 1, low - 4);
        double tailVol = PriceSeries.Mean(s.Volume, endFrom, low);
        double fade = tailVol / peakVol;
        if (fade > _o.RoundedMaxVolumeFadeRatio) 
            return null;
        if (PriceSeries.Slope(s.SmoothedVolume, peak, low) >= 0) 
            return null;

        // Price must decelerate: the last third of the fall is clearly flatter than the first third.
        int third = Math.Max(2, len / 3);
        double firstSlope = PriceSeries.Slope(s.Close, high, high + third);
        double lastSlope = PriceSeries.Slope(s.Close, low - third, low);
        if (firstSlope >= 0 || lastSlope < firstSlope * _o.RoundedMaxDecelRatio) 
            return null;

        // Bowl: parabola through decline + early recovery, opening upwards, turning point near the low.
        int after = Math.Min(s.Count - 1 - low, _o.RoundedRecoveryBars);
        if (after < _o.RoundedMinRecoveryBars) 
            return null;       // not enough bars after the low to confirm the shape

        if (!PriceSeries.FitParabola(s.Close, high, low + after, out double a, out double vertex, out double r2))
            return null;
        if (a <= 0 || r2 < _o.RoundedMinRSquared || Math.Abs(vertex - low) > _o.RoundedVertexToleranceBars) 
            return null;

        double peakIntensity = s.SmoothedTrPct[peak];
        double endIntensity = PriceSeries.Mean(s.TrPct, endFrom, low);

        int vFrom = Math.Max(0, peak - 2), vTo = Math.Min(s.Count - 1, peak + 2);
        double climaxVol = 0;
        for (int i = vFrom; i <= vTo; i++)
            climaxVol = Math.Max(climaxVol, s.Volume[i]);

        return new SellingExhaustion(high, peak, low, decline, peakIntensity, endIntensity,
            climaxVol, climaxVol / s.MedianVolume, ClimaxStyle.Rounded, fade, r2);
    }

    // ------------------------------------------------------------------------------------------------
    // 2a. Automatic rally (SHARP): selling stops, price bounces on moderate (not climactic) volume.
    // ------------------------------------------------------------------------------------------------

    private AutomaticRally? DetectAutomaticRally(PriceSeries s, SellingExhaustion ex)
    {
        int low = ex.LowIndex;
        int end = Math.Min(s.Count - 1, low + _o.AutoRallyMaxBars);
        if (end - low < 3) 
            return null;

        // Use the first confirmed local swing high rather than the highest high anywhere
        // in the full window. The latter can accidentally absorb a later markup leg and
        // mislabel it as the automatic rally.
        int a = -1;
        for (int i = low + 2; i <= end - 2; i++)
        {
            bool localHigh = s.High[i] >= s.High[i - 1] &&
                             s.High[i] >= s.High[i - 2] &&
                             s.High[i] > s.High[i + 1] &&
                             s.High[i] > s.High[i + 2];
            if (localHigh)
            {
                a = i;
                break;
            }
        }

        if (a < 0)
            return null; // no confirmed rally top inside the confirmation window

        double rise = s.High[a] - s.Low[low];
        double rallyPct = rise / s.Low[low];
        double declineAmt = s.High[ex.HighIndex] - s.Low[low];
        double retrace = declineAmt > 0 ? rise / declineAmt : 0;
        if (rallyPct < _o.MinRallyPct || retrace < _o.MinRetraceOfDecline) 
            return null;

        double avgVol = PriceSeries.Mean(s.Volume, low + 1, a);
        double volRatio = avgVol / s.MedianVolume;
        if (volRatio < _o.RallyVolumeMinRatio || volRatio > _o.RallyVolumeMaxRatio) 
            return null;
        if (avgVol >= ex.ClimaxVolume) 
            return null;     // moderate = quieter than the selling climax

        return new AutomaticRally(a, s.High[a], rallyPct, retrace, volRatio);
    }

    // ------------------------------------------------------------------------------------------------
    // 2b. Gradual recovery (ROUNDED): price climbs out of the bowl over many bars with higher lows on quiet volume.
    //     A recovery that is still in progress at the last bar is accepted (the candidate then simply has no
    //     accumulation box yet).
    // ------------------------------------------------------------------------------------------------

    private AutomaticRally? DetectGradualRecovery(PriceSeries s, SellingExhaustion ex)
    {
        int low = ex.LowIndex;
        int end = Math.Min(s.Count - 1, low + _o.RoundedMaxRecoveryBars);
        if (end - low < _o.RoundedMinRecoveryBars) 
            return null;

        int a = low + 1;
        for (int i = low + 1; i <= end; i++)
        {
            if (s.High[i] >= s.High[a])
                a = i;
        }

        if (a - low < _o.RoundedMinRecoveryBars) 
            return null;      // too quick to be a gradual recovery (the sharp detector covers that)

        double rise = s.High[a] - s.Low[low];
        double rallyPct = rise / s.Low[low];
        double declineAmt = s.High[ex.HighIndex] - s.Low[low];
        double retrace = declineAmt > 0 ? rise / declineAmt : 0;
        if (rallyPct < _o.RoundedMinRecoveryPct || retrace < _o.MinRetraceOfDecline) 
            return null;

        if (PriceSeries.Slope(s.Low, low, a) <= 0) 
            return null;              // no pattern of higher lows

        double avgVol = PriceSeries.Mean(s.Volume, low + 1, a);
        double volRatio = avgVol / s.MedianVolume;
        if (volRatio > _o.RallyVolumeMaxRatio) 
            return null;
        if (avgVol >= ex.ClimaxVolume) 
            return null;

        return new AutomaticRally(a, s.High[a], rallyPct, retrace, volRatio, Gradual: true);
    }

    // ------------------------------------------------------------------------------------------------
    // 3. Accumulation: tight, low-volume consolidation boxes after the rally (there may be several).
    // ------------------------------------------------------------------------------------------------

    private List<AccumulationZone> DetectAccumulation(PriceSeries s, SellingExhaustion ex, AutomaticRally ar)
    {
        var zones = new List<AccumulationZone>();
        double floor = s.Low[ex.LowIndex] * (1 - _o.StructureBreakdownPct);
        // Keep the original cap but avoid accepting an invalid/non-positive range denominator.
        double cap = ar.High * 1.05;
        int n = s.Count;
        int i = ar.Index + 1;

        while (i + _o.MinAccumulationBars <= n)
        {
            double hi = double.MinValue, lo = double.MaxValue;
            int e = i - 1;

            // Extend a candidate box until its range or structural boundaries are breached.
            while (e + 1 < n)
            {
                double nextHigh = s.High[e + 1];
                double nextLow = s.Low[e + 1];
                if (!double.IsFinite(nextHigh) || !double.IsFinite(nextLow) || nextLow <= 0)
                    break;

                double nh = Math.Max(hi, nextHigh);
                double nl = Math.Min(lo, nextLow);
                if (nl <= 0 || (nh - nl) / nl > _o.MaxAccumulationRangePct ||
                    nl < floor || nh > cap)
                    break;

                hi = nh;
                lo = nl;
                e++;
            }

            int length = e - i + 1;
            if (length >= _o.MinAccumulationBars)
            {
                double meanVolume = PriceSeries.Mean(s.Volume, i, e);
                double ratio = s.MedianVolume > 0 ? meanVolume / s.MedianVolume : double.PositiveInfinity;
                if (ratio <= _o.AccumulationVolumeRatio)
                {
                    zones.Add(new AccumulationZone(i, e, lo, hi, ratio));
                    i = e + 1;
                    continue;
                }
            }

            i++;
        }

        return zones;
    }

    // ------------------------------------------------------------------------------------------------
    // 4. Spring: after bouncing between support and resistance, a brief dip below support that quickly recovers.
    // ------------------------------------------------------------------------------------------------

    private List<SpringInfo> DetectSprings(PriceSeries s, List<AccumulationZone> zones)
    {
        var springs = new List<SpringInfo>();
        if (zones.Count == 0)
            return springs;

        for (int i = 0; i < zones.Count; i++)
        {
            var zone = zones[i];

            // A spring must occur after a completed, volume-qualified range. The old
            // implementation could derive support from any post-rally bars, even when no
            // accepted accumulation box existed at the spring date.
            int first = Math.Max(zone.End + 1, zone.Start + _o.MinAccumulationBars);
            // Continue evaluating through the end of the series. A spring can occur
            // after a later re-accumulation box, not only in the gap before the next box.
            int lastSpring = s.Count - 1;

            for (int bar = first; bar <= lastSpring; bar++)
            {
                int ws = Math.Max(zone.Start, bar - _o.SpringRangeLookbackBars);
                int we = bar - 1;
                if (we - ws + 1 < _o.MinAccumulationBars)
                    continue;

                double support = double.MaxValue, resistance = double.MinValue;
                for (int k = ws; k <= we; k++)
                {
                    support = Math.Min(support, s.Low[k]);
                    resistance = Math.Max(resistance, s.High[k]);
                }

                if (support <= 0 || resistance <= support ||
                    (resistance - support) / support > _o.SpringMaxRangePct)
                    continue;

                int supportTouches = 0, resistanceTouches = 0;
                for (int k = ws; k <= we; k++)
                {
                    if (s.Low[k] <= support * (1 + _o.TouchTolerancePct))
                        supportTouches++;
                    if (s.High[k] >= resistance * (1 - _o.TouchTolerancePct))
                        resistanceTouches++;
                }
                if (supportTouches < _o.MinSupportTouches ||
                    resistanceTouches < _o.MinResistanceTouches)
                    continue;

                // Require a genuine undercut, but reject a deep penetration that is more
                // consistent with a breakdown than a spring/upthrust.
                if (s.Low[bar] >= support * (1 - _o.MinSpringPiercePct))
                    continue;
                if (s.Low[bar] < support * (1 - _o.MaxSpringPiercePct))
                    continue;

                int recovery = -1;
                int lastCheck = Math.Min(s.Count - 1, bar + _o.SpringRecoveryBars);
                for (int k = bar; k <= lastCheck; k++)
                {
                    if (s.Close[k] > support)
                    {
                        recovery = k;
                        break;
                    }
                }
                if (recovery < 0)
                    continue;

                springs.Add(new SpringInfo(bar, recovery, support, resistance, s.Low[bar]));
            }
        }

        return springs;
    }

    // ------------------------------------------------------------------------------------------------
    // 5. Sign of strength: up-bar closing above resistance on expanded volume and range.
    // ------------------------------------------------------------------------------------------------

    private SosInfo? DetectSignOfStrength(PriceSeries s, SpringInfo spring)
    {
        SosInfo? latest = null;

        for (int j = spring.RecoveryIndex + 1; j < s.Count; j++)
        {
            double barRange = s.High[j] - s.Low[j];
            if (barRange <= 0)
                continue;

            if (s.Close[j] <= spring.Resistance * (1 + _o.BreakoutMarginPct))
                continue;
            if (s.Close[j] <= s.Open[j])
                continue;

            // A strong close near the top of the breakout bar helps reject long-upper-wick
            // probes that technically close above resistance but show weak demand.
            double closeLocation = (s.Close[j] - s.Low[j]) / barRange;
            if (closeLocation < 0.65)
                continue;

            int vs = Math.Max(0, j - 20);
            if (j - vs < 5)
                continue;

            double avgVol = PriceSeries.Mean(s.Volume, vs, j - 1);
            if (avgVol <= 0)
                continue;
            double volRatio = s.Volume[j] / avgVol;
            if (volRatio < _o.SosVolumeMultiple)
                continue;

            double avgRange = 0;
            for (int k = vs; k < j; k++)
                avgRange += s.High[k] - s.Low[k];
            avgRange /= (j - vs);

            if (barRange < avgRange)
                continue;

            // Keep scanning: a later breakout may be the currently valid event, while an
            // earlier breakout may have failed and been reclaimed.
            latest = new SosInfo(j, spring.Resistance, volRatio);
        }

        return latest;
    }
}
