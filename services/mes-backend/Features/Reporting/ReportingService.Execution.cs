using FactoryMES.Backend.Persistence.Entities;
using FactoryMES.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FactoryMES.Backend.Features.Reporting;

// 마감 생산실적 원장을 영업일·설비별 통계로 변환한다. 원본 작업·실적은 수정하지 않는다.
public sealed partial class ReportingService
{
    // 내부 집계 요청을 받아 같은 영업일 실행을 직렬화하고 저장된 실적 원장으로 통계를 재구축한다.
    public async Task<JobRunView> RebuildAsync(ReportingRebuildRequest input)
    {
        try
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "RebuildAsync",
                "입력 처리 시작"
            );
            var day = BusinessDay(input.BusinessDate);
            await coordinator.Gate.WaitAsync();
            try
            {
                JobRun run;
                await using (var tx = await db.Database.BeginTransactionAsync())
                {
                    await LockRunsAsync();
                    run = await NewRunAsync(day);
                    await tx.CommitAsync();
                }

                return await ExecuteAsync(run.JobRunId);
            }
            finally
            {
                coordinator.Gate.Release();
            }
        }
        catch (Exception processingError)
        {
            if (processingError is OperationCanceledException)
            {
                log.LogDebug("요청 처리가 취소됨: {Operation}", "RebuildAsync");
            }
            else
            {
                log.LogWarning(
                    "요청 처리 거절 또는 실패: {Operation}, {FailureCode}",
                    "RebuildAsync",
                    processingError is FactoryMES.Backend.Features.Orders.OrderFault fault
                        ? fault.Code : processingError.GetType().Name
                );
            }
            throw;
        }
    }

    // 대기 중 집계 작업을 정해진 개수만 실행하고 성공·실패 개수를 반환한다.
    public async Task<ReportingDispatchResult> PendingAsync(DispatchBatchRequest input)
    {
        try
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "PendingAsync",
                "입력 처리 시작"
            );
            var size = Size(input.Limit);
            await coordinator.Gate.WaitAsync();
            try
            {
                var ids = await db.Set<JobRun>()
                    .AsNoTracking()
                    .Where(x => x.Status == "RUNNING" && x.JobKey.StartsWith(Prefix))
                    .OrderBy(x => x.JobRunId)
                    .Select(x => x.JobRunId)
                    .Take(size)
                    .ToArrayAsync();
                var completed = 0;
                var failed = 0;
                foreach (var id in ids)
                {
                    var result = await ExecuteAsync(id);
                    if (result.Status == "SUCCEEDED")
                    {
                        log.LogDebug(
                            "{Operation}: {Decision}",
                            "PendingAsync",
                            "분기 일치 [result.Status == \"SUCCEEDED\"]"
                        );
                        completed++;
                    }
                    else
                    {
                        log.LogDebug(
                            "{Operation}: {Decision}",
                            "PendingAsync",
                            "분기 불일치 [result.Status == \"SUCCEEDED\"]"
                        );
                        if (result.Status == "FAILED")
                        {
                            log.LogDebug(
                                "{Operation}: {Decision}",
                                "PendingAsync",
                                "분기 일치 [result.Status == \"FAILED\"]"
                            );
                            failed++;
                        }
                        else
                        {
                            log.LogDebug(
                                "{Operation}: {Decision}",
                                "PendingAsync",
                                "분기 불일치 [result.Status == \"FAILED\"]"
                            );
                        }
                    }

                    db.ChangeTracker.Clear();
                }

                return new ReportingDispatchResult(
                    ids.Length,
                    completed,
                    failed
                );
            }
            finally
            {
                coordinator.Gate.Release();
            }
        }
        catch (Exception processingError)
        {
            if (processingError is OperationCanceledException)
            {
                log.LogDebug("요청 처리가 취소됨: {Operation}", "PendingAsync");
            }
            else
            {
                log.LogWarning(
                    "요청 처리 거절 또는 실패: {Operation}, {FailureCode}",
                    "PendingAsync",
                    processingError is FactoryMES.Backend.Features.Orders.OrderFault fault
                        ? fault.Code : processingError.GetType().Name
                );
            }
            throw;
        }
    }

    // KST 마감일의 CLOSED 작업 원장에서 MANUAL·AUTO 순실적을 재계산한다. 실패 시 기존 집계를 보존한다.
    private async Task<JobRunView> ExecuteAsync(long id)
    {
        log.LogDebug(
            "{Operation}: {Decision}",
            "ExecuteAsync",
            "입력 처리 시작"
        );
        await using var tx = await db.Database.BeginTransactionAsync();
        try
        {
            // All reporting paths acquire the same table locks in this order.
            await LockRunsAsync();
            var run = await db.Set<JobRun>().SingleAsync(x => x.JobRunId == id);
            await db.Entry(run).ReloadAsync();
            if (run.Status != "RUNNING")
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "ExecuteAsync",
                    "분기 일치 [run.Status != \"RUNNING\"]"
                );
                return View(run);
            }
            else
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "ExecuteAsync",
                    "분기 불일치 [run.Status != \"RUNNING\"]"
                );
            }

            if (CollectionFormat.UtcNow() < run.StartedAtUtc)
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "ExecuteAsync",
                    "분기 일치 [CollectionFormat.UtcNow() < run.StartedAtUtc]"
                );
                throw new InvalidOperationException("REPORTING_CLOCK_REGRESSION");
            }
            else
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "ExecuteAsync",
                    "분기 불일치 [CollectionFormat.UtcNow() < run.StartedAtUtc]"
                );
            }

            var day = BusinessDay(run.JobKey[Prefix.Length..]);
            var date = DateKey(day);
            var start = DateTime.SpecifyKind(date.AddHours(-9), DateTimeKind.Utc);
            var end = start.AddDays(1);
            // AUTO도 TOTAL 증분·REJECT·REVERSAL 원장으로 기록하므로 같은 순합계 규칙을 쓴다.
            // 카운터 원시 시작/종료값 차이를 다시 합산하면 리셋/중지 구간을 중복 계산하게 된다.
            await db.Database.ExecuteSqlRawAsync("LOCK TABLE DAILY_PRODUCTION IN EXCLUSIVE MODE");
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM DAILY_PRODUCTION WHERE BUSINESS_DATE = {date}"
            );
            var now = CollectionFormat.UtcNow();
            var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO DAILY_PRODUCTION
                    (BUSINESS_DATE, EQUIPMENT_ID, PRODUCT_ID, TOTAL_QTY, REJECT_QTY,
                     REFRESHED_AT_UTC, CREATED_AT_UTC)
                SELECT {date}, w.EQUIPMENT_ID, w.PRODUCT_ID,
                    NVL(SUM(CASE WHEN NVL(o.KIND, e.KIND) <> 'REJECT' THEN
                        CASE WHEN e.KIND IN ('REVERSAL', 'TOTAL_ADJUST_DOWN')
                             THEN -e.QTY ELSE e.QTY END ELSE 0 END), 0),
                    NVL(SUM(CASE WHEN NVL(o.KIND, e.KIND) = 'REJECT' THEN
                        CASE WHEN e.KIND = 'REVERSAL' THEN -e.QTY ELSE e.QTY END ELSE 0 END), 0),
                    {now}, {now}
                FROM WORK_ORDER w
                LEFT JOIN PRODUCTION_ENTRY e ON e.ORDER_ID = w.ORDER_ID
                LEFT JOIN PRODUCTION_ENTRY o ON o.ENTRY_ID = e.REVERSAL_OF
                    AND o.ORDER_ID = e.ORDER_ID
                WHERE w.STATUS = 'CLOSED' AND w.COUNT_MODE IN ('MANUAL','AUTO')
                    AND w.CLOSED_AT_UTC >= {start} AND w.CLOSED_AT_UTC < {end}
                GROUP BY w.EQUIPMENT_ID, w.PRODUCT_ID
                """);
            var ended = CollectionFormat.UtcNow();
            if (ended < run.StartedAtUtc)
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "ExecuteAsync",
                    "분기 일치 [ended < run.StartedAtUtc]"
                );
                throw new InvalidOperationException("REPORTING_CLOCK_REGRESSION");
            }
            else
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "ExecuteAsync",
                    "분기 불일치 [ended < run.StartedAtUtc]"
                );
            }

            run.Status = "SUCCEEDED";
            run.EndedAtUtc = ended;
            run.AffectedRows = affected;
            run.Error = null;
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            log.LogInformation(
                "생산 통계 재계산 완료: {JobRunId}, {BusinessDate}, {AffectedRows}",
                run.JobRunId,
                day,
                affected
            );
            return View(run);
        }
        catch (Exception error)
        {
            log.LogWarning(
                "생산 통계 재계산 실패: {JobRunId}, {FailureType}",
                id,
                error.GetType().Name
            );
            await tx.RollbackAsync();
            await tx.DisposeAsync();
            db.ChangeTracker.Clear();
            await using var failure = await db.Database.BeginTransactionAsync();
            await LockRunsAsync();
            var run = await db.Set<JobRun>().SingleAsync(x => x.JobRunId == id);
            if (run.Status == "RUNNING")
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "ExecuteAsync",
                    "분기 일치 [run.Status == \"RUNNING\"]"
                );
                run.Status = "FAILED";
                var ended = CollectionFormat.UtcNow();
                var regressed = ended < run.StartedAtUtc;
                run.EndedAtUtc = regressed ? run.StartedAtUtc : ended;
                run.Error = regressed ? "REPORTING_CLOCK_REGRESSION"
                    : error is InvalidOperationException
                        && error.Message is "REPORTING_AUTO_NOT_SUPPORTED" or "REPORTING_CLOCK_REGRESSION"
                            ? error.Message : "REPORTING_REBUILD_FAILED";
                await db.SaveChangesAsync();
            }
            else
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "ExecuteAsync",
                    "분기 불일치 [run.Status == \"RUNNING\"]"
                );
            }

            await failure.CommitAsync();
            return View(run);
        }
    }
}




