using System.Windows;
using System.Windows.Input;

namespace CncPrieniky3D;

public partial class MarkContactDialog : Window
{
    public string Oznacenie => LabelBox.Text ?? "";

    public MarkContactDialog(string partA, string partB, string proposed)
    {
        InitializeComponent();
        InfoText.Text = $"{partA}  ↔  {partB}";
        LabelBox.Text = proposed;
        LabelBox.SelectAll();
        Loaded += (_, _) => LabelBox.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void LabelBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            DialogResult = true;
            Close();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            DialogResult = false;
            Close();
            e.Handled = true;
        }
    }
}
