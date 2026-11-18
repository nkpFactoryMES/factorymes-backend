namespace FactoryMES.Backend.Features.Alarms;

public readonly record struct HighReading(
    DateTime SampledAt,
    DateTime ReceivedAt,
    decimal? Value,
    string Quality
);

// Oracle에서 최신 측정 시각 내림차순으로 받은 이력을 순수 함수로 판정한다.
// 현재 시각 경과만으로 복구하지 않고 GOOD 품질·임계값·연속 간격·유지시간을 모두 만족해야 한다.
public static class HighThresholdEvidence
{
    public static bool Supports(
        IReadOnlyList<HighReading> readings,
        decimal threshold,
        bool recovering,
        long holdSec,
        long staleSec,
        DateTime now
    )
    {
        if (readings.Count == 0) return false;
        var latest = readings[0];
        var cutoff = now.AddSeconds(-staleSec);
        if (latest.SampledAt < cutoff || latest.ReceivedAt < cutoff
            || latest.SampledAt > now.AddSeconds(2)) return false;
        var previous = latest.SampledAt;
        foreach (var reading in readings)
        {
            if (reading.Quality != "GOOD" || reading.Value is null
                || (recovering ? reading.Value > threshold : reading.Value < threshold)
                || reading.SampledAt > previous
                || (previous - reading.SampledAt).TotalSeconds > staleSec) return false;
            if ((latest.SampledAt - reading.SampledAt).TotalSeconds >= holdSec) return true;
            previous = reading.SampledAt;
        }
        return false;
    }
}


