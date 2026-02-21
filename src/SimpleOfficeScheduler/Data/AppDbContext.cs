using Microsoft.EntityFrameworkCore;
using SimpleOfficeScheduler.Models;

namespace SimpleOfficeScheduler.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<Event> Events => Set<Event>();
    public DbSet<EventOccurrence> EventOccurrences => Set<EventOccurrence>();
    public DbSet<EventSignup> EventSignups => Set<EventSignup>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // AppUser
        modelBuilder.Entity<AppUser>(entity =>
        {
            entity.HasIndex(e => e.Username).IsUnique();
            entity.Property(e => e.Username).HasMaxLength(256);
            entity.Property(e => e.DisplayName).HasMaxLength(256);
            entity.Property(e => e.Email).HasMaxLength(256);
        });

        // Event
        modelBuilder.Entity<Event>(entity =>
        {
            entity.HasOne(e => e.Owner)
                .WithMany(u => u.OwnedEvents)
                .HasForeignKey(e => e.OwnerUserId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.Property(e => e.Title).HasMaxLength(256);

            // RecurrencePattern as owned entity
            entity.OwnsOne(e => e.Recurrence, recurrence =>
            {
                recurrence.Property(r => r.Type).HasColumnName("Recurrence_Type");
                recurrence.Property(r => r.Interval).HasColumnName("Recurrence_Interval");
                recurrence.Property(r => r.RecurrenceEndDate).HasColumnName("Recurrence_EndDate");
                recurrence.Property(r => r.MaxOccurrences).HasColumnName("Recurrence_MaxOccurrences");

                // Store DaysOfWeek as JSON string for SQLite
                recurrence.Property(r => r.DaysOfWeek)
                    .HasColumnName("Recurrence_DaysOfWeek")
                    .HasConversion(
                        v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                        v => System.Text.Json.JsonSerializer.Deserialize<List<DayOfWeek>>(v, (System.Text.Json.JsonSerializerOptions?)null) ?? new List<DayOfWeek>()
                    );
            });
        });

        // EventOccurrence
        modelBuilder.Entity<EventOccurrence>(entity =>
        {
            entity.HasOne(e => e.Event)
                .WithMany(ev => ev.Occurrences)
                .HasForeignKey(e => e.EventId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => new { e.EventId, e.StartTime });
            entity.HasIndex(e => e.StartTime);
        });

        // EventSignup
        modelBuilder.Entity<EventSignup>(entity =>
        {
            entity.HasOne(e => e.Occurrence)
                .WithMany(o => o.Signups)
                .HasForeignKey(e => e.EventOccurrenceId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.User)
                .WithMany(u => u.Signups)
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => new { e.EventOccurrenceId, e.UserId }).IsUnique();
        });
    }
}
