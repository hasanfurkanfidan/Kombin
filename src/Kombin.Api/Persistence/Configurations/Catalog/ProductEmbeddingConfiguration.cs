using Kombin.Api.Persistence.Entities.Catalog;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kombin.Api.Persistence.Configurations.Catalog;

internal sealed class ProductEmbeddingConfiguration : IEntityTypeConfiguration<ProductEmbedding>
{
    /// <summary>HNSW graph degree and build-time candidate list; the reasoning is in <c>docs/design/visual-search.md</c> ("Index").</summary>
    public const int HnswM = 16;

    public const int HnswEfConstruction = 64;

    public void Configure(EntityTypeBuilder<ProductEmbedding> builder)
    {
        builder.ToTable("product_embeddings", "catalog");

        builder.HasKey(embedding => embedding.ProductId);
        builder.Property(embedding => embedding.ProductId).ValueGeneratedNever();

        builder.Property(embedding => embedding.Embedding).HasColumnType($"vector({ProductEmbedding.Dimensions})");
        builder.Property(embedding => embedding.Model).HasMaxLength(100).IsRequired();
        builder.Property(embedding => embedding.SourceImageUrl).HasMaxLength(2048).IsRequired();
        builder.Property(embedding => embedding.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(embedding => embedding.FailureReason).HasMaxLength(300);

        // Cosine distance (<=>) on unit vectors. NULL vectors (rows that are not Ready) are not indexed by pgvector.
        builder.HasIndex(embedding => embedding.Embedding)
            .HasDatabaseName("ix_product_embeddings_embedding_hnsw")
            .HasMethod("hnsw")
            .HasOperators("vector_cosine_ops")
            .HasStorageParameter("m", HnswM)
            .HasStorageParameter("ef_construction", HnswEfConstruction);

        // Products that share an image reuse its vector instead of downloading it again.
        builder.HasIndex(embedding => embedding.SourceImageUrl);

        builder.HasOne(embedding => embedding.Product)
            .WithOne()
            .HasForeignKey<ProductEmbedding>(embedding => embedding.ProductId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<ProductImage>()
            .WithMany()
            .HasForeignKey(embedding => embedding.SourceImageId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
