using System.Windows;
using System.Windows.Controls;
using BillingSoftware.Desktop.ViewModels;

namespace BillingSoftware.Desktop.Views;

public partial class GstView : UserControl
{
    public GstView(GstViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Loaded += async (_, _) => { ApplyHeight(); if (!vm.HasReport) await vm.PrepareCommand.ExecuteAsync(null); };
    }

    private void Scroller_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyHeight();

    /// <summary>The page fills the window, but on a short screen the tables keep a usable height and the page scrolls.</summary>
    private void ApplyHeight() => Root.Height = Math.Max(660, Scroller.ActualHeight - Root.Margin.Top - Root.Margin.Bottom);
}
