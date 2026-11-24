using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class MetalLotRulesTests
{
    [Fact]
    public void SupplierOrder_RequiresPositiveCoilsAndArea()
    {
        Assert.Throws<ArgumentException>(()=>MetalLotRules.ValidateOrder("Штрипсы",10m,4m));
        Assert.Throws<ArgumentException>(()=>MetalLotRules.ValidateOrder("Бухты",0m,4m));
        Assert.Throws<ArgumentException>(()=>MetalLotRules.ValidateOrder("Бухты",10m,0m));
        Assert.Throws<ArgumentException>(()=>MetalLotRules.ValidateOrder("Бухты",10m,4.2m));
        MetalLotRules.ValidateOrder("Бухты",10m,4m);
    }

    [Fact] public void SupplierPayment_CannotBeRecordedTwice()
    {
        MetalLotRules.ValidatePayment(false);
        Assert.Throws<InvalidOperationException>(()=>MetalLotRules.ValidatePayment(true));
    }

    [Fact] public void SupplierReceipt_RequiresPriorPayment()
    {
        Assert.Throws<InvalidOperationException>(()=>
            MetalLotRules.ValidateReceipt(false,"Склад",10000,4,0,0,5000,2));
    }

    [Fact] public void SupplierReceipt_CannotUseSupplierAsPhysicalDestination()
    {
        Assert.Throws<ArgumentException>(()=>
            MetalLotRules.ValidateReceipt(true,"У поставщиков",10000,4,0,0,5000,2));
    }

    [Fact] public void SupplierReceipt_SupportsPartialAndFinalReceipt()
    {
        MetalLotRules.ValidateReceipt(true,"Склад",10000,4,0,0,4000,1);
        MetalLotRules.ValidateReceipt(true,"Цех Выселковая",10000,4,4000,1,6000,3);
    }

    [Fact] public void SupplierReceipt_RejectsAreaAndPieceOverflow()
    {
        Assert.Throws<ArgumentException>(()=>
            MetalLotRules.ValidateReceipt(true,"Склад",10000,4,4000,1,7000,2));
        Assert.Throws<ArgumentException>(()=>
            MetalLotRules.ValidateReceipt(true,"Склад",10000,4,4000,1,6000,4));
    }

    [Fact] public void SupplierReceipt_RejectsPartialZeroRemainderInOnlyOneUnit()
    {
        Assert.Throws<ArgumentException>(()=>
            MetalLotRules.ValidateReceipt(true,"Склад",10000,4,4000,1,6000,2));
        Assert.Throws<ArgumentException>(()=>
            MetalLotRules.ValidateReceipt(true,"Склад",10000,4,4000,1,5000,3));
    }

    [Fact] public void SupplierReceipt_CannotRepeatFullReceipt()
    {
        Assert.Throws<ArgumentException>(()=>
            MetalLotRules.ValidateReceipt(true,"Склад",10000,4,10000,4,1000,1));
    }

    [Fact] public void SimulatedSecondIssue_RecognizesAlreadySpentArea()
    {
        var initial=new MetalMovement(1,1,"Бухты","0,5","Начальный запас",
            10000,10,0,"","Склад",null,"","worker","");
        var input=new MetalOperation("Бухты","0,5","Выдача",7m,7,"Склад","",3,"",
            "",Guid.NewGuid());
        var first=MetalWarehouseRules.Prepare(input,[initial],true);
        Assert.Equal(7000,first.Area);
        var debited=new MetalMovement(2,1,"Бухты","0,5","Выдача",7000,7,
            0,"Склад","",3,"Obj","worker","");
        Assert.Throws<ArgumentException>(()=>
            MetalWarehouseRules.Prepare(input,[initial,debited],true));
    }
}
