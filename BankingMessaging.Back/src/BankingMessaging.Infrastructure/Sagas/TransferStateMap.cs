using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BankingMessaging.Infrastructure.Sagas;

public class TransferStateMap : SagaClassMap<TransferState>
{
    protected override void Configure(EntityTypeBuilder<TransferState> entity, ModelBuilder model)
    {
        entity.Property(x => x.CurrentState).HasMaxLength(64);
        entity.Property(x => x.Amount).HasColumnType("numeric(18,4)");
        entity.Property(x => x.RowVersion).IsRowVersion();
    }
}
