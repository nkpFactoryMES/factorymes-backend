using FactoryMES.Backend.Features.Orders;
using FactoryMES.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Oracle.ManagedDataAccess.Client;

namespace FactoryMES.Backend.Features.Analysis;

// 업무 백엔드 AI 분석 경계. Oracle 스냅샷·작업 상태를 보존하고 게이트웨이 호출·예약·재시도를 조정한다.
public sealed partial class AnalysisService
{
    // Oracle 시간 집계와 경계 원본을 합쳐 대량 GOOD 시계열을 AI 입력 크기에 맞게 요약한다.
    private async Task<(AnalysisMetric[] Metrics, long Samples,
        long Buckets)> AggregateMetricsAsync(
            long equipment,
            DateTime start,
            DateTime end,
            bool includeObservation = false
        )
    {
        log.LogDebug(
            "{Operation}: {Decision}",
            "AggregateMetricsAsync",
            "입력 처리 시작"
        );
        // QueueCore owns the open connection and transaction. Only grouped rows leave Oracle.
        var connection = (OracleConnection)db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.BindByName = true;
        command.CommandTimeout = 30;
        command.Transaction = (OracleTransaction)db.Database.CurrentTransaction!.GetDbTransaction();
        command.Parameters.Add("equipmentId", OracleDbType.Int64).Value = equipment;
        command.Parameters.Add("startUtc", OracleDbType.TimeStamp).Value = start;
        command.Parameters.Add("endUtc", OracleDbType.TimeStamp).Value = end;
        command.CommandText = """
            WITH observations AS (
                SELECT e.TELEMETRY_ID, e.OCCURRED_AT_UTC,
                    m.METRIC_CODE, m.UNIT, m.NUMERIC_VALUE,
                    e.OCCURRED_AT_UTC - CAST(:startUtc AS TIMESTAMP) AS elapsed
                FROM TELEMETRY_EVENT e
                JOIN TELEMETRY_METRIC m ON m.TELEMETRY_ID = e.TELEMETRY_ID
                WHERE e.EQUIPMENT_ID = :equipmentId
                    AND e.OCCURRED_AT_UTC >= :startUtc
                    AND e.OCCURRED_AT_UTC < :endUtc
                    AND m.QUALITY = 'GOOD' AND m.NUMERIC_VALUE IS NOT NULL
            ), numbered AS (
                SELECT observations.*,
                    FLOOR(EXTRACT(DAY FROM elapsed) * 1440
                        + EXTRACT(HOUR FROM elapsed) * 60
                        + EXTRACT(MINUTE FROM elapsed)
                        + EXTRACT(SECOND FROM elapsed) / 60) AS bucket_no
                FROM observations
            ), minute_buckets AS (
                SELECT METRIC_CODE, UNIT, bucket_no,
                    COUNT(*) AS value_count,
                    SUM(NUMERIC_VALUE) AS value_sum,
                    MIN(NUMERIC_VALUE) AS minimum_value,
                    MAX(NUMERIC_VALUE) AS maximum_value,
                    MAX(OCCURRED_AT_UTC) AS last_at,
                    MAX(TELEMETRY_ID) KEEP (
                        DENSE_RANK LAST ORDER BY OCCURRED_AT_UTC, TELEMETRY_ID
                    ) AS last_id,
                    MAX(NUMERIC_VALUE) KEEP (
                        DENSE_RANK LAST ORDER BY OCCURRED_AT_UTC, TELEMETRY_ID
                    ) AS last_value
                FROM numbered
                GROUP BY METRIC_CODE, UNIT, bucket_no
            ), metric_totals AS (
                SELECT METRIC_CODE, UNIT,
                    MIN(minimum_value) AS minimum_value,
                    MAX(maximum_value) AS maximum_value,
                    CAST(ROUND(SUM(value_sum) / SUM(value_count), 6)
                        AS NUMBER(18,6)) AS mean_value,
                    MAX(last_value) KEEP (
                        DENSE_RANK LAST ORDER BY last_at, last_id
                    ) AS last_value,
                    SUM(value_count) AS metric_sample_count,
                    COUNT(*) AS metric_bucket_count,
                    MAX(last_at) AS metric_last_at
                FROM minute_buckets
                GROUP BY METRIC_CODE, UNIT
            ), totals AS (
                SELECT COUNT(DISTINCT TELEMETRY_ID) AS sample_count,
                    COUNT(DISTINCT bucket_no) AS bucket_count
                FROM numbered
            )
            SELECT m.METRIC_CODE, m.UNIT, m.minimum_value, m.maximum_value,
                m.mean_value, m.last_value, t.sample_count, t.bucket_count,
                m.metric_sample_count, m.metric_bucket_count, m.metric_last_at
            FROM metric_totals m CROSS JOIN totals t
            ORDER BY m.METRIC_CODE, m.UNIT NULLS FIRST
            FETCH FIRST 51 ROWS ONLY
            """;

        var metrics = new List<AnalysisMetric>();
        long samples = 0;
        long buckets = 0;
        var expectedBuckets = (decimal)Math.Ceiling((end - start).TotalMinutes);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            AnalysisMetricObservation? observation = null;
            if (includeObservation)
            {
                // 지표별 GOOD 표본·관측 분·마지막 시각을 Oracle 집계에서 읽는다.
                // 전체 카운터가 정상 수집돼도 온도만 오래된 경우를 구분할 근거다.
                var observedAt = reader.GetDateTime(10);
                observedAt = new DateTime(
                    observedAt.Ticks - observedAt.Ticks % 10,
                    DateTimeKind.Utc
                );
                observation = new AnalysisMetricObservation(
                    reader.GetInt64(8),
                    CollectionFormat.Number(Math.Round(
                        Math.Min(1, reader.GetInt64(9) / expectedBuckets),
                        6
                    )),
                    observedAt
                );
            }

            metrics.Add(new AnalysisMetric(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                CollectionFormat.Number(reader.GetDecimal(2)),
                CollectionFormat.Number(reader.GetDecimal(3)),
                CollectionFormat.Number(reader.GetDecimal(4)),
                CollectionFormat.Number(reader.GetDecimal(5)),
                observation
            ));
            samples = reader.GetInt64(6);
            buckets = reader.GetInt64(7);
        }

        if (metrics.Count > 50)
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "AggregateMetricsAsync",
                "분기 일치 [metrics.Count > 50]"
            );
            throw new OrderFault(
                422,
                "AnalysisMetricLimit",
                "분석 지표·단위 그룹은 50개까지입니다."
            );
        }
        else
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "AggregateMetricsAsync",
                "분기 불일치 [metrics.Count > 50]"
            );
        }

        Check(metrics.Select(x => x.MetricCode).Distinct().Count() == metrics.Count,
            "같은 측정 코드의 단위가 일치해야 합니다.");

        log.LogDebug(
            "Oracle AI 집계 완료: {EquipmentId}, {SampleCount}, {MetricCount}, {BucketCount}, "
                + "지표별 관측 근거 포함 {IncludeObservation}",
            equipment,
            samples,
            metrics.Count,
            buckets,
            includeObservation
        );
        return (metrics.ToArray(), samples, buckets);
    }
}





