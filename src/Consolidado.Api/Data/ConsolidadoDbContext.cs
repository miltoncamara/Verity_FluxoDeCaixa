using Consolidado.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Consolidado.Api.Data;

public sealed class ConsolidadoDbContext(DbContextOptions<ConsolidadoDbContext> options) : DbContext(options)
{
    public DbSet<SaldoDiario> SaldosDiarios => Set<SaldoDiario>();
    public DbSet<EventoProcessado> EventosProcessados => Set<EventoProcessado>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SaldoDiario>(e =>
        {
            e.ToTable("saldo_diario");
            e.HasKey(s => s.Data);
            // Totais com mais dígitos que o valor de um lançamento, porque acumulam o dia inteiro.
            e.Property(s => s.TotalCreditos).HasPrecision(20, 2);
            e.Property(s => s.TotalDebitos).HasPrecision(20, 2);
            e.Ignore(s => s.Saldo);
        });

        modelBuilder.Entity<EventoProcessado>(e =>
        {
            e.ToTable("eventos_processados");
            e.HasKey(ev => ev.EventoId);
        });
    }
}
