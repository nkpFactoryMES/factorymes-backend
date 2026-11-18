using Microsoft.EntityFrameworkCore;
using FactoryMES.Backend.Persistence.Entities;

namespace FactoryMES.Backend.Persistence;

// 업무 엔티티를 Oracle 테이블·키·제약에 매핑하고 UTC/감사시각/낙관적 버전을 일관되게 적용한다.
public sealed partial class MesDbContext(DbContextOptions<MesDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder model)
    {
        ConfigureAutoProduction(model);
        ConfigureNotificationSettings(model);
        // 내부 로그인 계정: 인증 서비스가 비밀번호 해시·활성 여부를 읽고 Cookie 주체를 만든다.
        model.Entity<UserAccount>(e =>
        {
            e.ToTable("USER_ACCOUNT");
            e.HasKey("UserId");
            e.HasIndex("NormalizedLogin")
                .IsUnique()
                .HasDatabaseName("UQ_USR_LOGIN");
            e.Property(x => x.UserId)
                .HasColumnName("USER_ID")
                .HasColumnType("NUMBER(19,0)")
                .IsRequired()
                .ValueGeneratedOnAdd();
            e.Property(x => x.Login)
                .HasColumnName("LOGIN")
                .HasColumnType("VARCHAR2(100 CHAR)")
                .IsRequired();
            e.Property(x => x.NormalizedLogin)
                .HasColumnName("NORMALIZED_LOGIN")
                .HasColumnType("VARCHAR2(100 CHAR)")
                .IsRequired();
            e.Property(x => x.Name)
                .HasColumnName("NAME")
                .HasColumnType("VARCHAR2(100 CHAR)")
                .IsRequired();
            e.Property(x => x.PasswordHash)
                .HasColumnName("PASSWORD_HASH")
                .HasColumnType("VARCHAR2(1000 CHAR)")
                .IsRequired();
            e.Property(x => x.Active)
                .HasColumnName("ACTIVE")
                .HasColumnType("NUMBER(1,0)")
                .IsRequired()
                .HasConversion<int>();
            e.Property(x => x.Email)
                .HasColumnName("EMAIL")
                .HasColumnType("VARCHAR2(254 CHAR)");
            e.Property(x => x.FailedCount)
                .HasColumnName("FAILED_COUNT")
                .HasColumnType("NUMBER(12,0)")
                .IsRequired();
            e.Property(x => x.LockUntilUtc)
                .HasColumnName("LOCK_UNTIL_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .HasConversion(UtcDateTime.Converter);
            e.Property(x => x.SecurityStamp)
                .HasColumnName("SECURITY_STAMP")
                .HasColumnType("VARCHAR2(64 CHAR)")
                .IsRequired();
            e.Property(x => x.CreatedAtUtc)
                .HasColumnName("CREATED_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .IsRequired()
                .HasConversion(UtcDateTime.Converter);
            e.Property(x => x.Version)
                .HasColumnName("VERSION")
                .HasColumnType("NUMBER(10,0)")
                .IsRequired()
                .IsConcurrencyToken();
            e.Property(x => x.UpdatedAtUtc)
                .HasColumnName("UPDATED_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .IsRequired()
                .HasConversion(UtcDateTime.Converter);
        });
        // 사용자별 ADMIN/WORKER 역할: 인증 이후 업무 API의 권한과 조회 범위를 결정한다.
        model.Entity<UserRole>(e =>
        {
            e.ToTable("USER_ROLE");
            e.HasKey("UserId", "Role");
            e.Property(x => x.UserId)
                .HasColumnName("USER_ID")
                .HasColumnType("NUMBER(19,0)")
                .IsRequired()
                .ValueGeneratedNever();
            e.Property(x => x.Role)
                .HasColumnName("ROLE")
                .HasColumnType("VARCHAR2(20 CHAR)")
                .IsRequired()
                .ValueGeneratedNever();
            e.Property(x => x.CreatedAtUtc)
                .HasColumnName("CREATED_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .IsRequired()
                .HasConversion(UtcDateTime.Converter);
        });
        // 품목 기준정보: 관리자가 등록하며 작업지시와 일별 생산 집계가 참조한다.
        model.Entity<Product>(e =>
        {
            e.ToTable("PRODUCT");
            e.HasKey("ProductId");
            e.HasIndex("Code")
                .IsUnique()
                .HasDatabaseName("UQ_PROD_CODE");
            e.Property(x => x.ProductId)
                .HasColumnName("PRODUCT_ID")
                .HasColumnType("NUMBER(19,0)")
                .IsRequired()
                .ValueGeneratedOnAdd();
            e.Property(x => x.Code)
                .HasColumnName("CODE")
                .HasColumnType("VARCHAR2(40 CHAR)")
                .IsRequired();
            e.Property(x => x.Name)
                .HasColumnName("NAME")
                .HasColumnType("VARCHAR2(100 CHAR)")
                .IsRequired();
            e.Property(x => x.Unit)
                .HasColumnName("UNIT")
                .HasColumnType("VARCHAR2(10 CHAR)")
                .IsRequired();
            e.Property(x => x.Active)
                .HasColumnName("ACTIVE")
                .HasColumnType("NUMBER(1,0)")
                .IsRequired()
                .HasConversion<int>();
            e.Property(x => x.CreatedAtUtc)
                .HasColumnName("CREATED_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .IsRequired()
                .HasConversion(UtcDateTime.Converter);
            e.Property(x => x.Version)
                .HasColumnName("VERSION")
                .HasColumnType("NUMBER(10,0)")
                .IsRequired()
                .IsConcurrencyToken();
            e.Property(x => x.UpdatedAtUtc)
                .HasColumnName("UPDATED_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .IsRequired()
                .HasConversion(UtcDateTime.Converter);
        });
        // 생산 라인 기준정보: 설비를 묶고 라인별 실적·경보 조회 범위를 제공한다.
        model.Entity<Line>(e =>
        {
            e.ToTable("LINE");
            e.HasKey("LineId");
            e.HasIndex("Code")
                .IsUnique()
                .HasDatabaseName("UQ_LINE_CODE");
            e.Property(x => x.LineId)
                .HasColumnName("LINE_ID")
                .HasColumnType("NUMBER(19,0)")
                .IsRequired()
                .ValueGeneratedOnAdd();
            e.Property(x => x.Code)
                .HasColumnName("CODE")
                .HasColumnType("VARCHAR2(40 CHAR)")
                .IsRequired();
            e.Property(x => x.Name)
                .HasColumnName("NAME")
                .HasColumnType("VARCHAR2(100 CHAR)")
                .IsRequired();
            e.Property(x => x.Active)
                .HasColumnName("ACTIVE")
                .HasColumnType("NUMBER(1,0)")
                .IsRequired()
                .HasConversion<int>();
            e.Property(x => x.CreatedAtUtc)
                .HasColumnName("CREATED_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .IsRequired()
                .HasConversion(UtcDateTime.Converter);
            e.Property(x => x.Version)
                .HasColumnName("VERSION")
                .HasColumnType("NUMBER(10,0)")
                .IsRequired()
                .IsConcurrencyToken();
            e.Property(x => x.UpdatedAtUtc)
                .HasColumnName("UPDATED_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .IsRequired()
                .HasConversion(UtcDateTime.Converter);
        });
        // 설비 기준정보: 작업지시와 수집 대상의 공통 식별자이며 작업 전환 잠금의 기준이다.
        model.Entity<Equipment>(e =>
        {
            e.ToTable("EQUIPMENT");
            e.HasKey("EquipmentId");
            e.HasIndex("Code")
                .IsUnique()
                .HasDatabaseName("UQ_EQ_CODE");
            e.Property(x => x.EquipmentId)
                .HasColumnName("EQUIPMENT_ID")
                .HasColumnType("NUMBER(19,0)")
                .IsRequired()
                .ValueGeneratedOnAdd();
            e.Property(x => x.LineId)
                .HasColumnName("LINE_ID")
                .HasColumnType("NUMBER(19,0)")
                .IsRequired();
            e.Property(x => x.Code)
                .HasColumnName("CODE")
                .HasColumnType("VARCHAR2(40 CHAR)")
                .IsRequired();
            e.Property(x => x.Name)
                .HasColumnName("NAME")
                .HasColumnType("VARCHAR2(100 CHAR)")
                .IsRequired();
            e.Property(x => x.Active)
                .HasColumnName("ACTIVE")
                .HasColumnType("NUMBER(1,0)")
                .IsRequired()
                .HasConversion<int>();
            e.Property(x => x.CreatedAtUtc)
                .HasColumnName("CREATED_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .IsRequired()
                .HasConversion(UtcDateTime.Converter);
            e.Property(x => x.Version)
                .HasColumnName("VERSION")
                .HasColumnType("NUMBER(10,0)")
                .IsRequired()
                .IsConcurrencyToken();
            e.Property(x => x.UpdatedAtUtc)
                .HasColumnName("UPDATED_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .IsRequired()
                .HasConversion(UtcDateTime.Converter);
        });
        // 작업지시: 관리자가 발행하고 담당 작업자가 수행한다. AUTO 수량은 수집 원장에서 계산한다.
        model.Entity<WorkOrder>(e =>
        {
            e.ToTable("WORK_ORDER");
            e.HasKey("OrderId");
            e.HasIndex("OrderNo")
                .IsUnique()
                .HasDatabaseName("UQ_WO_NO");
            e.HasAlternateKey("OrderId", "EquipmentId")
                .HasName("UQ_WO_ORDER_EQ");
            e.Property(x => x.OrderId)
                .HasColumnName("ORDER_ID")
                .HasColumnType("NUMBER(19,0)")
                .IsRequired()
                .ValueGeneratedOnAdd();
            e.Property(x => x.OrderNo)
                .HasColumnName("ORDER_NO")
                .HasColumnType("VARCHAR2(40 CHAR)")
                .IsRequired();
            e.Property(x => x.ProductId)
                .HasColumnName("PRODUCT_ID")
                .HasColumnType("NUMBER(19,0)")
                .IsRequired();
            e.Property(x => x.EquipmentId)
                .HasColumnName("EQUIPMENT_ID")
                .HasColumnType("NUMBER(19,0)")
                .IsRequired();
            e.Property(x => x.AssigneeId)
                .HasColumnName("ASSIGNEE_ID")
                .HasColumnType("NUMBER(19,0)")
                .IsRequired();
            e.Property(x => x.OperationLabel)
                .HasColumnName("OPERATION_LABEL")
                .HasColumnType("VARCHAR2(100 CHAR)");
            e.Property(x => x.TargetQty)
                .HasColumnName("TARGET_QTY")
                .HasColumnType("NUMBER(12,0)")
                .IsRequired();
            e.Property(x => x.CountMode)
                .HasColumnName("COUNT_MODE")
                .HasColumnType("VARCHAR2(10 CHAR)")
                .IsRequired();
            e.Property(x => x.CounterMappingId)
                .HasColumnName("COUNTER_MAPPING_ID")
                .HasColumnType("NUMBER(19,0)");
            e.Property(x => x.PlannedStartUtc)
                .HasColumnName("PLANNED_START_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .IsRequired()
                .HasConversion(UtcDateTime.Converter);
            e.Property(x => x.PlannedEndUtc)
                .HasColumnName("PLANNED_END_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .IsRequired()
                .HasConversion(UtcDateTime.Converter);
            e.Property(x => x.Status)
                .HasColumnName("STATUS")
                .HasColumnType("VARCHAR2(20 CHAR)")
                .IsRequired();
            e.Property(x => x.CreatedBy)
                .HasColumnName("CREATED_BY")
                .HasColumnType("NUMBER(19,0)")
                .IsRequired();
            e.Property(x => x.StartedAtUtc)
                .HasColumnName("STARTED_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .HasConversion(UtcDateTime.Converter);
            e.Property(x => x.SubmittedAtUtc)
                .HasColumnName("SUBMITTED_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .HasConversion(UtcDateTime.Converter);
            e.Property(x => x.ClosedAtUtc)
                .HasColumnName("CLOSED_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .HasConversion(UtcDateTime.Converter);
            e.Property(x => x.CancelledAtUtc)
                .HasColumnName("CANCELLED_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .HasConversion(UtcDateTime.Converter);
            e.Property(x => x.CancelReason)
                .HasColumnName("CANCEL_REASON")
                .HasColumnType("VARCHAR2(1000 CHAR)");
            e.Property(x => x.CompletionReason)
                .HasColumnName("COMPLETION_REASON")
                .HasColumnType("VARCHAR2(1000 CHAR)");
            e.Property(x => x.StartCounter)
                .HasColumnName("START_COUNTER")
                .HasColumnType("NUMBER(18,6)");
            e.Property(x => x.EndCounter)
                .HasColumnName("END_COUNTER")
                .HasColumnType("NUMBER(18,6)");
            e.Property(x => x.StartSampleAtUtc)
                .HasColumnName("START_SAMPLE_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .HasConversion(UtcDateTime.Converter);
            e.Property(x => x.EndSampleAtUtc)
                .HasColumnName("END_SAMPLE_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .HasConversion(UtcDateTime.Converter);
            e.Property(x => x.StartReceivedAtUtc)
                .HasColumnName("START_RECEIVED_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .HasConversion(UtcDateTime.Converter);
            e.Property(x => x.EndReceivedAtUtc)
                .HasColumnName("END_RECEIVED_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .HasConversion(UtcDateTime.Converter);
            e.Property(x => x.CounterInvalid)
                .HasColumnName("COUNTER_INVALID")
                .HasColumnType("NUMBER(1,0)")
                .IsRequired()
                .HasConversion<int>();
            e.Property(x => x.CreatedAtUtc)
                .HasColumnName("CREATED_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .IsRequired()
                .HasConversion(UtcDateTime.Converter);
            e.Property(x => x.Version)
                .HasColumnName("VERSION")
                .HasColumnType("NUMBER(10,0)")
                .IsRequired()
                .IsConcurrencyToken();
            e.Property(x => x.UpdatedAtUtc)
                .HasColumnName("UPDATED_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .IsRequired()
                .HasConversion(UtcDateTime.Converter);
        });
        // 작업 전 점검: 담당 작업자가 체크한 안전·준비 항목을 시작 조건으로 사용한다.
        model.Entity<WorkCheckItem>(e =>
        {
            e.ToTable("WORK_CHECK_ITEM");
            e.HasKey("CheckId");
            e.HasIndex("OrderId", "Seq")
                .IsUnique()
                .HasDatabaseName("UQ_CHK_SEQ");
            e.Property(x => x.CheckId)
                .HasColumnName("CHECK_ID")
                .HasColumnType("NUMBER(19,0)")
                .IsRequired()
                .ValueGeneratedOnAdd();
            e.Property(x => x.OrderId)
                .HasColumnName("ORDER_ID")
                .HasColumnType("NUMBER(19,0)")
                .IsRequired();
            e.Property(x => x.Seq)
                .HasColumnName("SEQ")
                .HasColumnType("NUMBER(12,0)")
                .IsRequired();
            e.Property(x => x.Label)
                .HasColumnName("LABEL")
                .HasColumnType("VARCHAR2(200 CHAR)")
                .IsRequired();
            e.Property(x => x.Required)
                .HasColumnName("REQUIRED")
                .HasColumnType("NUMBER(1,0)")
                .IsRequired()
                .HasConversion<int>();
            e.Property(x => x.Checked)
                .HasColumnName("CHECKED")
                .HasColumnType("NUMBER(1,0)")
                .IsRequired()
                .HasConversion<int>();
            e.Property(x => x.CheckedBy)
                .HasColumnName("CHECKED_BY")
                .HasColumnType("NUMBER(19,0)");
            e.Property(x => x.CheckedAtUtc)
                .HasColumnName("CHECKED_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .HasConversion(UtcDateTime.Converter);
            e.Property(x => x.CommentText)
                .HasColumnName("COMMENT_TEXT")
                .HasColumnType("VARCHAR2(1000 CHAR)");
            e.Property(x => x.CreatedAtUtc)
                .HasColumnName("CREATED_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .IsRequired()
                .HasConversion(UtcDateTime.Converter);
        });
        // 작업 중지 구간: 중지·재개 명령과 사유를 보존하며 AUTO 중지 구간의 수량은 제외한다.
        model.Entity<WorkPause>(e =>
        {
            e.ToTable("WORK_PAUSE");
            e.HasKey("PauseId");
            e.Property(x => x.PauseId)
                .HasColumnName("PAUSE_ID")
                .HasColumnType("NUMBER(19,0)")
                .IsRequired()
                .ValueGeneratedOnAdd();
            e.Property(x => x.OrderId)
                .HasColumnName("ORDER_ID")
                .HasColumnType("NUMBER(19,0)")
                .IsRequired();
            e.Property(x => x.AlarmId)
                .HasColumnName("ALARM_ID")
                .HasColumnType("NUMBER(19,0)");
            e.Property(x => x.ReasonCode)
                .HasColumnName("REASON_CODE")
                .HasColumnType("VARCHAR2(40 CHAR)")
                .IsRequired();
            e.Property(x => x.Note)
                .HasColumnName("NOTE")
                .HasColumnType("VARCHAR2(1000 CHAR)");
            e.Property(x => x.StartedAtUtc)
                .HasColumnName("STARTED_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .IsRequired()
                .HasConversion(UtcDateTime.Converter);
            e.Property(x => x.EndedAtUtc)
                .HasColumnName("ENDED_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .HasConversion(UtcDateTime.Converter);
            e.Property(x => x.RecordedBy)
                .HasColumnName("RECORDED_BY")
                .HasColumnType("NUMBER(19,0)")
                .IsRequired();
            e.Property(x => x.CreatedAtUtc)
                .HasColumnName("CREATED_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .IsRequired()
                .HasConversion(UtcDateTime.Converter);
            e.Property(x => x.EquipmentId)
                .HasColumnName("EQUIPMENT_ID")
                .HasColumnType("NUMBER(19,0)")
                .IsRequired();
        });
        // 생산 수량 원장: 수동 입력·자동 증분·역분개를 보존하고 TOTAL/REJECT 순합으로 집계한다.
        model.Entity<ProductionEntry>(e =>
        {
            e.ToTable("PRODUCTION_ENTRY");
            e.HasKey("EntryId");
            e.HasIndex("ClientEntryId")
                .IsUnique()
                .HasDatabaseName("UQ_ENTRY_CLIENT");
            e.HasIndex("ReversalOf")
                .IsUnique()
                .HasDatabaseName("UQ_ENTRY_REV");
            e.HasAlternateKey("EntryId", "OrderId")
                .HasName("UQ_ENTRY_ID_ORDER");
            e.Property(x => x.EntryId)
                .HasColumnName("ENTRY_ID")
                .HasColumnType("NUMBER(19,0)")
                .IsRequired()
                .ValueGeneratedOnAdd();
            e.Property(x => x.ClientEntryId)
                .HasColumnName("CLIENT_ENTRY_ID")
                .HasColumnType("VARCHAR2(36 CHAR)")
                .IsRequired();
            e.Property(x => x.OrderId)
                .HasColumnName("ORDER_ID")
                .HasColumnType("NUMBER(19,0)")
                .IsRequired();
            e.Property(x => x.Kind)
                .HasColumnName("KIND")
                .HasColumnType("VARCHAR2(20 CHAR)")
                .IsRequired();
            e.Property(x => x.Qty)
                .HasColumnName("QTY")
                .HasColumnType("NUMBER(12,0)")
                .IsRequired();
            e.Property(x => x.ReversalOf)
                .HasColumnName("REVERSAL_OF")
                .HasColumnType("NUMBER(19,0)");
            e.Property(x => x.CorrectionId)
                .HasColumnName("CORRECTION_ID")
                .HasColumnType("NUMBER(19,0)");
            e.Property(x => x.Reason)
                .HasColumnName("REASON")
                .HasColumnType("VARCHAR2(1000 CHAR)");
            e.Property(x => x.RecordedBy)
                .HasColumnName("RECORDED_BY")
                .HasColumnType("NUMBER(19,0)")
                .IsRequired();
            e.Property(x => x.OccurredAtUtc)
                .HasColumnName("OCCURRED_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .IsRequired()
                .HasConversion(UtcDateTime.Converter);
            e.Property(x => x.CreatedAtUtc)
                .HasColumnName("CREATED_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .IsRequired()
                .HasConversion(UtcDateTime.Converter);
            e.Property(x => x.RequestHash)
                .HasColumnName("REQUEST_HASH")
                .HasColumnType("VARCHAR2(64 CHAR)")
                .IsRequired();
        });
        // 관리자 정정 요청: 제출/마감 전 원장을 지우지 않고 허용된 정정 절차를 추적한다.
        model.Entity<CorrectionRequest>(e =>
        {
            e.ToTable("CORRECTION_REQUEST");
            e.HasKey("CorrectionId");
            e.HasAlternateKey("CorrectionId", "OrderId")
                .HasName("UQ_CORR_ID_ORDER");
            e.Property(x => x.CorrectionId)
                .HasColumnName("CORRECTION_ID")
                .HasColumnType("NUMBER(19,0)")
                .IsRequired()
                .ValueGeneratedOnAdd();
            e.Property(x => x.OrderId)
                .HasColumnName("ORDER_ID")
                .HasColumnType("NUMBER(19,0)")
                .IsRequired();
            e.Property(x => x.Status)
                .HasColumnName("STATUS")
                .HasColumnType("VARCHAR2(10 CHAR)")
                .IsRequired();
            e.Property(x => x.Reason)
                .HasColumnName("REASON")
                .HasColumnType("VARCHAR2(1000 CHAR)")
                .IsRequired();
            e.Property(x => x.RequestedBy)
                .HasColumnName("REQUESTED_BY")
                .HasColumnType("NUMBER(19,0)")
                .IsRequired();
            e.Property(x => x.ResolvedBy)
                .HasColumnName("RESOLVED_BY")
                .HasColumnType("NUMBER(19,0)");
            e.Property(x => x.ResolvedAtUtc)
                .HasColumnName("RESOLVED_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .HasConversion(UtcDateTime.Converter);
            e.Property(x => x.ResolutionNote)
                .HasColumnName("RESOLUTION_NOTE")
                .HasColumnType("VARCHAR2(1000 CHAR)");
            e.Property(x => x.CreatedAtUtc)
                .HasColumnName("CREATED_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .IsRequired()
                .HasConversion(UtcDateTime.Converter);
            e.Property(x => x.Version)
                .HasColumnName("VERSION")
                .HasColumnType("NUMBER(10,0)")
                .IsRequired()
                .IsConcurrencyToken();
            e.Property(x => x.UpdatedAtUtc)
                .HasColumnName("UPDATED_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .IsRequired()
                .HasConversion(UtcDateTime.Converter);
        });
        // 작업 상태 이력: 업무 명령의 이전·이후 상태와 수행자를 감사 목적으로 보존한다.
        model.Entity<OrderTransition>(e =>
        {
            e.ToTable("ORDER_TRANSITION");
            e.HasKey("TransitionId");
            e.Property(x => x.TransitionId)
                .HasColumnName("TRANSITION_ID")
                .HasColumnType("NUMBER(19,0)")
                .IsRequired()
                .ValueGeneratedOnAdd();
            e.Property(x => x.OrderId)
                .HasColumnName("ORDER_ID")
                .HasColumnType("NUMBER(19,0)")
                .IsRequired();
            e.Property(x => x.FromStatus)
                .HasColumnName("FROM_STATUS")
                .HasColumnType("VARCHAR2(20 CHAR)")
                .IsRequired();
            e.Property(x => x.ToStatus)
                .HasColumnName("TO_STATUS")
                .HasColumnType("VARCHAR2(20 CHAR)")
                .IsRequired();
            e.Property(x => x.ActorId)
                .HasColumnName("ACTOR_ID")
                .HasColumnType("NUMBER(19,0)");
            e.Property(x => x.Principal)
                .HasColumnName("PRINCIPAL")
                .HasColumnType("VARCHAR2(100 CHAR)")
                .IsRequired();
            e.Property(x => x.Reason)
                .HasColumnName("REASON")
                .HasColumnType("VARCHAR2(1000 CHAR)");
            e.Property(x => x.OccurredAtUtc)
                .HasColumnName("OCCURRED_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .IsRequired()
                .HasConversion(UtcDateTime.Converter);
            e.Property(x => x.CreatedAtUtc)
                .HasColumnName("CREATED_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .IsRequired()
                .HasConversion(UtcDateTime.Converter);
        });
        // 연동 대상: 관리자가 등록한 설비/AI 설정의 식별자·활성 상태·설정 버전을 관리한다.
        model.Entity<IntegrationTarget>(e =>
        {
            e.ToTable("INTEGRATION_TARGET");
            e.HasKey("TargetId");
            e.HasIndex("TargetCode")
                .IsUnique()
                .HasDatabaseName("UQ_TGT_CODE");
            e.HasIndex("EquipmentId")
                .IsUnique()
                .HasDatabaseName("UQ_TGT_EQ");
            e.HasAlternateKey("TargetId", "Kind")
                .HasName("UQ_TGT_ID_KIND");
            e.Property(x => x.TargetId)
                .HasColumnName("TARGET_ID")
                .HasColumnType("NUMBER(19,0)")
                .IsRequired()
                .ValueGeneratedOnAdd();
            e.Property(x => x.TargetCode)
                .HasColumnName("TARGET_CODE")
                .HasColumnType("VARCHAR2(64 CHAR)")
                .IsRequired();
            e.Property(x => x.Kind)
                .HasColumnName("KIND")
                .HasColumnType("VARCHAR2(20 CHAR)")
                .IsRequired();
            e.Property(x => x.DisplayName)
                .HasColumnName("DISPLAY_NAME")
                .HasColumnType("VARCHAR2(100 CHAR)")
                .IsRequired();
            e.Property(x => x.ConnectorType)
                .HasColumnName("CONNECTOR_TYPE")
                .HasColumnType("VARCHAR2(40 CHAR)")
                .IsRequired();
            e.Property(x => x.EquipmentId)
                .HasColumnName("EQUIPMENT_ID")
                .HasColumnType("NUMBER(19,0)");
            e.Property(x => x.Enabled)
                .HasColumnName("ENABLED")
                .HasColumnType("NUMBER(1,0)")
                .IsRequired()
                .HasConversion<int>();
            e.Property(x => x.Deleted)
                .HasColumnName("DELETED")
                .HasColumnType("NUMBER(1,0)")
                .IsRequired()
                .HasConversion<int>();
            e.Property(x => x.ConfigVersion)
                .HasColumnName("CONFIG_VERSION")
                .HasColumnType("NUMBER(12,0)")
                .IsRequired();
            e.Property(x => x.ActivatedAtUtc)
                .HasColumnName("ACTIVATED_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .HasConversion(UtcDateTime.Converter);
            e.Property(x => x.CreatedAtUtc)
                .HasColumnName("CREATED_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .IsRequired()
                .HasConversion(UtcDateTime.Converter);
            e.Property(x => x.Version)
                .HasColumnName("VERSION")
                .HasColumnType("NUMBER(10,0)")
                .IsRequired()
                .IsConcurrencyToken();
            e.Property(x => x.UpdatedAtUtc)
                .HasColumnName("UPDATED_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .IsRequired()
                .HasConversion(UtcDateTime.Converter);
        });
        // 설비 접속 설정: 관리자 입력을 게이트웨이 설정 캐시에 투영하고 수집기가 HTTP로 읽는다.
        model.Entity<EquipmentConnection>(e =>
        {
            e.ToTable("EQUIPMENT_CONNECTION");
            e.HasKey("TargetId");
            e.Property(x => x.TargetId)
                .HasColumnName("TARGET_ID")
                .HasColumnType("NUMBER(19,0)")
                .IsRequired()
                .ValueGeneratedNever();
            e.Property(x => x.Protocol)
                .HasColumnName("PROTOCOL")
                .HasColumnType("VARCHAR2(20 CHAR)")
                .IsRequired();
            e.Property(x => x.ApprovedEndpoint)
                .HasColumnName("APPROVED_ENDPOINT")
                .HasColumnType("VARCHAR2(500 CHAR)")
                .IsRequired();
            e.Property(x => x.PollIntervalMs)
                .HasColumnName("POLL_INTERVAL_MS")
                .HasColumnType("NUMBER(12,0)")
                .IsRequired();
            e.Property(x => x.TimeoutMs)
                .HasColumnName("TIMEOUT_MS")
                .HasColumnType("NUMBER(12,0)")
                .IsRequired();
            e.Property(x => x.StaleSec)
                .HasColumnName("STALE_SEC")
                .HasColumnType("NUMBER(12,0)")
                .IsRequired();
            e.Property(x => x.CredentialRef)
                .HasColumnName("CREDENTIAL_REF")
                .HasColumnType("VARCHAR2(200 CHAR)");
            e.Property(x => x.LastValidSampleAtUtc)
                .HasColumnName("LAST_VALID_SAMPLE_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .HasConversion(UtcDateTime.Converter);
            e.Property(x => x.LastValidReceivedAtUtc)
                .HasColumnName("LAST_VALID_RECEIVED_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .HasConversion(UtcDateTime.Converter);
            e.Property(x => x.CreatedAtUtc)
                .HasColumnName("CREATED_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .IsRequired()
                .HasConversion(UtcDateTime.Converter);
            e.Property(x => x.Version)
                .HasColumnName("VERSION")
                .HasColumnType("NUMBER(10,0)")
                .IsRequired()
                .IsConcurrencyToken();
            e.Property(x => x.UpdatedAtUtc)
                .HasColumnName("UPDATED_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .IsRequired()
                .HasConversion(UtcDateTime.Converter);
            e.Property(x => x.Kind)
                .HasColumnName("KIND")
                .HasColumnType("VARCHAR2(20 CHAR)")
                .IsRequired();
        });
        // 원본 필드 매핑: 수집 JSON 경로를 표준 지표·타입·단위로 바꾼다.
        model.Entity<EquipmentTagMapping>(e =>
        {
            e.ToTable("EQUIPMENT_TAG_MAPPING");
            e.HasKey("MappingId");
            e.HasIndex("TargetId", "MetricCode")
                .IsUnique()
                .HasDatabaseName("UQ_MAP_METRIC");
            e.Property(x => x.MappingId)
                .HasColumnName("MAPPING_ID")
                .HasColumnType("NUMBER(19,0)")
                .IsRequired()
                .ValueGeneratedOnAdd();
            e.Property(x => x.TargetId)
                .HasColumnName("TARGET_ID")
                .HasColumnType("NUMBER(19,0)")
                .IsRequired();
            e.Property(x => x.MetricCode)
                .HasColumnName("METRIC_CODE")
                .HasColumnType("VARCHAR2(40 CHAR)")
                .IsRequired();
            e.Property(x => x.SourcePath)
                .HasColumnName("SOURCE_PATH")
                .HasColumnType("VARCHAR2(200 CHAR)")
                .IsRequired();
            e.Property(x => x.DataType)
                .HasColumnName("DATA_TYPE")
                .HasColumnType("VARCHAR2(20 CHAR)")
                .IsRequired();
            e.Property(x => x.ValueKind)
                .HasColumnName("VALUE_KIND")
                .HasColumnType("VARCHAR2(10 CHAR)")
                .IsRequired();
            e.Property(x => x.Unit)
                .HasColumnName("UNIT")
                .HasColumnType("VARCHAR2(20 CHAR)");
            e.Property(x => x.Scale)
                .HasColumnName("SCALE")
                .HasColumnType("NUMBER(18,6)")
                .IsRequired();
            e.Property(x => x.OffsetValue)
                .HasColumnName("OFFSET_VALUE")
                .HasColumnType("NUMBER(18,6)")
                .IsRequired();
            e.Property(x => x.Enabled)
                .HasColumnName("ENABLED")
                .HasColumnType("NUMBER(1,0)")
                .IsRequired()
                .HasConversion<int>();
            e.Property(x => x.LatestValue)
                .HasColumnName("LATEST_VALUE")
                .HasColumnType("NUMBER(18,6)");
            e.Property(x => x.LatestSampleAtUtc)
                .HasColumnName("LATEST_SAMPLE_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .HasConversion(UtcDateTime.Converter);
            e.Property(x => x.LatestReceivedAtUtc)
                .HasColumnName("LATEST_RECEIVED_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .HasConversion(UtcDateTime.Converter);
            e.Property(x => x.CreatedAtUtc)
                .HasColumnName("CREATED_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .IsRequired()
                .HasConversion(UtcDateTime.Converter);
            e.Property(x => x.Version)
                .HasColumnName("VERSION")
                .HasColumnType("NUMBER(10,0)")
                .IsRequired()
                .IsConcurrencyToken();
            e.Property(x => x.UpdatedAtUtc)
                .HasColumnName("UPDATED_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .IsRequired()
                .HasConversion(UtcDateTime.Converter);
        });
        // AI 제공자 설정: Mock/로컬/클라우드 선택을 분리하며 실제 키는 외부 비밀 저장소 참조로 다룬다.
        model.Entity<AiProviderConfig>(e =>
        {
            e.ToTable("AI_PROVIDER_CONFIG");
            e.HasKey("TargetId");
            e.Property(x => x.TargetId)
                .HasColumnName("TARGET_ID")
                .HasColumnType("NUMBER(19,0)")
                .IsRequired()
                .ValueGeneratedNever();
            e.Property(x => x.ProviderKind)
                .HasColumnName("PROVIDER_KIND")
                .HasColumnType("VARCHAR2(10 CHAR)")
                .IsRequired();
            e.Property(x => x.ApprovedEndpoint)
                .HasColumnName("APPROVED_ENDPOINT")
                .HasColumnType("VARCHAR2(500 CHAR)");
            e.Property(x => x.Model)
                .HasColumnName("MODEL")
                .HasColumnType("VARCHAR2(100 CHAR)");
            e.Property(x => x.CredentialRef)
                .HasColumnName("CREDENTIAL_REF")
                .HasColumnType("VARCHAR2(200 CHAR)");
            e.Property(x => x.TimeoutMs)
                .HasColumnName("TIMEOUT_MS")
                .HasColumnType("NUMBER(12,0)")
                .IsRequired();
            e.Property(x => x.CreatedAtUtc)
                .HasColumnName("CREATED_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .IsRequired()
                .HasConversion(UtcDateTime.Converter);
            e.Property(x => x.Version)
                .HasColumnName("VERSION")
                .HasColumnType("NUMBER(10,0)")
                .IsRequired()
                .IsConcurrencyToken();
            e.Property(x => x.UpdatedAtUtc)
                .HasColumnName("UPDATED_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .IsRequired()
                .HasConversion(UtcDateTime.Converter);
            e.Property(x => x.Kind)
                .HasColumnName("KIND")
                .HasColumnType("VARCHAR2(20 CHAR)")
                .IsRequired();
        });
        // 커넥터 실행 상태: heartbeat·마지막 성공·실패 상태를 관리 화면과 통신 경보에서 읽는다.
        model.Entity<ConnectorRuntime>(e =>
        {
            e.ToTable("CONNECTOR_RUNTIME");
            e.HasKey("TargetId", "InstanceId");
            e.Property(x => x.TargetId)
                .HasColumnName("TARGET_ID")
                .HasColumnType("NUMBER(19,0)")
                .IsRequired()
                .ValueGeneratedNever();
            e.Property(x => x.InstanceId)
                .HasColumnName("INSTANCE_ID")
                .HasColumnType("VARCHAR2(64 CHAR)")
                .IsRequired()
                .ValueGeneratedNever();
            e.Property(x => x.AppliedConfigVersion)
                .HasColumnName("APPLIED_CONFIG_VERSION")
                .HasColumnType("NUMBER(12,0)")
                .IsRequired();
            e.Property(x => x.ConnectionState)
                .HasColumnName("CONNECTION_STATE")
                .HasColumnType("VARCHAR2(20 CHAR)")
                .IsRequired();
            e.Property(x => x.OperatingState)
                .HasColumnName("OPERATING_STATE")
                .HasColumnType("VARCHAR2(20 CHAR)");
            e.Property(x => x.LastHeartbeatAtUtc)
                .HasColumnName("LAST_HEARTBEAT_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .HasConversion(UtcDateTime.Converter);
            e.Property(x => x.LastError)
                .HasColumnName("LAST_ERROR")
                .HasColumnType("VARCHAR2(2000 CHAR)");
            e.Property(x => x.CreatedAtUtc)
                .HasColumnName("CREATED_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .IsRequired()
                .HasConversion(UtcDateTime.Converter);
            e.Property(x => x.Version)
                .HasColumnName("VERSION")
                .HasColumnType("NUMBER(10,0)")
                .IsRequired()
                .IsConcurrencyToken();
            e.Property(x => x.UpdatedAtUtc)
                .HasColumnName("UPDATED_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .IsRequired()
                .HasConversion(UtcDateTime.Converter);
        });
        // AI 분석 작업: 예약 식별자·상태·재시도 시각을 보존하여 재시작 후 누락·중복을 제어한다.
        model.Entity<IntegrationJob>(e =>
        {
            e.ToTable("INTEGRATION_JOB");
            e.HasKey("JobId");
            e.HasIndex(
                "Principal",
                "RequestId",
                "Operation"
            )
                .IsUnique()
                .HasDatabaseName("UQ_IJOB_REQUEST");
            e.Property(x => x.JobId)
                .HasColumnName("JOB_ID")
                .HasColumnType("VARCHAR2(36 CHAR)")
                .IsRequired()
                .ValueGeneratedNever();
            e.Property(x => x.RequestId)
                .HasColumnName("REQUEST_ID")
                .HasColumnType("VARCHAR2(36 CHAR)")
                .IsRequired();
            e.Property(x => x.TargetId)
                .HasColumnName("TARGET_ID")
                .HasColumnType("NUMBER(19,0)")
                .IsRequired();
            e.Property(x => x.Operation)
                .HasColumnName("OPERATION")
                .HasColumnType("VARCHAR2(40 CHAR)")
                .IsRequired();
            e.Property(x => x.ActorId)
                .HasColumnName("ACTOR_ID")
                .HasColumnType("NUMBER(19,0)");
            e.Property(x => x.Principal)
                .HasColumnName("PRINCIPAL")
                .HasColumnType("VARCHAR2(100 CHAR)")
                .IsRequired();
            e.Property(x => x.SnapshotId)
                .HasColumnName("SNAPSHOT_ID")
                .HasColumnType("NUMBER(19,0)");
            e.Property(x => x.ConfigVersion)
                .HasColumnName("CONFIG_VERSION")
                .HasColumnType("NUMBER(12,0)")
                .IsRequired();
            e.Property(x => x.Attempt)
                .HasColumnName("ATTEMPT")
                .HasColumnType("NUMBER(12,0)")
                .IsRequired();
            e.Property(x => x.State)
                .HasColumnName("STATE")
                .HasColumnType("VARCHAR2(20 CHAR)")
                .IsRequired();
            e.Property(x => x.DeadlineUtc)
                .HasColumnName("DEADLINE_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .IsRequired()
                .HasConversion(UtcDateTime.Converter);
            e.Property(x => x.RetryAtUtc)
                .HasColumnName("RETRY_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .HasConversion(UtcDateTime.Converter);
            e.Property(x => x.Error)
                .HasColumnName("ERROR")
                .HasColumnType("VARCHAR2(2000 CHAR)");
            e.Property(x => x.CorrelationId)
                .HasColumnName("CORRELATION_ID")
                .HasColumnType("VARCHAR2(64 CHAR)")
                .IsRequired();
            e.Property(x => x.CreatedAtUtc)
                .HasColumnName("CREATED_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .IsRequired()
                .HasConversion(UtcDateTime.Converter);
            e.Property(x => x.Version)
                .HasColumnName("VERSION")
                .HasColumnType("NUMBER(10,0)")
                .IsRequired()
                .IsConcurrencyToken();
            e.Property(x => x.UpdatedAtUtc)
                .HasColumnName("UPDATED_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .IsRequired()
                .HasConversion(UtcDateTime.Converter);
        });
        // 분석 입력 스냅샷: 시계열 사전 집계에서 만든 고정 입력을 재시도 때 재사용한다.
        model.Entity<AnalysisSnapshot>(e =>
        {
            e.ToTable("ANALYSIS_SNAPSHOT");
            e.HasKey("SnapshotId");
            e.Property(x => x.SnapshotId)
                .HasColumnName("SNAPSHOT_ID")
                .HasColumnType("NUMBER(19,0)")
                .IsRequired()
                .ValueGeneratedOnAdd();
            e.Property(x => x.EquipmentId)
                .HasColumnName("EQUIPMENT_ID")
                .HasColumnType("NUMBER(19,0)")
                .IsRequired();
            e.Property(x => x.PeriodStartUtc)
                .HasColumnName("PERIOD_START_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .IsRequired()
                .HasConversion(UtcDateTime.Converter);
            e.Property(x => x.PeriodEndUtc)
                .HasColumnName("PERIOD_END_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .IsRequired()
                .HasConversion(UtcDateTime.Converter);
            e.Property(x => x.InputVersion)
                .HasColumnName("INPUT_VERSION")
                .HasColumnType("NUMBER(12,0)")
                .IsRequired();
            e.Property(x => x.Coverage)
                .HasColumnName("COVERAGE")
                .HasColumnType("NUMBER(18,6)")
                .IsRequired();
            e.Property(x => x.Payload)
                .HasColumnName("PAYLOAD")
                .HasColumnType("CLOB")
                .IsRequired();
            e.Property(x => x.InputHash)
                .HasColumnName("INPUT_HASH")
                .HasColumnType("VARCHAR2(64 CHAR)")
                .IsRequired();
            e.Property(x => x.CreatedAtUtc)
                .HasColumnName("CREATED_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .IsRequired()
                .HasConversion(UtcDateTime.Converter);
        });
        // 원시 수집 이벤트: 서명된 수집 배치를 멱등 저장하고 AUTO 생산과 경보의 근거로 사용한다.
        model.Entity<TelemetryEvent>(e =>
        {
            e.ToTable("TELEMETRY_EVENT");
            e.HasKey("TelemetryId");
            e.HasIndex("EventId")
                .IsUnique()
                .HasDatabaseName("UQ_TEVT_EVENT");
            e.HasIndex(
                "EquipmentId",
                "StreamId",
                "SequenceNo"
            )
                .IsUnique()
                .HasDatabaseName("UQ_TEVT_STREAM");
            e.Property(x => x.TelemetryId)
                .HasColumnName("TELEMETRY_ID")
                .HasColumnType("NUMBER(19,0)")
                .IsRequired()
                .ValueGeneratedOnAdd();
            e.Property(x => x.EventId)
                .HasColumnName("EVENT_ID")
                .HasColumnType("VARCHAR2(64 CHAR)")
                .IsRequired();
            e.Property(x => x.EquipmentId)
                .HasColumnName("EQUIPMENT_ID")
                .HasColumnType("NUMBER(19,0)")
                .IsRequired();
            e.Property(x => x.TargetId)
                .HasColumnName("TARGET_ID")
                .HasColumnType("NUMBER(19,0)")
                .IsRequired();
            e.Property(x => x.StreamId)
                .HasColumnName("STREAM_ID")
                .HasColumnType("VARCHAR2(64 CHAR)")
                .IsRequired();
            e.Property(x => x.SequenceNo)
                .HasColumnName("SEQUENCE_NO")
                .HasColumnType("NUMBER(19,0)")
                .IsRequired();
            e.Property(x => x.SchemaVersion)
                .HasColumnName("SCHEMA_VERSION")
                .HasColumnType("NUMBER(12,0)")
                .IsRequired();
            e.Property(x => x.ConfigVersion)
                .HasColumnName("CONFIG_VERSION")
                .HasColumnType("NUMBER(12,0)")
                .IsRequired();
            e.Property(x => x.OccurredAtUtc)
                .HasColumnName("OCCURRED_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .IsRequired()
                .HasConversion(UtcDateTime.Converter);
            e.Property(x => x.ReceivedAtUtc)
                .HasColumnName("RECEIVED_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .IsRequired()
                .HasConversion(UtcDateTime.Converter);
            e.Property(x => x.Payload)
                .HasColumnName("PAYLOAD")
                .HasColumnType("CLOB")
                .IsRequired();
            e.Property(x => x.PayloadHash)
                .HasColumnName("PAYLOAD_HASH")
                .HasColumnType("VARCHAR2(64 CHAR)")
                .IsRequired();
            e.Property(x => x.CreatedAtUtc)
                .HasColumnName("CREATED_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .IsRequired()
                .HasConversion(UtcDateTime.Converter);
        });
        // 정규화 측정 지표: 매핑별 값·품질을 저장하여 임계치 판정과 AI 집계에 제공한다.
        model.Entity<TelemetryMetric>(e =>
        {
            e.ToTable("TELEMETRY_METRIC");
            e.HasKey("TelemetryId", "MetricCode");
            e.Property(x => x.TelemetryId)
                .HasColumnName("TELEMETRY_ID")
                .HasColumnType("NUMBER(19,0)")
                .IsRequired()
                .ValueGeneratedNever();
            e.Property(x => x.MetricCode)
                .HasColumnName("METRIC_CODE")
                .HasColumnType("VARCHAR2(40 CHAR)")
                .IsRequired()
                .ValueGeneratedNever();
            e.Property(x => x.MappingId)
                .HasColumnName("MAPPING_ID")
                .HasColumnType("NUMBER(19,0)")
                .IsRequired();
            e.Property(x => x.NumericValue)
                .HasColumnName("NUMERIC_VALUE")
                .HasColumnType("NUMBER(18,6)");
            e.Property(x => x.TextValue)
                .HasColumnName("TEXT_VALUE")
                .HasColumnType("VARCHAR2(200 CHAR)");
            e.Property(x => x.Unit)
                .HasColumnName("UNIT")
                .HasColumnType("VARCHAR2(20 CHAR)");
            e.Property(x => x.Quality)
                .HasColumnName("QUALITY")
                .HasColumnType("VARCHAR2(10 CHAR)")
                .IsRequired();
            e.Property(x => x.CreatedAtUtc)
                .HasColumnName("CREATED_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .IsRequired()
                .HasConversion(UtcDateTime.Converter);
        });
        // 트랜잭션 발송 대기열: 업무 변경과 함께 저장한 이벤트를 전달기가 재시도 가능하게 처리한다.
        model.Entity<OutboxEvent>(e =>
        {
            e.ToTable("OUTBOX_EVENT");
            e.HasKey("OutboxId");
            e.HasIndex("EventId")
                .IsUnique()
                .HasDatabaseName("UQ_OUTB_EVENT");
            e.Property(x => x.OutboxId)
                .HasColumnName("OUTBOX_ID")
                .HasColumnType("NUMBER(19,0)")
                .IsRequired()
                .ValueGeneratedOnAdd();
            e.Property(x => x.EventId)
                .HasColumnName("EVENT_ID")
                .HasColumnType("VARCHAR2(64 CHAR)")
                .IsRequired();
            e.Property(x => x.Type)
                .HasColumnName("TYPE")
                .HasColumnType("VARCHAR2(100 CHAR)")
                .IsRequired();
            e.Property(x => x.AggregateType)
                .HasColumnName("AGGREGATE_TYPE")
                .HasColumnType("VARCHAR2(40 CHAR)")
                .IsRequired();
            e.Property(x => x.AggregateId)
                .HasColumnName("AGGREGATE_ID")
                .HasColumnType("VARCHAR2(64 CHAR)")
                .IsRequired();
            e.Property(x => x.AggregateVersion)
                .HasColumnName("AGGREGATE_VERSION")
                .HasColumnType("NUMBER(12,0)")
                .IsRequired();
            e.Property(x => x.Payload)
                .HasColumnName("PAYLOAD")
                .HasColumnType("CLOB")
                .IsRequired();
            e.Property(x => x.Attempts)
                .HasColumnName("ATTEMPTS")
                .HasColumnType("NUMBER(12,0)")
                .IsRequired();
            e.Property(x => x.NextAttemptAtUtc)
                .HasColumnName("NEXT_ATTEMPT_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .HasConversion(UtcDateTime.Converter);
            e.Property(x => x.DeliveredAtUtc)
                .HasColumnName("DELIVERED_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .HasConversion(UtcDateTime.Converter);
            e.Property(x => x.Status)
                .HasColumnName("STATUS")
                .HasColumnType("VARCHAR2(10 CHAR)")
                .IsRequired();
            e.Property(x => x.LastError)
                .HasColumnName("LAST_ERROR")
                .HasColumnType("VARCHAR2(2000 CHAR)");
            e.Property(x => x.CreatedAtUtc)
                .HasColumnName("CREATED_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .IsRequired()
                .HasConversion(UtcDateTime.Converter);
        });
        // AI 분석 결과: 분석 작업에 연결한 예측/위험도와 제공자 결과를 보존한다.
        model.Entity<AiAnalysisResult>(e =>
        {
            e.ToTable("AI_ANALYSIS_RESULT");
            e.HasKey("ResultId");
            e.HasIndex("JobId", "Attempt")
                .IsUnique()
                .HasDatabaseName("UQ_AIRES_ATTEMPT");
            e.Property(x => x.ResultId)
                .HasColumnName("RESULT_ID")
                .HasColumnType("NUMBER(19,0)")
                .IsRequired()
                .ValueGeneratedOnAdd();
            e.Property(x => x.JobId)
                .HasColumnName("JOB_ID")
                .HasColumnType("VARCHAR2(36 CHAR)")
                .IsRequired();
            e.Property(x => x.Attempt)
                .HasColumnName("ATTEMPT")
                .HasColumnType("NUMBER(12,0)")
                .IsRequired();
            e.Property(x => x.Provider)
                .HasColumnName("PROVIDER")
                .HasColumnType("VARCHAR2(20 CHAR)")
                .IsRequired();
            e.Property(x => x.Model)
                .HasColumnName("MODEL")
                .HasColumnType("VARCHAR2(100 CHAR)");
            e.Property(x => x.InputPeriodStartUtc)
                .HasColumnName("INPUT_PERIOD_START_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .IsRequired()
                .HasConversion(UtcDateTime.Converter);
            e.Property(x => x.InputPeriodEndUtc)
                .HasColumnName("INPUT_PERIOD_END_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .IsRequired()
                .HasConversion(UtcDateTime.Converter);
            e.Property(x => x.DataCoverage)
                .HasColumnName("DATA_COVERAGE")
                .HasColumnType("NUMBER(18,6)")
                .IsRequired();
            e.Property(x => x.ResultVersion)
                .HasColumnName("RESULT_VERSION")
                .HasColumnType("NUMBER(12,0)")
                .IsRequired();
            e.Property(x => x.Risk)
                .HasColumnName("RISK")
                .HasColumnType("VARCHAR2(40 CHAR)");
            e.Property(x => x.Evidence)
                .HasColumnName("EVIDENCE")
                .HasColumnType("CLOB")
                .IsRequired();
            e.Property(x => x.MockFlag)
                .HasColumnName("MOCK_FLAG")
                .HasColumnType("NUMBER(1,0)")
                .IsRequired()
                .HasConversion<int>();
            e.Property(x => x.Accepted)
                .HasColumnName("ACCEPTED")
                .HasColumnType("NUMBER(1,0)")
                .IsRequired()
                .HasConversion<int>();
            e.Property(x => x.CompletedAtUtc)
                .HasColumnName("COMPLETED_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .IsRequired()
                .HasConversion(UtcDateTime.Converter);
            e.Property(x => x.CreatedAtUtc)
                .HasColumnName("CREATED_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .IsRequired()
                .HasConversion(UtcDateTime.Converter);
        });
        // 경보 규칙: 관리자가 정한 통신 유실/HIGH 임계치와 유지시간을 수집·감시에서 평가한다.
        model.Entity<AlarmRule>(e =>
        {
            e.ToTable("ALARM_RULE");
            e.HasKey("RuleId");
            e.Property(x => x.RuleId)
                .HasColumnName("RULE_ID")
                .HasColumnType("NUMBER(19,0)")
                .IsRequired()
                .ValueGeneratedOnAdd();
            e.Property(x => x.EquipmentId)
                .HasColumnName("EQUIPMENT_ID")
                .HasColumnType("NUMBER(19,0)")
                .IsRequired();
            e.Property(x => x.TargetId)
                .HasColumnName("TARGET_ID")
                .HasColumnType("NUMBER(19,0)")
                .IsRequired();
            e.Property(x => x.MappingId)
                .HasColumnName("MAPPING_ID")
                .HasColumnType("NUMBER(19,0)");
            e.Property(x => x.Kind)
                .HasColumnName("KIND")
                .HasColumnType("VARCHAR2(10 CHAR)")
                .IsRequired();
            e.Property(x => x.Severity)
                .HasColumnName("SEVERITY")
                .HasColumnType("VARCHAR2(10 CHAR)")
                .IsRequired();
            e.Property(x => x.TriggerHigh)
                .HasColumnName("TRIGGER_HIGH")
                .HasColumnType("NUMBER(18,6)");
            e.Property(x => x.ClearBelow)
                .HasColumnName("CLEAR_BELOW")
                .HasColumnType("NUMBER(18,6)");
            e.Property(x => x.HoldSec)
                .HasColumnName("HOLD_SEC")
                .HasColumnType("NUMBER(12,0)");
            e.Property(x => x.ClearHoldSec)
                .HasColumnName("CLEAR_HOLD_SEC")
                .HasColumnType("NUMBER(12,0)");
            e.Property(x => x.Enabled)
                .HasColumnName("ENABLED")
                .HasColumnType("NUMBER(1,0)")
                .IsRequired()
                .HasConversion<int>();
            e.Property(x => x.SuppressUntilUtc)
                .HasColumnName("SUPPRESS_UNTIL_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .HasConversion(UtcDateTime.Converter);
            e.Property(x => x.CreatedAtUtc)
                .HasColumnName("CREATED_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .IsRequired()
                .HasConversion(UtcDateTime.Converter);
            e.Property(x => x.Version)
                .HasColumnName("VERSION")
                .HasColumnType("NUMBER(10,0)")
                .IsRequired()
                .IsConcurrencyToken();
            e.Property(x => x.UpdatedAtUtc)
                .HasColumnName("UPDATED_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .IsRequired()
                .HasConversion(UtcDateTime.Converter);
        });
        // 발생 경보: 수집·감시가 생성하며 ACK·복구·조치·종결을 구분해 추적한다.
        model.Entity<Alarm>(e =>
        {
            e.ToTable("ALARM");
            e.HasKey("AlarmId");
            e.HasAlternateKey("AlarmId", "EquipmentId")
                .HasName("UQ_ALRM_ID_EQ");
            e.Property(x => x.AlarmId)
                .HasColumnName("ALARM_ID")
                .HasColumnType("NUMBER(19,0)")
                .IsRequired()
                .ValueGeneratedOnAdd();
            e.Property(x => x.RuleId)
                .HasColumnName("RULE_ID")
                .HasColumnType("NUMBER(19,0)")
                .IsRequired();
            e.Property(x => x.EquipmentId)
                .HasColumnName("EQUIPMENT_ID")
                .HasColumnType("NUMBER(19,0)")
                .IsRequired();
            e.Property(x => x.Status)
                .HasColumnName("STATUS")
                .HasColumnType("VARCHAR2(20 CHAR)")
                .IsRequired();
            e.Property(x => x.Kind)
                .HasColumnName("KIND")
                .HasColumnType("VARCHAR2(10 CHAR)")
                .IsRequired();
            e.Property(x => x.Severity)
                .HasColumnName("SEVERITY")
                .HasColumnType("VARCHAR2(10 CHAR)")
                .IsRequired();
            e.Property(x => x.RuleVersion)
                .HasColumnName("RULE_VERSION")
                .HasColumnType("NUMBER(12,0)")
                .IsRequired();
            e.Property(x => x.TriggerValue)
                .HasColumnName("TRIGGER_VALUE")
                .HasColumnType("NUMBER(18,6)");
            e.Property(x => x.TriggerSampleAtUtc)
                .HasColumnName("TRIGGER_SAMPLE_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .HasConversion(UtcDateTime.Converter);
            e.Property(x => x.RuleSnapshot)
                .HasColumnName("RULE_SNAPSHOT")
                .HasColumnType("CLOB")
                .IsRequired();
            e.Property(x => x.OpenedAtUtc)
                .HasColumnName("OPENED_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .IsRequired()
                .HasConversion(UtcDateTime.Converter);
            e.Property(x => x.RecoveredAtUtc)
                .HasColumnName("RECOVERED_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .HasConversion(UtcDateTime.Converter);
            e.Property(x => x.AckBy)
                .HasColumnName("ACK_BY")
                .HasColumnType("NUMBER(19,0)");
            e.Property(x => x.AckAtUtc)
                .HasColumnName("ACK_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .HasConversion(UtcDateTime.Converter);
            e.Property(x => x.CloseKind)
                .HasColumnName("CLOSE_KIND")
                .HasColumnType("VARCHAR2(10 CHAR)");
            e.Property(x => x.ClosedBy)
                .HasColumnName("CLOSED_BY")
                .HasColumnType("NUMBER(19,0)");
            e.Property(x => x.ClosedAtUtc)
                .HasColumnName("CLOSED_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .HasConversion(UtcDateTime.Converter);
            e.Property(x => x.CloseReason)
                .HasColumnName("CLOSE_REASON")
                .HasColumnType("VARCHAR2(2000 CHAR)");
            e.Property(x => x.CreatedAtUtc)
                .HasColumnName("CREATED_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .IsRequired()
                .HasConversion(UtcDateTime.Converter);
            e.Property(x => x.Version)
                .HasColumnName("VERSION")
                .HasColumnType("NUMBER(10,0)")
                .IsRequired()
                .IsConcurrencyToken();
            e.Property(x => x.UpdatedAtUtc)
                .HasColumnName("UPDATED_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .IsRequired()
                .HasConversion(UtcDateTime.Converter);
        });
        // 경보 조치: 담당자의 처리 내용과 진행 상태를 원래 경보에 연결한다.
        model.Entity<AlarmAction>(e =>
        {
            e.ToTable("ALARM_ACTION");
            e.HasKey("ActionId");
            e.Property(x => x.ActionId)
                .HasColumnName("ACTION_ID")
                .HasColumnType("NUMBER(19,0)")
                .IsRequired()
                .ValueGeneratedOnAdd();
            e.Property(x => x.AlarmId)
                .HasColumnName("ALARM_ID")
                .HasColumnType("NUMBER(19,0)")
                .IsRequired();
            e.Property(x => x.OwnerId)
                .HasColumnName("OWNER_ID")
                .HasColumnType("NUMBER(19,0)")
                .IsRequired();
            e.Property(x => x.Status)
                .HasColumnName("STATUS")
                .HasColumnType("VARCHAR2(20 CHAR)")
                .IsRequired();
            e.Property(x => x.Content)
                .HasColumnName("CONTENT")
                .HasColumnType("VARCHAR2(2000 CHAR)")
                .IsRequired();
            e.Property(x => x.Result)
                .HasColumnName("RESULT")
                .HasColumnType("VARCHAR2(2000 CHAR)");
            e.Property(x => x.StartedAtUtc)
                .HasColumnName("STARTED_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .HasConversion(UtcDateTime.Converter);
            e.Property(x => x.CompletedAtUtc)
                .HasColumnName("COMPLETED_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .HasConversion(UtcDateTime.Converter);
            e.Property(x => x.CreatedAtUtc)
                .HasColumnName("CREATED_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .IsRequired()
                .HasConversion(UtcDateTime.Converter);
            e.Property(x => x.Version)
                .HasColumnName("VERSION")
                .HasColumnType("NUMBER(10,0)")
                .IsRequired()
                .IsConcurrencyToken();
            e.Property(x => x.UpdatedAtUtc)
                .HasColumnName("UPDATED_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .IsRequired()
                .HasConversion(UtcDateTime.Converter);
        });
        // 공통 알림 본문: 경보·업무 이벤트가 생성하고 사용자별 수신 상태와 분리한다.
        model.Entity<Notification>(e =>
        {
            e.ToTable("NOTIFICATION");
            e.HasKey("NotificationId");
            e.HasIndex("EventId")
                .IsUnique()
                .HasDatabaseName("UQ_NTF_EVENT");
            e.Property(x => x.NotificationId)
                .HasColumnName("NOTIFICATION_ID")
                .HasColumnType("NUMBER(19,0)")
                .IsRequired()
                .ValueGeneratedOnAdd();
            e.Property(x => x.EventId)
                .HasColumnName("EVENT_ID")
                .HasColumnType("VARCHAR2(64 CHAR)")
                .IsRequired();
            e.Property(x => x.Title)
                .HasColumnName("TITLE")
                .HasColumnType("VARCHAR2(200 CHAR)")
                .IsRequired();
            e.Property(x => x.Body)
                .HasColumnName("BODY")
                .HasColumnType("VARCHAR2(2000 CHAR)")
                .IsRequired();
            e.Property(x => x.AlarmId)
                .HasColumnName("ALARM_ID")
                .HasColumnType("NUMBER(19,0)");
            e.Property(x => x.OrderId)
                .HasColumnName("ORDER_ID")
                .HasColumnType("NUMBER(19,0)");
            e.Property(x => x.AnalysisResultId)
                .HasColumnName("ANALYSIS_RESULT_ID")
                .HasColumnType("NUMBER(19,0)");
            e.Property(x => x.CreatedAtUtc)
                .HasColumnName("CREATED_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .IsRequired()
                .HasConversion(UtcDateTime.Converter);
        });
        // 알림 수신함: 수신 사용자별 읽음 상태를 관리하며 타인 알림 조회를 차단한다.
        model.Entity<NotificationRecipient>(e =>
        {
            e.ToTable("NOTIFICATION_RECIPIENT");
            e.HasKey("NotificationId", "UserId");
            e.Property(x => x.NotificationId)
                .HasColumnName("NOTIFICATION_ID")
                .HasColumnType("NUMBER(19,0)")
                .IsRequired()
                .ValueGeneratedNever();
            e.Property(x => x.UserId)
                .HasColumnName("USER_ID")
                .HasColumnType("NUMBER(19,0)")
                .IsRequired()
                .ValueGeneratedNever();
            e.Property(x => x.ReadAtUtc)
                .HasColumnName("READ_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .HasConversion(UtcDateTime.Converter);
            e.Property(x => x.CreatedAtUtc)
                .HasColumnName("CREATED_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .IsRequired()
                .HasConversion(UtcDateTime.Converter);
        });
        // 채널별 발송 이력: Outbox 전달 결과·Mock 이메일/채팅 응답·재시도 횟수를 보존한다.
        model.Entity<Delivery>(e =>
        {
            e.ToTable("DELIVERY");
            e.HasKey("DeliveryId");
            e.HasIndex(
                "NotificationId",
                "RecipientId",
                "Channel"
            )
                .IsUnique()
                .HasDatabaseName("UQ_DLV_RECIPIENT");
            e.Property(x => x.DeliveryId)
                .HasColumnName("DELIVERY_ID")
                .HasColumnType("NUMBER(19,0)")
                .IsRequired()
                .ValueGeneratedOnAdd();
            e.Property(x => x.NotificationId)
                .HasColumnName("NOTIFICATION_ID")
                .HasColumnType("NUMBER(19,0)")
                .IsRequired();
            e.Property(x => x.RecipientId)
                .HasColumnName("RECIPIENT_ID")
                .HasColumnType("NUMBER(19,0)")
                .IsRequired();
            e.Property(x => x.Channel)
                .HasColumnName("CHANNEL")
                .HasColumnType("VARCHAR2(10 CHAR)")
                .IsRequired();
            e.Property(x => x.DestinationKey)
                .HasColumnName("DESTINATION_KEY")
                .HasColumnType("VARCHAR2(100 CHAR)")
                .IsRequired();
            e.Property(x => x.TargetRef)
                .HasColumnName("TARGET_REF")
                .HasColumnType("VARCHAR2(200 CHAR)")
                .IsRequired();
            e.Property(x => x.Status)
                .HasColumnName("STATUS")
                .HasColumnType("VARCHAR2(10 CHAR)")
                .IsRequired();
            e.Property(x => x.Attempts)
                .HasColumnName("ATTEMPTS")
                .HasColumnType("NUMBER(12,0)")
                .IsRequired();
            e.Property(x => x.NextAttemptAtUtc)
                .HasColumnName("NEXT_ATTEMPT_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .HasConversion(UtcDateTime.Converter);
            e.Property(x => x.SentAtUtc)
                .HasColumnName("SENT_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .HasConversion(UtcDateTime.Converter);
            e.Property(x => x.LastError)
                .HasColumnName("LAST_ERROR")
                .HasColumnType("VARCHAR2(2000 CHAR)");
            e.Property(x => x.CreatedAtUtc)
                .HasColumnName("CREATED_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .IsRequired()
                .HasConversion(UtcDateTime.Converter);
            e.Property(x => x.Version)
                .HasColumnName("VERSION")
                .HasColumnType("NUMBER(10,0)")
                .IsRequired()
                .IsConcurrencyToken();
            e.Property(x => x.UpdatedAtUtc)
                .HasColumnName("UPDATED_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .IsRequired()
                .HasConversion(UtcDateTime.Converter);
        });
        // 감사 이벤트: 누가 어떤 업무 엔티티를 변경했는지 DB에 보존한다. 사유 원문을 운영 로그에 복제하지 않는다.
        model.Entity<AuditEvent>(e =>
        {
            e.ToTable("AUDIT_EVENT");
            e.HasKey("AuditId");
            e.Property(x => x.AuditId)
                .HasColumnName("AUDIT_ID")
                .HasColumnType("NUMBER(19,0)")
                .IsRequired()
                .ValueGeneratedOnAdd();
            e.Property(x => x.ActorId)
                .HasColumnName("ACTOR_ID")
                .HasColumnType("NUMBER(19,0)");
            e.Property(x => x.Principal)
                .HasColumnName("PRINCIPAL")
                .HasColumnType("VARCHAR2(100 CHAR)")
                .IsRequired();
            e.Property(x => x.Action)
                .HasColumnName("ACTION")
                .HasColumnType("VARCHAR2(100 CHAR)")
                .IsRequired();
            e.Property(x => x.EntityType)
                .HasColumnName("ENTITY_TYPE")
                .HasColumnType("VARCHAR2(40 CHAR)")
                .IsRequired();
            e.Property(x => x.EntityId)
                .HasColumnName("ENTITY_ID")
                .HasColumnType("VARCHAR2(64 CHAR)");
            e.Property(x => x.Detail)
                .HasColumnName("DETAIL")
                .HasColumnType("CLOB")
                .IsRequired();
            e.Property(x => x.OccurredAtUtc)
                .HasColumnName("OCCURRED_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .IsRequired()
                .HasConversion(UtcDateTime.Converter);
            e.Property(x => x.CreatedAtUtc)
                .HasColumnName("CREATED_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .IsRequired()
                .HasConversion(UtcDateTime.Converter);
        });
        // 배치 실행 이력: Quartz 작업의 실행 구간·처리 건수·성공 여부를 운영 화면에 제공한다.
        model.Entity<JobRun>(e =>
        {
            e.ToTable("JOB_RUN");
            e.HasKey("JobRunId");
            e.HasIndex(
                "JobKey",
                "ScheduledForUtc",
                "Attempt"
            )
                .IsUnique()
                .HasDatabaseName("UQ_JRUN_ATTEMPT");
            e.Property(x => x.JobRunId)
                .HasColumnName("JOB_RUN_ID")
                .HasColumnType("NUMBER(19,0)")
                .IsRequired()
                .ValueGeneratedOnAdd();
            e.Property(x => x.JobKey)
                .HasColumnName("JOB_KEY")
                .HasColumnType("VARCHAR2(100 CHAR)")
                .IsRequired();
            e.Property(x => x.ScheduledForUtc)
                .HasColumnName("SCHEDULED_FOR_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .IsRequired()
                .HasConversion(UtcDateTime.Converter);
            e.Property(x => x.Attempt)
                .HasColumnName("ATTEMPT")
                .HasColumnType("NUMBER(12,0)")
                .IsRequired();
            e.Property(x => x.Status)
                .HasColumnName("STATUS")
                .HasColumnType("VARCHAR2(10 CHAR)")
                .IsRequired();
            e.Property(x => x.StartedAtUtc)
                .HasColumnName("STARTED_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .IsRequired()
                .HasConversion(UtcDateTime.Converter);
            e.Property(x => x.EndedAtUtc)
                .HasColumnName("ENDED_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .HasConversion(UtcDateTime.Converter);
            e.Property(x => x.AffectedRows)
                .HasColumnName("AFFECTED_ROWS")
                .HasColumnType("NUMBER(12,0)");
            e.Property(x => x.Error)
                .HasColumnName("ERROR")
                .HasColumnType("VARCHAR2(2000 CHAR)");
            e.Property(x => x.CreatedAtUtc)
                .HasColumnName("CREATED_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .IsRequired()
                .HasConversion(UtcDateTime.Converter);
        });
        // 일별 생산 집계: 마감된 MANUAL/AUTO 순원장을 KST 기준으로 재계산한 조회용 데이터다.
        model.Entity<DailyProduction>(e =>
        {
            e.ToTable("DAILY_PRODUCTION");
            e.HasKey(
                "BusinessDate",
                "EquipmentId",
                "ProductId"
            );
            e.Property(x => x.BusinessDate)
                .HasColumnName("BUSINESS_DATE")
                .HasColumnType("DATE")
                .IsRequired()
                .ValueGeneratedNever();
            e.Property(x => x.EquipmentId)
                .HasColumnName("EQUIPMENT_ID")
                .HasColumnType("NUMBER(19,0)")
                .IsRequired()
                .ValueGeneratedNever();
            e.Property(x => x.ProductId)
                .HasColumnName("PRODUCT_ID")
                .HasColumnType("NUMBER(19,0)")
                .IsRequired()
                .ValueGeneratedNever();
            e.Property(x => x.TotalQty)
                .HasColumnName("TOTAL_QTY")
                .HasColumnType("NUMBER(12,0)")
                .IsRequired();
            e.Property(x => x.RejectQty)
                .HasColumnName("REJECT_QTY")
                .HasColumnType("NUMBER(12,0)")
                .IsRequired();
            e.Property(x => x.RefreshedAtUtc)
                .HasColumnName("REFRESHED_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .IsRequired()
                .HasConversion(UtcDateTime.Converter);
            e.Property(x => x.CreatedAtUtc)
                .HasColumnName("CREATED_AT_UTC")
                .HasColumnType("TIMESTAMP(6)")
                .IsRequired()
                .HasConversion(UtcDateTime.Converter);
        });
        model.Entity<UserRole>()
            .HasOne<UserAccount>()
            .WithMany()
            .HasForeignKey("UserId")
            .HasPrincipalKey("UserId")
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_UROLE_01");
        model.Entity<Equipment>()
            .HasOne<Line>()
            .WithMany()
            .HasForeignKey("LineId")
            .HasPrincipalKey("LineId")
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_EQ_02");
        model.Entity<WorkOrder>()
            .HasOne<Product>()
            .WithMany()
            .HasForeignKey("ProductId")
            .HasPrincipalKey("ProductId")
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_WO_03");
        model.Entity<WorkOrder>()
            .HasOne<Equipment>()
            .WithMany()
            .HasForeignKey("EquipmentId")
            .HasPrincipalKey("EquipmentId")
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_WO_04");
        model.Entity<WorkOrder>()
            .HasOne<UserAccount>()
            .WithMany()
            .HasForeignKey("AssigneeId")
            .HasPrincipalKey("UserId")
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_WO_05");
        model.Entity<WorkOrder>()
            .HasOne<EquipmentTagMapping>()
            .WithMany()
            .HasForeignKey("CounterMappingId")
            .HasPrincipalKey("MappingId")
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_WO_09");
        model.Entity<WorkOrder>()
            .HasOne<UserAccount>()
            .WithMany()
            .HasForeignKey("CreatedBy")
            .HasPrincipalKey("UserId")
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_WO_13");
        model.Entity<WorkCheckItem>()
            .HasOne<WorkOrder>()
            .WithMany()
            .HasForeignKey("OrderId")
            .HasPrincipalKey("OrderId")
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_CHK_02");
        model.Entity<WorkCheckItem>()
            .HasOne<UserAccount>()
            .WithMany()
            .HasForeignKey("CheckedBy")
            .HasPrincipalKey("UserId")
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_CHK_07");
        model.Entity<WorkPause>()
            .HasOne<WorkOrder>()
            .WithMany()
            .HasForeignKey(
            "OrderId",
            "EquipmentId"
        )
            .HasPrincipalKey(
            "OrderId",
            "EquipmentId"
        )
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_PAUSE_ORDER_EQ");
        model.Entity<WorkPause>()
            .HasOne<Alarm>()
            .WithMany()
            .HasForeignKey(
            "AlarmId",
            "EquipmentId"
        )
            .HasPrincipalKey(
            "AlarmId",
            "EquipmentId"
        )
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_PAUSE_ALARM_EQ");
        model.Entity<WorkPause>()
            .HasOne<UserAccount>()
            .WithMany()
            .HasForeignKey("RecordedBy")
            .HasPrincipalKey("UserId")
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_PAUSE_08");
        model.Entity<ProductionEntry>()
            .HasOne<WorkOrder>()
            .WithMany()
            .HasForeignKey("OrderId")
            .HasPrincipalKey("OrderId")
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_ENTRY_03");
        model.Entity<ProductionEntry>()
            .HasOne<ProductionEntry>()
            .WithMany()
            .HasForeignKey(
            "ReversalOf",
            "OrderId"
        )
            .HasPrincipalKey(
            "EntryId",
            "OrderId"
        )
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_ENTRY_REV_ORDER");
        model.Entity<ProductionEntry>()
            .HasOne<CorrectionRequest>()
            .WithMany()
            .HasForeignKey(
            "CorrectionId",
            "OrderId"
        )
            .HasPrincipalKey(
            "CorrectionId",
            "OrderId"
        )
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_ENTRY_CORR_ORDER");
        model.Entity<ProductionEntry>()
            .HasOne<UserAccount>()
            .WithMany()
            .HasForeignKey("RecordedBy")
            .HasPrincipalKey("UserId")
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_ENTRY_09");
        model.Entity<CorrectionRequest>()
            .HasOne<WorkOrder>()
            .WithMany()
            .HasForeignKey("OrderId")
            .HasPrincipalKey("OrderId")
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_CORR_02");
        model.Entity<CorrectionRequest>()
            .HasOne<UserAccount>()
            .WithMany()
            .HasForeignKey("RequestedBy")
            .HasPrincipalKey("UserId")
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_CORR_05");
        model.Entity<CorrectionRequest>()
            .HasOne<UserAccount>()
            .WithMany()
            .HasForeignKey("ResolvedBy")
            .HasPrincipalKey("UserId")
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_CORR_06");
        model.Entity<OrderTransition>()
            .HasOne<WorkOrder>()
            .WithMany()
            .HasForeignKey("OrderId")
            .HasPrincipalKey("OrderId")
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_TRANS_02");
        model.Entity<OrderTransition>()
            .HasOne<UserAccount>()
            .WithMany()
            .HasForeignKey("ActorId")
            .HasPrincipalKey("UserId")
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_TRANS_05");
        model.Entity<IntegrationTarget>()
            .HasOne<Equipment>()
            .WithMany()
            .HasForeignKey("EquipmentId")
            .HasPrincipalKey("EquipmentId")
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_TGT_06");
        model.Entity<EquipmentConnection>()
            .HasOne<IntegrationTarget>()
            .WithMany()
            .HasForeignKey(
            "TargetId",
            "Kind"
        )
            .HasPrincipalKey(
            "TargetId",
            "Kind"
        )
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_CONN_TARGET_KIND");
        model.Entity<EquipmentTagMapping>()
            .HasOne<EquipmentConnection>()
            .WithMany()
            .HasForeignKey("TargetId")
            .HasPrincipalKey("TargetId")
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_MAP_02");
        model.Entity<AiProviderConfig>()
            .HasOne<IntegrationTarget>()
            .WithMany()
            .HasForeignKey(
            "TargetId",
            "Kind"
        )
            .HasPrincipalKey(
            "TargetId",
            "Kind"
        )
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_AIP_TARGET_KIND");
        model.Entity<ConnectorRuntime>()
            .HasOne<IntegrationTarget>()
            .WithMany()
            .HasForeignKey("TargetId")
            .HasPrincipalKey("TargetId")
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_RT_01");
        model.Entity<IntegrationJob>()
            .HasOne<IntegrationTarget>()
            .WithMany()
            .HasForeignKey("TargetId")
            .HasPrincipalKey("TargetId")
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_IJOB_03");
        model.Entity<IntegrationJob>()
            .HasOne<UserAccount>()
            .WithMany()
            .HasForeignKey("ActorId")
            .HasPrincipalKey("UserId")
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_IJOB_05");
        model.Entity<IntegrationJob>()
            .HasOne<AnalysisSnapshot>()
            .WithMany()
            .HasForeignKey("SnapshotId")
            .HasPrincipalKey("SnapshotId")
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_IJOB_07");
        model.Entity<AnalysisSnapshot>()
            .HasOne<Equipment>()
            .WithMany()
            .HasForeignKey("EquipmentId")
            .HasPrincipalKey("EquipmentId")
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_SNAP_02");
        model.Entity<TelemetryEvent>()
            .HasOne<Equipment>()
            .WithMany()
            .HasForeignKey("EquipmentId")
            .HasPrincipalKey("EquipmentId")
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_TEVT_03");
        model.Entity<TelemetryEvent>()
            .HasOne<EquipmentConnection>()
            .WithMany()
            .HasForeignKey("TargetId")
            .HasPrincipalKey("TargetId")
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_TEVT_04");
        model.Entity<TelemetryMetric>()
            .HasOne<TelemetryEvent>()
            .WithMany()
            .HasForeignKey("TelemetryId")
            .HasPrincipalKey("TelemetryId")
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_TMET_01");
        model.Entity<TelemetryMetric>()
            .HasOne<EquipmentTagMapping>()
            .WithMany()
            .HasForeignKey("MappingId")
            .HasPrincipalKey("MappingId")
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_TMET_03");
        model.Entity<AiAnalysisResult>()
            .HasOne<IntegrationJob>()
            .WithMany()
            .HasForeignKey("JobId")
            .HasPrincipalKey("JobId")
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_AIRES_02");
        model.Entity<AlarmRule>()
            .HasOne<Equipment>()
            .WithMany()
            .HasForeignKey("EquipmentId")
            .HasPrincipalKey("EquipmentId")
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_ARULE_02");
        model.Entity<AlarmRule>()
            .HasOne<EquipmentConnection>()
            .WithMany()
            .HasForeignKey("TargetId")
            .HasPrincipalKey("TargetId")
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_ARULE_03");
        model.Entity<AlarmRule>()
            .HasOne<EquipmentTagMapping>()
            .WithMany()
            .HasForeignKey("MappingId")
            .HasPrincipalKey("MappingId")
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_ARULE_04");
        model.Entity<Alarm>()
            .HasOne<AlarmRule>()
            .WithMany()
            .HasForeignKey("RuleId")
            .HasPrincipalKey("RuleId")
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_ALRM_02");
        model.Entity<Alarm>()
            .HasOne<Equipment>()
            .WithMany()
            .HasForeignKey("EquipmentId")
            .HasPrincipalKey("EquipmentId")
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_ALRM_03");
        model.Entity<Alarm>()
            .HasOne<UserAccount>()
            .WithMany()
            .HasForeignKey("AckBy")
            .HasPrincipalKey("UserId")
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_ALRM_13");
        model.Entity<Alarm>()
            .HasOne<UserAccount>()
            .WithMany()
            .HasForeignKey("ClosedBy")
            .HasPrincipalKey("UserId")
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_ALRM_16");
        model.Entity<AlarmAction>()
            .HasOne<Alarm>()
            .WithMany()
            .HasForeignKey("AlarmId")
            .HasPrincipalKey("AlarmId")
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_ACT_02");
        model.Entity<AlarmAction>()
            .HasOne<UserAccount>()
            .WithMany()
            .HasForeignKey("OwnerId")
            .HasPrincipalKey("UserId")
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_ACT_03");
        model.Entity<Notification>()
            .HasOne<Alarm>()
            .WithMany()
            .HasForeignKey("AlarmId")
            .HasPrincipalKey("AlarmId")
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_NTF_05");
        model.Entity<Notification>()
            .HasOne<WorkOrder>()
            .WithMany()
            .HasForeignKey("OrderId")
            .HasPrincipalKey("OrderId")
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_NTF_06");
        model.Entity<Notification>()
            .HasOne<AiAnalysisResult>()
            .WithMany()
            .HasForeignKey("AnalysisResultId")
            .HasPrincipalKey("ResultId")
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_NTF_07");
        model.Entity<NotificationRecipient>()
            .HasOne<Notification>()
            .WithMany()
            .HasForeignKey("NotificationId")
            .HasPrincipalKey("NotificationId")
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_NREC_01");
        model.Entity<NotificationRecipient>()
            .HasOne<UserAccount>()
            .WithMany()
            .HasForeignKey("UserId")
            .HasPrincipalKey("UserId")
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_NREC_02");
        model.Entity<Delivery>()
            .HasOne<NotificationRecipient>()
            .WithMany()
            .HasForeignKey(
            "NotificationId",
            "RecipientId"
        )
            .HasPrincipalKey(
            "NotificationId",
            "UserId"
        )
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_DLV_RECIPIENT");
        model.Entity<AuditEvent>()
            .HasOne<UserAccount>()
            .WithMany()
            .HasForeignKey("ActorId")
            .HasPrincipalKey("UserId")
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_AUD_02");
        model.Entity<DailyProduction>()
            .HasOne<Equipment>()
            .WithMany()
            .HasForeignKey("EquipmentId")
            .HasPrincipalKey("EquipmentId")
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_DAY_02");
        model.Entity<DailyProduction>()
            .HasOne<Product>()
            .WithMany()
            .HasForeignKey("ProductId")
            .HasPrincipalKey("ProductId")
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_DAY_03");
    }

    private bool preserveCounterProjectionVersion;

    // 수집으로 갱신한 원시 카운터는 작업자의 편집 버전과 분리한다.
    // 설비/작업 행 잠금은 그대로 유지하며 상태·수동 실적·이상 전이는 기존 버전을 올린다.
    public async Task SaveCounterProjectionAsync(bool preserveOrderVersion)
    {
        preserveCounterProjectionVersion = preserveOrderVersion;
        try
        {
            await SaveChangesAsync();
        }
        finally
        {
            preserveCounterProjectionVersion = false;
        }
    }

    private void StampChanges()
    {
        var now = DateTime.UtcNow;
        foreach (var entry in ChangeTracker.Entries()
            .Where(x => x.State is EntityState.Added or EntityState.Modified))
        {
            if (entry.State == EntityState.Added
                && entry.Metadata.FindProperty("CreatedAtUtc") is not null)
                entry.Property("CreatedAtUtc")
                    .CurrentValue = now;
            if (entry.Metadata.FindProperty("UpdatedAtUtc") is not null)
                entry.Property("UpdatedAtUtc")
                    .CurrentValue = now;
            if (entry.State == EntityState.Modified
                && entry.Metadata.FindProperty("Version") is not null
                && !(preserveCounterProjectionVersion && entry.Entity is WorkOrder))
                entry.Property("Version")
                    .CurrentValue =
                    checked((long)entry.Property("Version")
                        .OriginalValue! + 1);
        }
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampChanges();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken =
        default
    )
    {
        StampChanges();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }
}
