using Lancamentos.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Lancamentos.Api.Data;

public sealed class LancamentosDbContext(DbContextOptions<LancamentosDbContext> options) : DbContext(options)
{
    public DbSet<Lancamento> Lancamentos => Set<Lancamento>();
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Lancamento>(e =>
        {
            e.ToTable("lancamentos");
            e.HasKey(l => l.Id);
            e.Property(l => l.Tipo).HasConversion<string>().HasMaxLength(10);
            e.Property(l => l.Valor).HasPrecision(18, 2);
            e.Property(l => l.Descricao).HasMaxLength(Lancamento.DescricaoTamanhoMaximo);
            e.Property(l => l.IdempotencyKey).HasMaxLength(Lancamento.IdempotencyKeyTamanhoMaximo);
            e.Property(l => l.CriadoPor).HasMaxLength(Lancamento.CriadoPorTamanhoMaximo);
            // A Idempotency-Key é única por cliente: dois clientes podem usar a mesma chave sem conflito,
            // e um cliente nunca recebe o lançamento de outro por acaso.
            e.HasIndex(l => new { l.CriadoPor, l.IdempotencyKey }).IsUnique();
            e.HasIndex(l => l.Data);
        });

        modelBuilder.Entity<OutboxMessage>(e =>
        {
            e.ToTable("outbox");
            e.HasKey(o => o.Id);
            e.Property(o => o.Tipo).HasMaxLength(100);
            e.Property(o => o.Payload).HasColumnType("jsonb");
            // Índice parcial: o publicador só procura eventos ainda não publicados.
            e.HasIndex(o => o.CriadoEm).HasFilter("publicado_em IS NULL");
        });
    }
}
