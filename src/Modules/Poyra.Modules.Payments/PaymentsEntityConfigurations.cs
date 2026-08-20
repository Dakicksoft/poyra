using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Poyra.Modules.Payments.Domain;

namespace Poyra.Modules.Payments;

internal sealed class PaymentIntentConfiguration : IEntityTypeConfiguration<PaymentIntent>
{
    public void Configure(EntityTypeBuilder<PaymentIntent> b)
    {
        b.Property(x => x.CustomerRef).HasMaxLength(100);
        b.Property(x => x.CustomerIp).HasMaxLength(45);
        b.Property(x => x.RiskDecisionJson).HasColumnName("risk_decision").HasColumnType("jsonb");
        b.HasIndex(x => new { x.CustomerRef, x.CreatedAt });
        b.HasIndex(x => new { x.CustomerIp, x.CreatedAt });

        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();

        // Dış kimlik DB'de üretilir: deterministik, çakışmasız, insert'te RETURNING ile okunur.
        b.Property(x => x.PublicId)
            .HasComputedColumnSql("'pay_' || replace(id::text, '-', '')", stored: true);
        b.HasIndex(x => x.PublicId).IsUnique();

        b.Property(x => x.Currency).HasMaxLength(3).IsFixedLength();
        b.Property(x => x.Description).HasMaxLength(500);
        b.Property(x => x.ClientSecret).HasMaxLength(64);
        b.Property(x => x.ReturnUrl).HasMaxLength(2000);
        b.Property(x => x.Channel).HasMaxLength(20);
        b.Property(x => x.RoutingResultJson).HasColumnName("routing_result").HasColumnType("jsonb");

        b.Property(x => x.Status)
            .HasConversion(s => PaymentStatusMap.ToDb[s], s => PaymentStatusMap.FromDb[s])
            .HasMaxLength(32);

        // Sıcak sorgu: işyerinin son işlemleri
        b.HasIndex(x => new { x.TenantId, x.CreatedAt }).IsDescending(false, true);

        b.ToTable(t =>
        {
            t.HasCheckConstraint("ck_payment_intents_amount_positive", "amount_minor > 0");
            t.HasCheckConstraint("ck_payment_intents_installments", "installments BETWEEN 1 AND 12");
        });
    }
}

internal sealed class PaymentAttemptConfiguration : IEntityTypeConfiguration<PaymentAttempt>
{
    public void Configure(EntityTypeBuilder<PaymentAttempt> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();

        b.Property(x => x.PublicId)
            .HasComputedColumnSql("'att_' || replace(id::text, '-', '')", stored: true);
        b.HasIndex(x => x.PublicId).IsUnique();

        b.Property(x => x.ConnectorKey).HasMaxLength(40);
        b.Property(x => x.Currency).HasMaxLength(3).IsFixedLength();
        b.Property(x => x.RedirectActionUrl).HasMaxLength(2000);
        b.Property(x => x.RedirectFieldsJson).HasColumnName("redirect_fields").HasColumnType("jsonb");
        b.Property(x => x.ConnectorTxnId).HasMaxLength(100);
        b.Property(x => x.AuthCode).HasMaxLength(20);
        b.Property(x => x.RedirectMethod).HasMaxLength(8);
        b.Property(x => x.ConnectorStateJson).HasColumnType("jsonb");
        b.Property(x => x.MaskedPan).HasMaxLength(30);
        b.Property(x => x.CardProgram).HasMaxLength(20);
        b.Property(x => x.CardBank).HasMaxLength(8);
        b.Property(x => x.CardBrand).HasMaxLength(20);
        b.Property(x => x.ErrorUnifiedCode).HasMaxLength(60);
        b.Property(x => x.ErrorRawCode).HasMaxLength(40);
        b.Property(x => x.ErrorRawMessage).HasMaxLength(500);

        b.Property(x => x.Status)
            .HasConversion(s => AttemptStatusMap.ToDb[s], s => AttemptStatusMap.FromDb[s])
            .HasMaxLength(32);

        b.HasIndex(x => new { x.PaymentIntentId, x.AttemptNo }).IsUnique();
        b.HasIndex(x => new { x.TenantId, x.CreatedAt }).IsDescending(false, true);

        b.HasOne<PaymentIntent>().WithMany()
            .HasForeignKey(x => x.PaymentIntentId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PaymentEventConfiguration : IEntityTypeConfiguration<PaymentEvent>
{
    public void Configure(EntityTypeBuilder<PaymentEvent> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.EventType).HasMaxLength(64);
        b.Property(x => x.Actor).HasMaxLength(32);
        b.Property(x => x.Payload).HasColumnType("jsonb");
        b.HasIndex(x => new { x.TenantId, x.PaymentIntentId, x.CreatedAt });

        b.HasOne<PaymentIntent>().WithMany()
            .HasForeignKey(x => x.PaymentIntentId)
            .OnDelete(DeleteBehavior.Restrict); // silme zaten yasak
    }
}

internal sealed class RefundConfiguration : IEntityTypeConfiguration<Refund>
{
    public void Configure(EntityTypeBuilder<Refund> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();

        b.Property(x => x.PublicId)
            .HasComputedColumnSql("'ref_' || replace(id::text, '-', '')", stored: true);
        b.HasIndex(x => x.PublicId).IsUnique();

        b.Property(x => x.Currency).HasMaxLength(3).IsFixedLength();
        b.Property(x => x.ConnectorRefundId).HasMaxLength(100);
        b.Property(x => x.ErrorUnifiedCode).HasMaxLength(60);
        b.Property(x => x.ErrorRawCode).HasMaxLength(40);
        b.Property(x => x.ErrorRawMessage).HasMaxLength(500);

        b.Property(x => x.Status)
            .HasConversion(s => RefundStatusMap.ToDb[s], s => RefundStatusMap.FromDb[s])
            .HasMaxLength(16);

        b.HasIndex(x => new { x.TenantId, x.PaymentIntentId });

        b.HasOne<PaymentIntent>().WithMany()
            .HasForeignKey(x => x.PaymentIntentId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PaymentAttempt>().WithMany()
            .HasForeignKey(x => x.PaymentAttemptId).OnDelete(DeleteBehavior.Restrict);

        b.ToTable(t => t.HasCheckConstraint("ck_refunds_amount_positive", "amount_minor > 0"));
    }
}

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.EventType).HasMaxLength(64);
        b.Property(x => x.PayloadJson).HasColumnName("payload").HasColumnType("jsonb");
        b.HasIndex(x => x.CreatedAt).HasFilter("dispatched_at IS NULL");
    }
}

internal sealed class CallbackTokenConfiguration : IEntityTypeConfiguration<CallbackToken>
{
    public void Configure(EntityTypeBuilder<CallbackToken> b)
    {
        b.HasKey(x => x.Token);
        b.Property(x => x.Token).HasMaxLength(64).ValueGeneratedNever();
        b.Property(x => x.ConnectorKey).HasMaxLength(40);
        b.HasIndex(x => x.PaymentAttemptId);

        b.HasOne<PaymentAttempt>().WithMany()
            .HasForeignKey(x => x.PaymentAttemptId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class IdempotencyRecordConfiguration : IEntityTypeConfiguration<IdempotencyRecord>
{
    public void Configure(EntityTypeBuilder<IdempotencyRecord> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Key).HasMaxLength(255);
        b.Property(x => x.Endpoint).HasMaxLength(200);
        b.Property(x => x.RequestHash).HasMaxLength(64);
        b.Property(x => x.ResponseBody).HasColumnType("jsonb");

        // Yarışı ÇÖZEN kısıt: iki eşzamanlı istek aynı anahtarla gelirse biri
        // 23505 alır ve ilkinin sonucunu okur. Uygulama içi kontrol yeterli olmazdı.
        b.HasIndex(x => new { x.TenantId, x.Key }).IsUnique();
    }
}
