using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BillingSoftware.Desktop.ViewModels;

namespace BillingSoftware.Desktop.Views;

/// <summary>The New / Edit item dialog. It shows the Items screen's view model and closes when the item is saved or cancelled.</summary>
public partial class ProductEditWindow : Window
{
    private const double OneColumnBelow = 760;
    private readonly ProductsViewModel _vm;
    private bool _oneColumn;
    private bool _applied;

    public ProductEditWindow(ProductsViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        _vm.Saved += OnSaved;
        _vm.PropertyChanged += OnVmChanged;
        Closed += (_, _) => { _vm.Saved -= OnSaved; _vm.PropertyChanged -= OnVmChanged; };
        Loaded += (_, _) => FitToOwner();
    }

    /// <summary>The dialog never grows past the window it belongs to, so on a small screen the body scrolls instead.</summary>
    private void FitToOwner()
    {
        if (Owner is { } o && o.ActualWidth > 0 && o.ActualHeight > 0)
        {
            Width = Math.Min(940, Math.Max(MinWidth, o.ActualWidth - 32));
            MaxHeight = Math.Max(MinHeight, o.ActualHeight - 32);
        }
        ApplyColumns();
    }

    private void OnSaved() { if (IsLoaded || IsVisible) Dispatcher.BeginInvoke(new Action(() => _vm.CloseFormCommand.Execute(null))); }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProductsViewModel.IsFormOpen) && !_vm.IsFormOpen) Close();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { _vm.CloseFormCommand.Execute(null); e.Handled = true; }
    }

    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) { try { DragMove(); } catch (InvalidOperationException) { } }
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyColumns();

    private void ApplyColumns()
    {
        var one = ActualWidth > 0 && ActualWidth < OneColumnBelow;
        if (one == _oneColumn && _applied) return;
        _applied = true;
        _oneColumn = one;
        RightColumn.Width = one ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(RightStack, one ? 0 : 1);
        Grid.SetRow(RightStack, one ? 1 : 0);
        LeftStack.Margin = one ? new Thickness(0) : new Thickness(0, 0, 8, 0);
        RightStack.Margin = one ? new Thickness(0) : new Thickness(8, 0, 0, 0);
    }
}
