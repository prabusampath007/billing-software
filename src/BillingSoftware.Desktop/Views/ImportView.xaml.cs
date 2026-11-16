using System.Windows;
using System.Windows.Controls;
using BillingSoftware.Desktop.Services;
using BillingSoftware.Desktop.ViewModels;

namespace BillingSoftware.Desktop.Views;

public partial class ImportView : UserControl
{
    private readonly ImportViewModel _vm;

    public ImportView(ImportViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        _vm.ColumnsChanged += BuildColumns;
        _vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ImportViewModel.IsPanelOpen)) ApplyLayout(); };
        BuildColumns();
        Loaded += (_, _) => ApplyLayout();
    }

    /// <summary>The table shows the file's own columns (name, price, GST ... for items), then what will happen to each row.</summary>
    private void BuildColumns()
    {
        ResultGrid.Columns.Clear();
        ResultGrid.Columns.Add(new DataGridTextColumn { Header = "Row", Binding = new System.Windows.Data.Binding("RowText"), Width = 56, ElementStyle = (Style)FindResource("RightCell") });
        for (var i = 0; i < _vm.Columns.Count; i++)
        {
            var title = _vm.Columns[i];
            var wide = title is "Name" or "Tamil name";
            var numeric = title is "Price" or "MRP" or "GST %" or "Opening stock" or "Opening cost" or "Credit limit" or "Opening due" or "Opening payable";
            var column = new DataGridTextColumn { Header = title, Binding = new System.Windows.Data.Binding($"Cells[{i}]"), Width = wide ? 190 : title is "Barcode" or "GSTIN" ? 140 : 100 };
            if (numeric) column.ElementStyle = (Style)FindResource("RightCell");
            ResultGrid.Columns.Add(column);
        }
        ResultGrid.Columns.Add(new DataGridTextColumn { Header = "Result", Binding = new System.Windows.Data.Binding("StatusText"), Width = 110 });
        ResultGrid.Columns.Add(new DataGridTextColumn { Header = "Details", Binding = new System.Windows.Data.Binding("Detail"), Width = 360 });
    }

    private void Scroller_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyLayout();

    private void ApplyLayout() => Adaptive.Apply(Scroller, Root, ListPane, FormPane, SecondColumn, FirstRow, secondWidth: 380, secondVisible: _vm.IsPanelOpen);
}
