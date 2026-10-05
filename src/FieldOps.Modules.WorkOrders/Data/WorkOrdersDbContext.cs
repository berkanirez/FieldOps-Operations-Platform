using FieldOps.Modules.WorkOrders.Domain;
using Microsoft.EntityFrameworkCore;

namespace FieldOps.Modules.WorkOrders.Data;

// internal — same ADR 0003 pattern as OrganizationsDbContext (Day 48).
internal class WorkOrdersDbContext : DbContext
{
    public WorkOrdersDbContext(DbContextOptions<WorkOrdersDbContext> options) : base(options)
    {
    }

    public DbSet<WorkOrder> WorkOrders => Set<WorkOrder>();

    // Day 71: the Outbox pattern's table, in the same database/DbContext as
    // WorkOrder itself — this is what makes a status change and its outbox
    // row commit atomically in one SaveChanges/transaction. A separate
    // database (even one for "all outbox messages") would defeat the whole
    // point: the guarantee only exists because both writes go through the
    // exact same DbContext instance's change tracker.
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    // Day 73: the Inbox pattern's table — the consumer-side mirror of
    // OutboxMessages above.
    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();

    // Day 74: dead-letter queue's supporting table — how many times each
    // (consumer, message) pair has failed so far.
    public DbSet<FailedMessageAttempt> FailedMessageAttempts => Set<FailedMessageAttempt>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WorkOrder>(entity =>
        {
            entity.Property(w => w.Title).IsRequired().HasMaxLength(200);

            // EF Core's "primitive collection" support (EF Core 8+) maps
            // List<string> to a JSON column on SQL Server automatically —
            // no separate EvidenceNote table needed for a demo-simplified,
            // text-only stand-in for real file evidence (Day 46).
            entity.PrimitiveCollection(w => w.EvidenceNotes);

            // Day 114: every list/report query filters by OrganizationId
            // (Day 113). Without an index SQL Server read the whole table —
            // measured: 1,674 pages to return organization 1's 2 rows out of
            // 50,002. A single-column index helped only whichever organization
            // ran first (parameter sniffing): planned for the small one, the
            // cached plan cost the 50,000-row one 150,089 reads via per-row
            // lookups. Status is included so the report's GROUP BY is answered
            // from the index alone, with no lookups, for any organization size.
            // The list still needs lookups for its other columns — bounded only
            // once it is paginated. Small cost on every insert.
            entity.HasIndex(w => new { w.OrganizationId, w.Status });

            // No HasData — WorkOrders started empty in-memory (Day 40, no
            // bootstrap problem the way Employees had) and stays empty here.
        });

        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.Property(m => m.EventType).IsRequired().HasMaxLength(200);
            entity.Property(m => m.Payload).IsRequired();
        });

        modelBuilder.Entity<ProcessedMessage>(entity =>
        {
            entity.Property(m => m.ConsumerName).IsRequired().HasMaxLength(200);
            entity.Property(m => m.MessageId).IsRequired().HasMaxLength(200);

            // The actual idempotency guarantee: the database itself refuses
            // a second row for the same (consumer, message) pair, so even
            // two near-simultaneous redeliveries can't both slip past a
            // read-then-write race in application code.
            entity.HasIndex(m => new { m.ConsumerName, m.MessageId }).IsUnique();
        });

        modelBuilder.Entity<FailedMessageAttempt>(entity =>
        {
            entity.Property(m => m.ConsumerName).IsRequired().HasMaxLength(200);
            entity.Property(m => m.MessageId).IsRequired().HasMaxLength(200);
            entity.HasIndex(m => new { m.ConsumerName, m.MessageId }).IsUnique();
        });
    }
}
