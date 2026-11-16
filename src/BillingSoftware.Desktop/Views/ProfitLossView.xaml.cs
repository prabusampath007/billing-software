using System.Windows;
using System.Windows.Controls;
using BillingSoftware.Desktop.ViewModels;

namespace BillingSoftware.Desktop.Views;

public partial class ProfitLossView : UserControl
{
    public ProfitLossView(ProfitLossViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Loaded += async (_, _) => { ApplyHeight(); if (!vm.HasReport) await vm.PrepareAsync(); };
    }

    private void Scroller_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyHeight();

    private void ApplyHeight() => Root.Height = Math.Max(660, Scroller.ActualHeight - Root.Margin.Top - Root.Margin.Bottom);
}
