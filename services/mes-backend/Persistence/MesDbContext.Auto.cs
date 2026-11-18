using FactoryMES.Backend.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace FactoryMES.Backend.Persistence;

public sealed partial class MesDbContext
{
    // 원시 수집→소비 체크포인트→생산 원장의 관계를 매핑한다. 기존 원장의 행과 출처는 보존한다.
    private static void ConfigureAutoProduction(ModelBuilder model)
    {
        model.Entity<ProductionEntry>(entry =>
        {
            entry.Property(x => x.Source).HasColumnName("ENTRY_SOURCE")
                .HasColumnType("VARCHAR2(10 CHAR)").HasDefaultValue("MANUAL").IsRequired();
            entry.Property(x => x.TelemetryId).HasColumnName("TELEMETRY_ID")
                .HasColumnType("NUMBER(19,0)");
            entry.HasIndex(x => x.TelemetryId).HasDatabaseName("IX_ENTRY_TELEM");
            entry.HasOne<TelemetryEvent>().WithMany().HasForeignKey(x => x.TelemetryId)
                .OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_ENTRY_TELEM");
        });
        model.Entity<AutoCounterState>(state =>
        {
            state.ToTable("AUTO_COUNTER_STATE");
            state.HasKey(x => x.OrderId).HasName("PK_AUTO_STATE");
            state.Property(x => x.OrderId).HasColumnName("ORDER_ID").HasColumnType("NUMBER(19,0)")
                .ValueGeneratedNever();
            state.Property(x => x.LastTelemetryId).HasColumnName("LAST_TELEMETRY_ID")
                .HasColumnType("NUMBER(19,0)");
            state.Property(x => x.LastStreamId).HasColumnName("LAST_STREAM_ID")
                .HasColumnType("VARCHAR2(64 CHAR)");
            state.Property(x => x.LastSequenceNo).HasColumnName("LAST_SEQUENCE_NO")
                .HasColumnType("NUMBER(19,0)");
            state.Property(x => x.LastCounter).HasColumnName("LAST_COUNTER")
                .HasColumnType("NUMBER(18,6)");
            state.Property(x => x.LastSampleAtUtc).HasColumnName("LAST_SAMPLE_AT_UTC")
                .HasColumnType("TIMESTAMP(6)").HasConversion(UtcDateTime.Converter);
            state.Property(x => x.LastReceivedAtUtc).HasColumnName("LAST_RECEIVED_AT_UTC")
                .HasColumnType("TIMESTAMP(6)").HasConversion(UtcDateTime.Converter);
            state.Property(x => x.AcceptAfterUtc).HasColumnName("ACCEPT_AFTER_UTC")
                .HasColumnType("TIMESTAMP(6)").HasConversion(UtcDateTime.Converter).IsRequired();
            state.Property(x => x.AwaitingBaseline).HasColumnName("AWAITING_BASELINE")
                .HasColumnType("NUMBER(1,0)").HasConversion<int>().IsRequired();
            state.Property(x => x.ConfigVersion).HasColumnName("CONFIG_VERSION")
                .HasColumnType("NUMBER(12,0)");
            state.Property(x => x.UpdatedAtUtc).HasColumnName("UPDATED_AT_UTC")
                .HasColumnType("TIMESTAMP(6)").HasConversion(UtcDateTime.Converter);
            state.HasIndex(x => x.LastTelemetryId).HasDatabaseName("IX_AUTO_TELEM");
            state.HasOne<WorkOrder>().WithMany().HasForeignKey(x => x.OrderId)
                .OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_AUTO_ORDER");
            state.HasOne<TelemetryEvent>().WithMany().HasForeignKey(x => x.LastTelemetryId)
                .OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_AUTO_TELEM");
        });
    }
}

