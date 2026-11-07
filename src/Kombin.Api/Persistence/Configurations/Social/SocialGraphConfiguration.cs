using Kombin.Api.Persistence.Entities.Identity;
using Kombin.Api.Persistence.Entities.Social;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kombin.Api.Persistence.Configurations.Social;

internal sealed class FollowConfiguration : IEntityTypeConfiguration<Follow>
{
    public void Configure(EntityTypeBuilder<Follow> builder)
    {
        builder.ToTable("follows", "social", table => table.HasCheckConstraint("ck_follows_not_self", "follower_id <> followee_id"));

        // The key serves "whom do I follow" (the feed); the second index "who follows them" (counts).
        builder.HasKey(follow => new { follow.FollowerId, follow.FolloweeId });
        builder.HasIndex(follow => follow.FolloweeId);

        builder.HasOne<User>().WithMany().HasForeignKey(follow => follow.FollowerId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<User>().WithMany().HasForeignKey(follow => follow.FolloweeId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class UserBlockConfiguration : IEntityTypeConfiguration<UserBlock>
{
    public void Configure(EntityTypeBuilder<UserBlock> builder)
    {
        builder.ToTable("blocks", "social", table => table.HasCheckConstraint("ck_blocks_not_self", "blocker_id <> blocked_id"));

        // Both directions are checked on every feed query: the key covers "whom I blocked", the index "who blocked me".
        builder.HasKey(block => new { block.BlockerId, block.BlockedId });
        builder.HasIndex(block => block.BlockedId);

        builder.HasOne<User>().WithMany().HasForeignKey(block => block.BlockerId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<User>().WithMany().HasForeignKey(block => block.BlockedId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class PostLikeConfiguration : IEntityTypeConfiguration<PostLike>
{
    public void Configure(EntityTypeBuilder<PostLike> builder)
    {
        builder.ToTable("likes", "social");

        builder.HasKey(like => new { like.PostId, like.UserId });
        builder.HasIndex(like => like.UserId);

        builder.HasOne<Post>().WithMany().HasForeignKey(like => like.PostId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<User>().WithMany().HasForeignKey(like => like.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class PostReportConfiguration : IEntityTypeConfiguration<PostReport>
{
    public void Configure(EntityTypeBuilder<PostReport> builder)
    {
        builder.ToTable("reports", "social");

        builder.HasKey(report => report.Id);
        builder.Property(report => report.Id).ValueGeneratedNever();
        builder.Property(report => report.Reason).HasConversion<string>().HasMaxLength(20);

        builder.HasIndex(report => new { report.PostId, report.ReporterId }).IsUnique();

        builder.HasOne<Post>().WithMany().HasForeignKey(report => report.PostId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<User>().WithMany().HasForeignKey(report => report.ReporterId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class PostingConsentRecordConfiguration : IEntityTypeConfiguration<PostingConsentRecord>
{
    public void Configure(EntityTypeBuilder<PostingConsentRecord> builder)
    {
        builder.ToTable("posting_consents", "social");

        builder.HasKey(consent => consent.Id);
        builder.Property(consent => consent.Id).UseIdentityAlwaysColumn();
        builder.Property(consent => consent.Version).HasMaxLength(40);
        builder.Property(consent => consent.Text).HasMaxLength(4000);

        builder.HasIndex(consent => new { consent.UserId, consent.Version });

        builder.HasOne<User>().WithMany().HasForeignKey(consent => consent.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
