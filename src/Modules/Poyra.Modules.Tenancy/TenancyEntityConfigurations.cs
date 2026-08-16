using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Poyra.Modules.Tenancy.Domain;
using Poyra.SharedKernel.Tenancy;

namespace Poyra.Modules.Tenancy;

internal sealed class OrganizationConfiguration : IEntityTypeConfiguration<Organization>
{
    public void Configure(EntityTypeBuilder<Organization> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Name).HasMaxLength(200);
    }
}

internal sealed class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.Slug).HasMaxLength(64);
        b.HasIndex(x => x.Slug).IsUnique();
        b.Property(x => x.Status)
            .HasConversion(s => s.ToString().ToLowerInvariant(), s => Enum.Parse<TenantStatus>(s, true))
            .HasMaxLength(16);
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class BusinessProfileConfiguration : IEntityTypeConfiguration<BusinessProfile>
{
    public void Configure(EntityTypeBuilder<BusinessProfile> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Name).HasMaxLength(200);
        b.HasIndex(x => x.TenantId);
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Email).HasMaxLength(320);
        b.HasIndex(x => x.Email).IsUnique();
        b.Property(x => x.PasswordHash).HasMaxLength(500);
        b.Property(x => x.DisplayName).HasMaxLength(200);
    }
}

internal sealed class UserTenantConfiguration : IEntityTypeConfiguration<UserTenant>
{
    public void Configure(EntityTypeBuilder<UserTenant> b)
    {
        b.HasKey(x => new { x.UserId, x.TenantId });
        b.Property(x => x.Role)
            .HasConversion(r => TenantRoleMap.ToDb[r], r => TenantRoleMap.FromDb[r])
            .HasMaxLength(16);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.TenantId);
    }
}

internal sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.TokenHash).HasMaxLength(128);
        b.HasIndex(x => x.TokenHash).IsUnique();
        b.Property(x => x.ReplacedByHash).HasMaxLength(128);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.UserId, x.ExpiresAt });
    }
}

internal sealed class UserTokenConfiguration : IEntityTypeConfiguration<UserToken>
{
    public void Configure(EntityTypeBuilder<UserToken> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.TokenHash).HasMaxLength(128); // SHA-512 hex
        b.HasIndex(x => x.TokenHash).IsUnique();
        b.Property(x => x.Purpose)
            .HasConversion(p => UserTokenPurposeMap.ToDb[p], p => UserTokenPurposeMap.FromDb[p])
            .HasMaxLength(24);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.UserId, x.Purpose });
    }
}

internal sealed class EmailMessageRecordConfiguration : IEntityTypeConfiguration<EmailMessageRecord>
{
    public void Configure(EntityTypeBuilder<EmailMessageRecord> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.ToEmail).HasMaxLength(320);
        b.Property(x => x.Subject).HasMaxLength(300);
        b.Property(x => x.Purpose).HasMaxLength(40);
        b.Property(x => x.Status)
            .HasConversion(s => EmailStatusMap.ToDb[s], s => EmailStatusMap.FromDb[s])
            .HasMaxLength(16);
        b.HasIndex(x => new { x.Status, x.CreatedAt });
    }
}

internal sealed class SmsMessageRecordConfiguration : IEntityTypeConfiguration<SmsMessageRecord>
{
    public void Configure(EntityTypeBuilder<SmsMessageRecord> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.ToPhone).HasMaxLength(20);
        b.Property(x => x.Body).HasMaxLength(1600); // 10 parça üst sınırı
        b.Property(x => x.Purpose).HasMaxLength(40);
        b.Property(x => x.ProviderMessageId).HasMaxLength(100);
        b.Property(x => x.Status)
            .HasConversion(s => EmailStatusMap.ToDb[s], s => EmailStatusMap.FromDb[s])
            .HasMaxLength(16);
        b.HasIndex(x => new { x.Status, x.CreatedAt });
        b.HasIndex(x => new { x.TenantId, x.CreatedAt }); // kredi raporu
    }
}

internal sealed class TenantBrandingConfiguration : IEntityTypeConfiguration<TenantBranding>
{
    public void Configure(EntityTypeBuilder<TenantBranding> b)
    {
        b.HasKey(x => x.TenantId);
        b.Property(x => x.TenantId).ValueGeneratedNever();
        b.Property(x => x.DisplayName).HasMaxLength(120);
        b.Property(x => x.PrimaryColor).HasMaxLength(7);
        b.Property(x => x.LogoContentType).HasMaxLength(64);
        b.Property(x => x.SupportEmail).HasMaxLength(320);
        b.Property(x => x.SupportPhone).HasMaxLength(32);
        b.Property(x => x.CheckoutDomain).HasMaxLength(253);
        b.HasIndex(x => x.CheckoutDomain).IsUnique().HasFilter("checkout_domain IS NOT NULL");
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ApiKeyConfiguration : IEntityTypeConfiguration<ApiKey>
{
    public void Configure(EntityTypeBuilder<ApiKey> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Name).HasMaxLength(100);
        b.Property(x => x.PrefixHint).HasMaxLength(16);
        b.Property(x => x.KeyHash).HasMaxLength(128); // SHA-512 hex
        b.HasIndex(x => x.KeyHash).IsUnique();
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
    }
}
