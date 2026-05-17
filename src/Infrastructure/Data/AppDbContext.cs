using Domain.Sessions;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Data;

public sealed class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<UserSession> UserSessions => Set<UserSession>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UserSession>(b =>
        {
            b.ToTable("user_sessions");
            b.HasKey(x => x.TelegramUserId);

            b.Property(x => x.TelegramUserId)
                .HasColumnName("telegram_user_id")
                // Telegram supplies this id; we must never let Postgres auto-generate it.
                .ValueGeneratedNever();
            b.Property(x => x.PhoneNumber).HasColumnName("phone_number").HasMaxLength(32);
            b.Property(x => x.DisplayName).HasColumnName("display_name").HasMaxLength(256);
            b.Property(x => x.SessionBytes).HasColumnName("session_bytes").IsRequired();
            b.Property(x => x.Nonce).HasColumnName("nonce").IsRequired();
            b.Property(x => x.Tag).HasColumnName("tag").IsRequired();
            b.Property(x => x.CreatedAt).HasColumnName("created_at");
            b.Property(x => x.LastUsedAt).HasColumnName("last_used_at");
            b.Property(x => x.IsActive).HasColumnName("is_active");
        });
    }
}
