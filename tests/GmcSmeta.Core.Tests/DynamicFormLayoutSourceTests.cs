namespace GmcSmeta.Core.Tests;

public sealed class DynamicFormLayoutSourceTests
{
    private static string Source(string file)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", file)))
            root = root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root!.FullName, "src", "GmcSmeta.App", file));
    }

    [Theory]
    [InlineData("MaterialsMarkupDialog.cs")]
    [InlineData("EstimateDiscountDialog.cs")]
    [InlineData("ActDiscountDialog.cs")]
    [InlineData("CostTransportDialog.cs")]
    [InlineData("KsActPropertiesDialog.cs")]
    [InlineData("SectionPickerDialog.cs")]
    public void FormsUseBoundedBodyAndSeparateFooter(string file)
    {
        var source = Source(file);
        Assert.Contains("DynamicFormLayout.Apply(", source);
        Assert.Contains("DynamicFormLayout.WithFooter(", source);
    }

    [Fact]
    public void MarkupUsesTwoRowsAndOriginalCallbacks()
    {
        var source = Source("MaterialsMarkupDialog.cs");
        Assert.Contains("AddField(\"Коэффициент\", _factor)", source);
        Assert.Contains("AddField(\"или наценка, %\", _percent)", source);
        Assert.Contains("_factor.TextChanged += FactorChanged", source);
        Assert.Contains("_percent.TextChanged += PercentChanged", source);
        Assert.DoesNotContain("FontSize = 17", source);
    }

    [Fact]
    public void DiscountPreviewGrowsAndModeStillCollapsesOptions()
    {
        var source = Source("EstimateDiscountDialog.cs");
        Assert.Contains("LineStackingStrategy.MaxHeight", source);
        Assert.DoesNotContain("LineHeight=24", source);
        Assert.Contains("_smr.Visibility=_whole.Visibility=_onTotal?Visibility.Collapsed:Visibility.Visible", source);
    }

    [Theory]
    [InlineData("ActDiscountDialog.cs", "_save", "LayoutFormStackedFieldGap")]
    [InlineData("EstimateDiscountDialog.cs", "_apply", "LayoutFormColumnGap")]
    [InlineData("CostTransportDialog.cs", "save", "LayoutFormColumnGap")]
    public void FinancialFormsUseResponsiveFieldAndActionRoles(string file, string action, string layoutKey)
    {
        var source = Source(file);
        Assert.Contains("ResizeMode.CanResize", source);
        Assert.Contains("DynamicFormLayout.Field(", source);
        Assert.Contains("new WrapPanel", source);
        Assert.Contains("DynamicFormLayout.StyleActions(" + action + ",", source);
        Assert.Contains(layoutKey, source);
        Assert.Contains("LayoutDialogActionGap", source);
        Assert.Contains("AutomationProperties.SetName(", source);
        Assert.Contains("HorizontalContentAlignment", source);
        Assert.DoesNotContain("new Thickness(", source);
    }

    [Fact]
    public void KsAdditionalAgreementFieldGrowsWithScroll()
    {
        var source = Source("KsActPropertiesDialog.cs");
        Assert.Contains("dsExtra.MinHeight = 64", source);
        Assert.Contains("dsExtra.MaxHeight = 180", source);
        Assert.Contains("dsExtra.VerticalScrollBarVisibility = ScrollBarVisibility.Auto", source);
        Assert.DoesNotContain("dsExtra.Height = 64", source);
    }

    [Fact]
    public void SectionAmountColumnsUseSemanticAlignmentWithoutChangingBindings()
    {
        var source = Source("ActSectionAmountsDialog.cs");
        Assert.Contains("WorkspaceWindowUi.StyleColumn(column,property==nameof(Entry.Name)?TextAlignment.Left:TextAlignment.Right)", source);
        Assert.Contains("Mode=readOnly?BindingMode.OneWay:BindingMode.TwoWay", source);
        Assert.Contains("TableColumnMenu.SetProtect", source);
        Assert.Contains("VirtualizationMode.Recycling", source);
    }

    [Fact]
    public void DynamicFormsRefreshWorkAreaForActualMonitorAndPreserveLocalCaps()
    {
        var source = Source("DynamicFormLayout.cs");
        Assert.Contains("MonitorFromWindow(handle, MonitorDefaultToNearest)", source);
        Assert.Contains("GetMonitorInfo(monitor, ref info)", source);
        Assert.Contains("VisualTreeHelper.GetDpi(window)", source);
        Assert.Contains("window.SourceInitialized +=", source);
        Assert.Contains("window.LocationChanged +=", source);
        Assert.Contains("window.DpiChanged +=", source);
        Assert.Contains("Math.Min(requestedMaxHeight, workArea.Height)", source);
        Assert.Contains("Math.Min(requestedMaxWidth, workArea.Width)", source);
        Assert.Contains("if (fitting) return;", source);
    }

    [Fact]
    public void ChromeUsesResourceBasedTypographyAndSingleFormInset()
    {
        var source = Source("DynamicFormLayout.cs");
        Assert.Contains("WorkspaceWindowUi.ApplyChrome(window)", source);
        Assert.Contains("body.SetResourceReference(FrameworkElement.MarginProperty, \"LayoutNoInset\")", source);
        Assert.Contains("DockPanel.SetDock(footer, Dock.Bottom)", source);
        Assert.Contains("VerticalScrollBarVisibility = ScrollBarVisibility.Auto", source);
        Assert.Contains("AutomationProperties.SetName(control, caption)", source);
    }
}
