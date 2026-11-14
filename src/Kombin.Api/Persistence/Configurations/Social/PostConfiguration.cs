using Kombin.Api.Persistence.Entities.Identity;
using Kombin.Api.Persistence.Entities.Social;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kombin.Api.Persistence.Configurations.Social;

internal sealed class PostConfiguration : IEntityTypeConfiguration<Post>
{
    public void Configure(EntityTypeBuilder<Post> builder)
    {
        builder.ToTable("posts", "social", table =>
        {
            table.HasCheckConstraint("ck_posts_counts", "like_count >= 0 AND report_count >= 0");
            table.HasCheckConstraint("ck_posts_caption", $"char_length(caption) <= {Post.MaxCaptionLength}");
        });

        builder.HasKey(post => post.Id);
        builder.Property(post => post.Id).ValueGeneratedNever();

        builder.Property(post => post.Kind).HasConversion<string>().HasMaxLength(20);
        builder.Property(post => post.Visibility).HasConversion<string>().HasMaxLength(20);
        builder.Property(post => post.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(post => post.StatusBeforeDeletion).HasConversion<string>().HasMaxLength(20);
        builder.Property(post => post.Audience).HasConversion<string>().HasMaxLength(20);
        builder.Property(post => post.ModerationVerdict).HasConversion<string>().HasMaxLength(20);
        builder.Property(post => post.Caption).HasMaxLength(Post.MaxCaptionLength);
        builder.Property(post => post.ConsentVersion).HasMaxLength(40);
        builder.Property(post => post.ModerationJson).HasColumnType("jsonb");
        builder.Property(post => post.ModerationReason).HasMaxLength(Post.MaxModerationReasonLength);
        builder.Property(post => post.ContentType).HasMaxLength(40);
        builder.Property(post => post.ImageOriginalKey).HasMaxLength(300);
        builder.Property(post => post.ImageFeedKey).HasMaxLength(300);
        builder.Property(post => post.ImagePreviewKey).HasMaxLength(300);
        builder.Property(post => post.FailureReason).HasMaxLength(300);

        // For you: live public posts, recent first. The SQL ranking reads the 14-day window of this index.
        builder.HasIndex(post => post.PublishedAt)
            .IsDescending()
            .HasDatabaseName("ix_posts_feed_window")
            .HasFilter("status = 'Live' AND visibility = 'Public'");

        // Following feed and profiles: an author's live posts, newest first.
        builder.HasIndex(post => new { post.AuthorId, post.PublishedAt })
            .IsDescending(false, true)
            .HasDatabaseName("ix_posts_author_live")
            .HasFilter("status = 'Live'");

        // My posts (every status), and the purge job's scan of tombstones and stale drafts.
        builder.HasIndex(post => new { post.AuthorId, post.CreatedAt });
        builder.HasIndex(post => new { post.Status, post.UpdatedAt });

        builder.HasMany(post => post.Tags)
            .WithOne()
            .HasForeignKey(tag => tag.PostId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(post => post.AuthorId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
