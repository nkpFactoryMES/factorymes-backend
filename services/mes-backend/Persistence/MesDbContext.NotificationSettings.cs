using FactoryMES.Backend.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace FactoryMES.Backend.Persistence;

public sealed partial class MesDbContext
{
    // Oracle 복합 PK와 버전 토큰으로 동일 사용자·이벤트의 중복 생성/갱신 충돌을 막는다.
    private static void ConfigureNotificationSettings(ModelBuilder model)
    {
        model.Entity<NotificationChannelSetting>(setting =>
        {
            setting.ToTable("NOTIFICATION_CHANNEL_SETTING");
            setting.HasKey(x => new { x.UserId, x.EventKind }).HasName("PK_NCS");
            setting.Property(x => x.UserId).HasColumnName("USER_ID")
                .HasColumnType("NUMBER(19,0)").ValueGeneratedNever();
            setting.Property(x => x.EventKind).HasColumnName("EVENT_KIND")
                .HasColumnType("VARCHAR2(40 CHAR)").IsRequired();
            setting.Property(x => x.EmailMode).HasColumnName("EMAIL_MODE")
                .HasColumnType("VARCHAR2(10 CHAR)").IsRequired();
            setting.Property(x => x.ChatMode).HasColumnName("CHAT_MODE")
                .HasColumnType("VARCHAR2(10 CHAR)").IsRequired();
            setting.Property(x => x.Version).HasColumnName("VERSION")
                .HasColumnType("NUMBER(19,0)").IsConcurrencyToken();
            setting.Property(x => x.UpdatedBy).HasColumnName("UPDATED_BY")
                .HasColumnType("NUMBER(19,0)");
            setting.Property(x => x.UpdatedAtUtc).HasColumnName("UPDATED_AT_UTC")
                .HasColumnType("TIMESTAMP(6)").HasConversion(UtcDateTime.Converter);
            setting.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_NCS_USER");
            setting.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.UpdatedBy)
                .OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_NCS_ACTOR");
        });
    }
}
