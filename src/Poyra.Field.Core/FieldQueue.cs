using Microsoft.EntityFrameworkCore;

namespace Poyra.Field.Core;

/// <summary>Cihazdaki SQLite kuyruğu.</summary>
public sealed class FieldQueueDbContext(DbContextOptions<FieldQueueDbContext> options) : DbContext(options)
{
    public DbSet<QueuedCollection> Queue => Set<QueuedCollection>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<QueuedCollection>();

        // Anahtar SIRA'dır, kimlik değil. Sebebi teknik: SQLite yalnız INTEGER PRIMARY KEY'i
        // (rowid) kendiliğinden artırır.
        entity.HasKey(x => x.Sequence);
        entity.Property(x => x.Sequence).ValueGeneratedOnAdd();

        // İş kimliği yine de TEKİL: aynı işlem iki kez kuyruğa giremez
        entity.Property(x => x.ClientOpId).ValueGeneratedNever();
        entity.HasIndex(x => x.ClientOpId).IsUnique();

        entity.HasIndex(x => new { x.State, x.Sequence });
    }

    public static FieldQueueDbContext Open(string databasePath)
    {
        var options = new DbContextOptionsBuilder<FieldQueueDbContext>()
            .UseSqlite($"Data Source={databasePath}")
            .Options;

        var db = new FieldQueueDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }
}

public sealed record SyncOutcome(
    Guid ClientOpId, string Outcome, string? CollectionId,
    string? Status, string? CheckoutUrl, string? Reason);


public sealed class FieldQueue(FieldQueueDbContext db)
{

    public const int MaxBatch = 200;


    public async Task<QueuedCollection> EnqueueAsync(QueuedCollection item, CancellationToken ct = default)
    {
        db.Queue.Add(item);
        await db.SaveChangesAsync(ct);
        return item;
    }

    public async Task<IReadOnlyList<QueuedCollection>> PendingAsync(
        int limit = MaxBatch, CancellationToken ct = default)
        => await db.Queue
            .Where(x => x.State == QueueState.Pending)
            .OrderBy(x => x.Sequence) // cihazdaki gerçek üretim sırası
            .Take(Math.Min(limit, MaxBatch))
            .ToListAsync(ct);


    public async Task ApplyAsync(IEnumerable<SyncOutcome> outcomes, CancellationToken ct = default)
    {
        var byId = outcomes.ToDictionary(o => o.ClientOpId);
        var ids = byId.Keys.ToList();

        var rows = await db.Queue.Where(x => ids.Contains(x.ClientOpId)).ToListAsync(ct);

        foreach (var row in rows)
        {
            var outcome = byId[row.ClientOpId];

            switch (outcome.Outcome)
            {
                case "accepted" or "duplicate":
                    row.State = QueueState.Synced;
                    row.ServerId = outcome.CollectionId;
                    row.ServerStatus = outcome.Status;
                    row.CheckoutUrl = outcome.CheckoutUrl;
                    break;

                case "rejected":
                    row.State = QueueState.Rejected;
                    row.RejectReason = outcome.Reason;
                    break;
            }
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task MarkAttemptAsync(
        IEnumerable<Guid> clientOpIds, DateTimeOffset at, CancellationToken ct = default)
    {
        var ids = clientOpIds.ToList();
        var rows = await db.Queue.Where(x => ids.Contains(x.ClientOpId)).ToListAsync(ct);

        foreach (var row in rows)
        {
            row.Attempts++;
            row.LastAttemptAt = at;
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<QueuedCollection>> RejectedAsync(CancellationToken ct = default)
        => await db.Queue.Where(x => x.State == QueueState.Rejected).ToListAsync(ct);

    public async Task<(int Pending, int Synced, int Rejected, long PendingMinor)> SummaryAsync(
        CancellationToken ct = default)
    {
        var rows = await db.Queue.ToListAsync(ct);
        return (
            rows.Count(r => r.State == QueueState.Pending),
            rows.Count(r => r.State == QueueState.Synced),
            rows.Count(r => r.State == QueueState.Rejected),
            rows.Where(r => r.State == QueueState.Pending).Sum(r => r.AmountMinor));
    }
}
