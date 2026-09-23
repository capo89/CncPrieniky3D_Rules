using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CncPrieniky3D.Models;

/// <summary>Uzol v strome zoznamu dielov (korpus / šufel / sekcia → deti).</summary>
public sealed class PartTreeNode : INotifyPropertyChanged
{
    private bool _isExpanded;
    private bool _isSelected;

    public PartTreeNode(DielecModel dielec)
    {
        Dielec = dielec;
    }

    /// <summary>Skupinový uzol bez dielca (napr. „čista miera“).</summary>
    public PartTreeNode(string sectionTitle)
    {
        SectionTitle = sectionTitle;
        // Zbalené — aby zoznam nebol zbytočne dlhý / „vyskrolovaný“.
        IsExpanded = false;
    }

    public DielecModel? Dielec { get; }

    public string? SectionTitle { get; }

    public bool IsSection => !string.IsNullOrEmpty(SectionTitle);

    public ObservableCollection<PartTreeNode> Children { get; } = new();

    public string Display => IsSection
        ? SectionTitle!
        : Dielec!.ToString();

    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (_isExpanded == value) return; _isExpanded = value; OnPropertyChanged(); }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected == value) return; _isSelected = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void RefreshDisplay() => OnPropertyChanged(nameof(Display));
}
