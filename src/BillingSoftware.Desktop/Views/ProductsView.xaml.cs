using System.Windows;
using System.Windows.Controls;
using BillingSoftware.Desktop.ViewModels;

namespace BillingSoftware.Desktop.Views;

/// <summary>The Items tab: a full-width table. New item and choosing an item open the edit dialog.</summary>
public partial class ProductsView : UserControl
{
    private readonly ProductsViewModel _vm;
    private ProductEditWindow? _dialog;

    /// <summary>The layout check sets this so choosing an item does not open a blocking dialog.</summary>
    public bool SuppressDialog { get; set; }

    public ProductsView(ProductsViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        _vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ProductsViewModel.IsFormOpen) && _vm.IsFormOpen) Dispatcher.BeginInvoke(new Action(OpenDialog)); };
        Loaded += async (_, _) => await _vm.LoadAsync();
    }

    /// <summary>The dialog that is open now, if any (used by the layout check).</summary>
    public ProductEditWindow? Dialog => _dialog;

    private void OpenDialog()
    {
        if (_dialog != null || !_vm.IsFormOpen || SuppressDialog) return;
        var dialog = new ProductEditWindow(_vm) { Owner = Window.GetWindow(this) };
        _dialog = dialog;
        try { dialog.ShowDialog(); }
        finally
        {
            _dialog = null;
            if (_vm.IsFormOpen) _vm.CloseFormCommand.Execute(null);      // closed some other way (Alt+F4)
        }
    }

    /// <summary>For the layout check: opens the dialog without blocking.</summary>
    public ProductEditWindow ShowDialogForCheck()
    {
        var dialog = new ProductEditWindow(_vm) { Owner = Window.GetWindow(this) };
        _dialog = dialog;
        dialog.Show();
        return dialog;
    }
}
