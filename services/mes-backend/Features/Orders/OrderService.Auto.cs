using System.Security.Claims;
using FactoryMES.Backend.Persistence.Entities;
using FactoryMES.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FactoryMES.Backend.Features.Orders;

public sealed partial class OrderService
{
    // 관리자가 선택할 수 있는 COUNTER만 제공한다. 최종 매핑·설비 검사는 저장할 때 다시 한다.
    public Task<AutoCounterOption[]> CounterOptionsAsync(string equipmentId) =>
        auto.OptionsAsync(ParseId(equipmentId));

    // 리셋 후 누락 구간을 추정하지 않는다. 담당자/관리자가 사유를 남기고 다음 정상 샘플을 새 기준으로 요청한다.
    public async Task<OrderView> RebaseCounterAsync(
        string id,
        ReasonCommand input,
        ClaimsPrincipal user
    )
    {
        // 이상 카운터의 확인 요청을 받아 추정 실적을 만들지 않고 다음 정상 샘플을 새 기준으로 기다린다.
        log.LogDebug("Order RebaseCounterAsync entered by actor {ActorId}", Actor(user));

        Require(!string.IsNullOrWhiteSpace(input.Reason) && input.Reason.Trim().Length <= 1000,
            "카운터 재기준 사유를 1~1000자로 입력하세요.");
        await using var tx = await db.Database.BeginTransactionAsync();
        var order = await LoadAsync(
            ParseId(id),
            user,
            true
        );
        Version(order, input.ExpectedVersion);
        State(
            order,
            "RUNNING",
            "PAUSED"
        );
        Require(order.CountMode == "AUTO" && order.CounterInvalid, "이상이 확인된 AUTO 작업만 재기준할 수 있습니다.");
        await auto.ArmAsync(order, LifecycleTime(order));
        order.CounterInvalid = false;
        db.Entry(order).Property(x => x.UpdatedAtUtc).IsModified = true;
        Audit(
            order,
            user,
            "order.counter.rebase",
            input.Reason.Trim()
        );
        await db.SaveChangesAsync();
        var result = await ViewAsync(order);
        await tx.CommitAsync();
        log.LogInformation(
            "AUTO counter recovery confirmed Actor={ActorId} Order={OrderId}; awaiting new baseline",
            Actor(user),
            order.OrderId
        );
        return result;
    }
}

