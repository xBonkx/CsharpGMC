using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class MetalWarehouseRulesTests
{
    private const string Category = "Бухты";
    private const string Material = "0,50 · 1250";
    private const string Place = "Склад";
    private static readonly long Owner = 51;
    private static readonly long Other = 52;

    private static MetalMovement Row(string kind,long area,long reservation=0,
        string source="",string destination="",long? obj=null,int pieces=0) =>
        new(1,1,Category,Material,kind,area,pieces,reservation,
            source,destination,obj,obj is null ? "" : "Объект", "engineer","");

    private static MetalOperation Op(string kind,decimal sqm,long? obj=null,int pieces=0,
        string from="",string to="",string note="") =>
        new(Category,Material,kind,sqm,pieces,from,to,obj,"",note,Guid.NewGuid());

    private static IReadOnlyList<MetalMovement> Initial(int area=10000,int pieces=10) =>
        [Row("Начальный запас",area,destination:Place,pieces:pieces)];

    private static MetalPrepared Prepare(MetalOperation op,IReadOnlyList<MetalMovement> history) =>
        MetalWarehouseRules.Prepare(op,history,op.ObjectId is not null);

    [Theory]
    [InlineData("0.001",1)]
    [InlineData("0.0005",1)]
    [InlineData("1.2345",1235)]
    [InlineData("-0.0005",-1)]
    [InlineData("10.001",10001)]
    public void Area_UsesExactDecimalThousandths(string text,long expected) =>
        Assert.Equal(expected,MetalWarehouseRules.Units(decimal.Parse(text,System.Globalization.CultureInfo.InvariantCulture)));

    [Fact] public void PiecesMustBeIntegralAndNonNegative()
    {
        Assert.Throws<ArgumentException>(()=>MetalWarehouseRules.Pieces(0.5m));
        Assert.Throws<ArgumentException>(()=>MetalWarehouseRules.Pieces(-1m));
        Assert.Throws<ArgumentException>(()=>MetalWarehouseRules.Pieces(10_000_001m));
        Assert.Equal(12,MetalWarehouseRules.Pieces(12m));
    }

    [Fact] public void Receipt_AddsToTarget()
    {
        var result=Prepare(Op("Приход",2m,to:Place,pieces:2),Initial());
        Assert.Equal(2000,result.Area);
        Assert.Equal(2,result.Pieces);
        Assert.Equal(Place,result.Destination);
    }

    [Fact] public void Transfer_DoesNotAllowNegativeOrigin()
    {
        Assert.Throws<ArgumentException>(()=>Prepare(Op("Перемещение",11m,from:Place,to:"Станки",pieces:1),Initial()));
        var result=Prepare(Op("Перемещение",9m,from:Place,to:"Станки",pieces:9),Initial());
        Assert.Equal(9000,result.Area);
    }

    [Fact] public void Reservations_DoNotMovePhysicalStock()
    {
        var result=Prepare(Op("Резерв",3m,Owner,from:Place),Initial());
        Assert.Equal(0,result.Area);
        Assert.Equal(3000,result.Reservation);
        Assert.Equal(Place,result.Source);
    }

    [Fact] public void AnotherObject_CannotSpendHeldStock()
    {
        var rows=Initial().Append(Row("Резерв",0,8000,source:Place,obj:Owner)).ToList();
        Assert.Throws<ArgumentException>(()=>Prepare(Op("Выдача",3m,Other,from:Place,pieces:1),rows));
        var own=Prepare(Op("Выдача",8m,Owner,from:Place,pieces:8),rows);
        Assert.Equal(-8000,own.Reservation);
        Assert.Equal(8000,own.Area);
    }

    [Fact] public void IssuePartiallyReleasesOwnReservation()
    {
        var rows=Initial().Append(Row("Резерв",0,3000,source:Place,obj:Owner)).ToList();
        var result=Prepare(Op("Выдача",2m,Owner,from:Place,pieces:2),rows);
        Assert.Equal(-2000,result.Reservation);
    }

    [Fact] public void CannotRemoveMoreReservationThanOwned()
    {
        var rows=Initial().Append(Row("Резерв",0,1000,source:Place,obj:Owner)).ToList();
        Assert.Throws<ArgumentException>(()=>Prepare(Op("Снять резерв",2m,Owner,from:Place),rows));
        var result=Prepare(Op("Снять резерв",1m,Owner,from:Place),rows);
        Assert.Equal(-1000,result.Reservation);
        Assert.Equal(0,result.Area);
    }

    [Fact] public void NeedsAreNewTotals_NotIncrementalAdditions()
    {
        var rows=Initial().Append(Row("Потребность",1500,obj:Owner)).ToList();
        var change=Prepare(Op("Потребность",2m,Owner),rows);
        Assert.Equal(500,change.Area);
        Assert.Throws<ArgumentException>(()=>Prepare(Op("Потребность",1.5m,Owner),rows));
    }

    [Fact] public void ReturnCannotExceedPreviouslyIssued()
    {
        var rows=Initial().Append(Row("Выдача",3000,source:Place,obj:Owner,pieces:3)).ToList();
        Assert.Throws<ArgumentException>(()=>Prepare(Op("Возврат",4m,Owner,to:Place,pieces:2),rows));
        Assert.Throws<ArgumentException>(()=>Prepare(Op("Возврат",2m,Owner,to:Place,pieces:4),rows));
        Assert.Equal(2000,Prepare(Op("Возврат",2m,Owner,to:Place,pieces:2),rows).Area);
    }

    [Fact] public void ReturnCannotComeStraightFromSupplier()
    {
        Assert.Throws<ArgumentException>(()=>Prepare(Op("Возврат",1m,Owner,to:MetalWarehouseRules.SupplierPlace),Initial()));
    }

    [Fact] public void CoilCountCannotExceedAvailablePieces()
    {
        Assert.Throws<ArgumentException>(()=>Prepare(Op("Выдача",1m,Owner,from:Place,pieces:11),Initial()));
        Assert.Throws<ArgumentException>(()=>Prepare(Op("Списание",10m,from:Place,pieces:0),Initial()));
    }

    [Fact] public void UnpaidSupplierStockIsUnavailableForIssuance()
    {
        var rows=Initial().Append(Row("Оплата партии",5000,destination:MetalWarehouseRules.SupplierPlace,pieces:5)).ToList();
        Assert.Throws<ArgumentException>(()=>Prepare(Op("Выдача",1m,Owner,from:MetalWarehouseRules.SupplierPlace,pieces:1),rows));
        Assert.Throws<ArgumentException>(()=>Prepare(Op("Резерв",1m,Owner,from:MetalWarehouseRules.SupplierPlace),rows));
    }

    [Fact] public void AdjustmentMustHaveReasonAndNegativeValue()
    {
        Assert.Throws<ArgumentException>(()=>Prepare(Op("Корректировка",-1m,to:Place),Initial()));
        var negative=Prepare(Op("Корректировка",-1m,to:Place,note:"Инвентаризация"),Initial());
        Assert.Equal(1000,negative.Area);
        Assert.Equal(Place,negative.Source);
        Assert.Equal("",negative.Destination);
    }

    [Fact] public void OpeningRejectsNegativeStockAndDemand()
    {
        Assert.Throws<ArgumentException>(()=>MetalWarehouseRules.ValidateOpening(
        [
            new MetalOpening("Бухты","0.7",[(Place,-0.001m)],[],null,[])
        ]));
        Assert.Throws<ArgumentException>(()=>MetalWarehouseRules.ValidateOpening(
        [
            new MetalOpening("Бухты","0.7",[(Place,0m)],[("Объект",-0.001m)],null,[])
        ]));
    }

    [Theory]
    [InlineData("Server=localhost;Database=master;Integrated Security=true")]
    [InlineData("Server=localhost;Database=RealGmcSmeta;Integrated Security=true")]
    [InlineData("Server=localhost;Database=GmcSmetaProduction;Integrated Security=true")]
    public void SQLWarehouseNeverOpensNonPilotDatabase(string connection) =>
        Assert.Throws<InvalidOperationException>(()=>new SqlServerMetalWarehouse(connection));

    [Fact] public void OpeningKeepsZeroStockMaterial()
    {
        var source=new MetalOpening("Штрипсы","(0,7)",[("Цех Выселковая",0m)],[],null,[]);
        MetalWarehouseRules.ValidateOpening([source]);
        Assert.Equal(0,MetalWarehouseRules.Units(source.Stock[0].Area));
    }
}
