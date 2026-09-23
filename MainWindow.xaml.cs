using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using CncPrieniky3D.Models;
using CncPrieniky3D.Services;
using HelixToolkit.Wpf;
using Microsoft.Win32;

namespace CncPrieniky3D;

public partial class MainWindow : Window
{
    private ExportDocument? _doc;
    private string? _sourceExcelPath;
    private readonly List<Visual3D> _sceneVisuals = new();
    private bool _suppressContactUi;
    private bool _suppressPartProps;
    private Point _mouseDownPos;
    private bool _mouseMoved;
    private List<KolikSerie>? _kolikyClipboard;
    private bool _suflikPromptOpen;

    public MainWindow()
    {
        InitializeComponent();
        ApplyChromeColors();
        Viewport.MouseLeftButtonDown += Viewport_MouseLeftButtonDown;
        Viewport.MouseMove += Viewport_MouseMove;
        Viewport.MouseLeftButtonUp += Viewport_MouseLeftButtonUp;
    }

    private void VlastnostiMenu_Click(object sender, RoutedEventArgs e)
    {
        var btn = (Button)sender;
        btn.ContextMenu.PlacementTarget = btn;
        btn.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        btn.ContextMenu.IsOpen = true;
    }

    private void TitleMin_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void TitleMax_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
        if (TitleMaxBtn != null)
            TitleMaxBtn.Content = WindowState == WindowState.Maximized ? "🗗" : "🗖";
    }

    private void TitleClose_Click(object sender, RoutedEventArgs e)
        => Close();

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            TitleMax_Click(sender, e);
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void ApplyChromeColors()
    {
        var c = ViewColors.Current;
        Background = new SolidColorBrush(c.WindowBackground);
        MainToolbar.Background = new SolidColorBrush(c.ToolbarBackground);
        Viewport.Background = new SolidColorBrush(c.ViewportBackground);
        ViewportHost.Background = new SolidColorBrush(c.ViewportBackground);
    }

    private void VlastnostiFarby_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ColorsSettingsWindow { Owner = this };
        if (dlg.ShowDialog() != true)
            return;
        ApplyChromeColors();
        RebuildScene();
    }

    private void OpenExcel_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Excel (*.xlsx)|*.xlsx|Všetky súbory (*.*)|*.*",
            Title = "Otvoriť export z CncExporter2026"
        };
        if (dlg.ShowDialog() != true)
            return;

        LoadExcelDocument(dlg.FileName);
    }

    private void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Projekt CncPrieniky3D (*" + ProjectSessionStore.Extension + ")|*" + ProjectSessionStore.Extension
                     + "|UI log (*_ui.log)|*_ui.log|JSON (*.json)|*.json|Všetky súbory (*.*)|*.*",
            Title = "Otvoriť uložený projekt (*.cnc3d.json / *_ui.log)"
        };
        if (dlg.ShowDialog() != true)
            return;

        try
        {
            string picked = dlg.FileName;
            bool fromUiLog = picked.EndsWith("_ui.log", StringComparison.OrdinalIgnoreCase);

            if (fromUiLog)
            {
                string? dir = Path.GetDirectoryName(picked);
                string baseName = Path.GetFileName(picked);
                if (baseName.EndsWith("_ui.log", StringComparison.OrdinalIgnoreCase))
                    baseName = baseName[..^"_ui.log".Length];
                string candidate = Path.Combine(dir ?? ".", baseName + ".xlsx");
                string excel;
                if (File.Exists(candidate))
                    excel = candidate;
                else
                {
                    var pick = new OpenFileDialog
                    {
                        Title = "Nájdi Excel k tomuto _ui.log",
                        Filter = "Excel (*.xlsx)|*.xlsx",
                        InitialDirectory = dir
                    };
                    if (pick.ShowDialog() != true)
                        return;
                    excel = pick.FileName;
                }

                LoadExcelDocument(excel, forceUiLogPath: picked);
                return;
            }

            var session = ProjectSessionStore.TryLoadFile(picked)
                ?? throw new InvalidOperationException("Súbor projektu sa nepodarilo načítať.");

            string? excelPath = session.ExcelPath;
            if (string.IsNullOrWhiteSpace(excelPath) || !File.Exists(excelPath))
            {
                // Excel vedľa .cnc3d.json s rovnakým názvom
                string? dir = Path.GetDirectoryName(picked);
                string baseName = Path.GetFileName(picked);
                if (baseName.EndsWith(ProjectSessionStore.Extension, StringComparison.OrdinalIgnoreCase))
                    baseName = baseName[..^ProjectSessionStore.Extension.Length];
                string candidate = Path.Combine(dir ?? ".", baseName + ".xlsx");
                if (File.Exists(candidate))
                    excelPath = candidate;
                else
                {
                    var pick = new OpenFileDialog
                    {
                        Title = "Nájdi Excel k tomuto projektu",
                        Filter = "Excel (*.xlsx)|*.xlsx",
                        InitialDirectory = dir
                    };
                    if (pick.ShowDialog() != true)
                        return;
                    excelPath = pick.FileName;
                }
            }

            LoadExcelDocument(excelPath, forceSession: session);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Chyba projektu", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveProject_Click(object sender, RoutedEventArgs e)
    {
        if (_doc == null || string.IsNullOrEmpty(_sourceExcelPath))
        {
            MessageBox.Show(this, "Najprv otvor Excel export.", "Uložiť projekt",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            SaveCurrentSession();
            string path = ProjectSessionStore.SessionPathForExcel(_sourceExcelPath);
            StatusText.Text = $"Projekt uložený: {Path.GetFileName(path)}";
            MessageBox.Show(this,
                $"Uložené:\n{path}\n\nNačítať späť cez „Otvoriť projekt…“.",
                "Uložiť projekt", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Chyba uloženia", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveCurrentSession()
    {
        if (_doc == null || string.IsNullOrEmpty(_sourceExcelPath))
            return;
        ProjectSessionStore.Save(_sourceExcelPath, _doc);
        UiInputLogger.Write("SaveProject", ProjectSessionStore.SessionPathForExcel(_sourceExcelPath));
    }

    private void TryAutoSaveSession()
    {
        try { SaveCurrentSession(); }
        catch { /* neblokuj UI */ }
    }

    private void LoadExcelDocument(
        string excelPath,
        ProjectSessionStore.ProjectSessionDto? forceSession = null,
        string? forceUiLogPath = null)
    {
        try
        {
            _doc = ExcelExportLoader.Load(excelPath);
            _sourceExcelPath = excelPath;
            UiInputLogger.SetExcelPath(_sourceExcelPath);
            UiInputLogger.Write("OpenExcel", excelPath);
            _doc.Dotyky.Clear();
            foreach (var c in ContactDetector.Find(_doc.KorpusDiely.ToList()))
                _doc.Dotyky.Add(c);
            SuflikContactBuilder.Attach(_doc);

            // Obnovenie kolíkov/skrutiek len cez „Otvoriť projekt…“ (forceSession / forceUiLogPath).
            ProjectSessionStore.ProjectSessionDto? session = forceSession;
            bool restoredFromUiLog = false;
            if (session != null)
            {
                int n = ProjectSessionStore.Apply(_doc, session);
                UiInputLogger.Write("LoadProject", $"položiek≈{n}; saved={session.SavedAt:yyyy-MM-dd HH:mm}");
            }
            else if (forceUiLogPath != null)
            {
                int n = UiLogSessionReplayer.Apply(_doc, File.ReadAllLines(forceUiLogPath));
                UiInputLogger.Write("LoadUiLog", $"zásahy≈{n}");
                restoredFromUiLog = n > 0;
                if (restoredFromUiLog)
                    TryAutoSaveSession();
            }

            // Po session/log: nohy vždy zapnú DruheUpnutie (viditeľné vo vlastnostiach).
            ExcelExportLoader.EnsureDruheUpnutieForNohy(_doc);

            PartList.ItemsSource = null;
            RefreshPartList(_doc.KorpusDiely.FirstOrDefault() ?? _doc.Diely.FirstOrDefault());
            RefreshContactListUi();

            int body = _doc.Diely.Sum(d => d.Body.Count);
            int plochy = _doc.Diely.Sum(d => d.Plochy.Count);
            int vyrezy = _doc.Diely.Sum(d => d.Vyrezy.Count);
            int cnc = _doc.Diely.Sum(d => d.CncZnacenia.Count);
            int movento = _doc.Diely.Sum(d => d.CncZnacenia.Count(CncZnacenieTyp.IsMovento));
            int nKorpus = _doc.KorpusDiely
                .GroupBy(d => PartRules.CncGroupKey(d, _doc), StringComparer.OrdinalIgnoreCase)
                .Count();
            int nKorpusInst = _doc.KorpusDiely.Count();
            int nSuflikInd = _doc.SuflikDiely.Count(d => !d.JeSuflikPozicia);
            int nPoz = _doc.SuflikDiely.Count(d => d.JeSuflikPozicia);
            int bodySuflik = _doc.SuflikDiely.Where(d => !d.JeSuflikPozicia).Sum(d => d.Body.Count);
            int visibleDotyky = _doc.Dotyky.Count(c => !c.JeSuflikAuto && !PartRules.IsPolicaContact(c));
            int nKol = _doc.Dotyky.Where(c => !c.JeSuflikAuto).Sum(c => c.CelkovyPocetKolikov);
            int nSkr = _doc.Dotyky.Where(c => !c.JeSuflikAuto).Sum(c => c.CelkovyPocetSkrutiek);
            string korpusTxt = nKorpusInst == nKorpus
                ? $"{nKorpus}"
                : $"{nKorpus} ({nKorpusInst} ks)";
            StatusText.Text =
                $"Blok „{_doc.BlockName}“ — korpus {korpusTxt}, šufle {nSuflikInd} dielov ({bodySuflik} bodov)/{nPoz} poz., " +
                $"výrezy {vyrezy}, plochy-body {plochy}, body {body}, CNC {cnc} (movento {movento}), {visibleDotyky} dotykov";
            if (session != null)
                StatusText.Text += $", obnovené kolíky {nKol}× / skrutky {nSkr}×";
            else if (restoredFromUiLog)
                StatusText.Text += $", obnovené z _ui.log: kolíky {nKol}× / skrutky {nSkr}×";
            StatusText.Text += ".";
            int nCncDims = _doc.KorpusDiely.Count(d => d.HasCncRozmery);
            if (nCncDims > 0)
                StatusText.Text += $" | CNC rozmery {nCncDims}×";
            if (_doc.LoadWarnings.Count > 0)
            {
                StatusText.Text += $" | ⚠ AABB≠CNC: {_doc.LoadWarnings.Count}";
                string msg = "Nezhoda AABB (3D) ↔ CNC (pôvodný kusovník):\n\n"
                    + string.Join("\n", _doc.LoadWarnings.Take(20));
                if (_doc.LoadWarnings.Count > 20)
                    msg += $"\n… a ďalších {_doc.LoadWarnings.Count - 20}";
                msg += "\n\n3D ostáva podľa WCS/AABB; obrobok .xcs podľa CNC rozmerov.";
                MessageBox.Show(this, msg, "Kontrola rozmerov", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            UpdateSuflikTabVisibility();
            RebuildScene();
            Viewport.ZoomExtents();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Chyba načítania", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Viewport_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _mouseDownPos = e.GetPosition(Viewport);
        _mouseMoved = false;
    }

    private void Viewport_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(Viewport);
        if (Math.Abs(p.X - _mouseDownPos.X) > 5 || Math.Abs(p.Y - _mouseDownPos.Y) > 5)
            _mouseMoved = true;
    }

    private void Viewport_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_mouseMoved || _doc == null) return;

        var hits = Viewport3DHelper.FindHits(Viewport.Viewport, _mouseDownPos);
        bool hitScene = false;
        foreach (var hit in hits)
        {
            DependencyObject? cur = hit.Visual as DependencyObject;
            while (cur != null)
            {
                if (cur is Visual3D v3 && _sceneVisuals.Contains(v3))
                {
                    hitScene = true;
                    break;
                }
                cur = VisualTreeHelper.GetParent(cur);
            }
            if (hitScene) break;
        }

        if (!hitScene)
        {
            // Klik mimo skrinku / dielce → zruš výber dielca aj plôch.
            ClearViewportSelection();
            e.Handled = true;
            return;
        }

        if (CurrentSceneGroup() == SceneBuilder.SceneGroup.CelaSkrinka) return;
        if (TogContacts.IsChecked != true || RbAssembly.IsChecked != true) return;

        ContactMark? hitContact = null;
        foreach (var hit in hits)
        {
            // Visual môže byť ContactBoxVisual3D alebo jeho rodič
            DependencyObject? cur = hit.Visual as DependencyObject;
            while (cur != null)
            {
                if (cur is ContactBoxVisual3D box && box.Contact != null)
                {
                    hitContact = box.Contact;
                    break;
                }
                cur = cur is Visual3D v3
                    ? VisualTreeHelper.GetParent(v3)
                    : null;
            }
            if (hitContact != null) break;

            // Fallback: podľa stredu hit bodu k najbližšiemu dotyku
        }

        if (hitContact == null && _doc.Dotyky.Count > 0)
        {
            // Hit na mesh dielu blízko stredu styku – nájdi najbližší ContactMark
            Point3D? hitPos = null;
            foreach (var hit in hits)
            {
                hitPos = hit.Position;
                break;
            }
            if (hitPos != null)
            {
                hitContact = _doc.Dotyky
                    .Where(c => !c.JeSuflikAuto && !PartRules.IsPolicaContact(c))
                    .OrderBy(c => (c.Center - hitPos.Value).LengthSquared)
                    .FirstOrDefault(c => (c.Center - hitPos.Value).Length < 40);
            }
        }

        if (hitContact == null)
            return;

        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        if (ctrl)
        {
            // Multi-výber: Ctrl+klik pridá/odoberie plochu
            ToggleContactSelection(hitContact);
            StatusText.Text =
                $"Vybrané plochy: {ContactList.SelectedItems.Count}. Ctrl+klik ďalšie, potom Kolíkovať.";
            e.Handled = true;
            return;
        }

        // Bežný klik = výber plochy (bez dialógu)
        SelectContactInList(hitContact);
        StatusText.Text =
            $"Vybrané: {hitContact.PartA} ↔ {hitContact.PartB}. Ctrl+klik = ďalšie, Kolíkovať = nastavenie.";
        e.Handled = true;
    }

    private void ClearViewportSelection()
    {
        bool hadPart = SelectedDielec() != null;
        bool hadContact = ContactList.SelectedItems.Count > 0;
        if (!hadPart && !hadContact)
            return;

        _suppressContactUi = true;
        ContactList.SelectedItems.Clear();
        _suppressContactUi = false;

        ClearPartTreeSelection(PartList.Items);
        StatusText.Text = "Výber zrušený.";
        RebuildScene();
    }

    private static void ClearPartTreeSelection(System.Collections.IEnumerable items)
    {
        foreach (var item in items)
        {
            if (item is not PartTreeNode n) continue;
            n.IsSelected = false;
            if (n.Children.Count > 0)
                ClearPartTreeSelection(n.Children);
        }
    }

    private void ToggleContactSelection(ContactMark c)
    {
        _suppressContactUi = true;
        if (ContactList.SelectedItems.Contains(c))
            ContactList.SelectedItems.Remove(c);
        else
            ContactList.SelectedItems.Add(c);
        ContactList.ScrollIntoView(c);
        _suppressContactUi = false;
        RebuildScene();
    }

    private void SelectContactInList(ContactMark c)
    {
        _suppressContactUi = true;
        ContactList.SelectedItems.Clear();
        ContactList.SelectedItems.Add(c);
        ContactList.ScrollIntoView(c);
        _suppressContactUi = false;
        RebuildScene();
    }

    private void PromptMarkIn3D(ContactMark c)
    {
        string proposed = string.IsNullOrWhiteSpace(c.Oznacenie) ? $"D{c.Cislo}" : c.Oznacenie;
        var dlg = new MarkContactDialog(c.PartA, c.PartB, proposed)
        {
            Owner = this
        };
        if (dlg.ShowDialog() == true)
        {
            c.Oznacenie = dlg.Oznacenie.Trim();
            c.Oznaceny = !string.IsNullOrWhiteSpace(c.Oznacenie);
            RefreshContactList(c);
            int marked = _doc!.Dotyky.Count(x => x.Oznaceny);
            StatusText.Text = $"V 3D označené: {marked}/{_doc.Dotyky.Count} — „{c.LabelText}“ ({c.PartA} ↔ {c.PartB})";
            RebuildScene();
        }
    }

    private void RefreshContactList(IReadOnlyList<ContactMark> keepSelected)
    {
        _suppressContactUi = true;
        var keep = keepSelected.ToList();
        RefreshContactListUi();
        ContactList.SelectedItems.Clear();
        foreach (var c in keep)
            ContactList.SelectedItems.Add(c);
        _suppressContactUi = false;
    }

    private void RefreshContactList(ContactMark keepSelected)
        => RefreshContactList(new[] { keepSelected });

    private DielecModel? SelectedDielec()
        => (PartList.SelectedItem as PartTreeNode)?.Dielec
           ?? (PartList.SelectedItem as DielecModel);

    private void PartList_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_doc != null && SelectedDielec() is DielecModel d)
        {
            StatusText.Text =
                $"Diel „{d.Nazov}“ — {d.RozmerX:0.##}×{d.RozmerY:0.##}×{d.RozmerZ:0.##} | " +
                $"{BodySolidBuilder.Describe(d)} | CNC {d.CncZnacenia.Count}" +
                (d.OtocitSpodkomHore ? " | spodkom hore" : "");

            bool justSelected = e.NewValue is PartTreeNode n && ReferenceEquals(n.Dielec, d);
            if (!_suflikPromptOpen && d.JeSuflikPozicia && justSelected && IsLoaded)
            {
                // Mimo handleru výberu — inak Refresh/ShowDialog padá (ItemsSource počas SelectedItemChanged).
                var sufel = d;
                Dispatcher.BeginInvoke(new Action(() => PromptSuflikPocetKolikov(sufel)));
            }
        }
        RefreshSelectedPartProps();
        RebuildScene();
    }

    private void PromptSuflikPocetKolikov(DielecModel sufel)
    {
        if (_suflikPromptOpen || !IsLoaded || _doc == null)
            return;
        // Stále ten istý výber?
        if (!ReferenceEquals(SelectedDielec(), sufel))
            return;

        _suflikPromptOpen = true;
        try
        {
            var dlg = new SuflikKolikyDialog(sufel.Nazov, sufel.PocetKolikov)
            {
                Owner = this
            };
            if (dlg.ShowDialog() != true)
                return;

            sufel.PocetKolikov = dlg.PocetKolikov;
            SuflikContactBuilder.RefreshKolikyForPozicia(_doc, sufel);

            // Len text v strome — NEresetovať ItemsSource (to spôsobovalo pád).
            if (PartList.SelectedItem is PartTreeNode node)
                node.RefreshDisplay();

            var sample = _doc.Dotyky.FirstOrDefault(c => c.JeSuflikAuto && c.KolikSerie.Count > 0);
            string roztecTxt = sample != null
                ? $", rozteč {sample.KolikSerie[0].RoztecKolikov:0.##} mm"
                : "";
            StatusText.Text =
                $"Šufel „{sufel.Nazov}“ — {sufel.PocetKolikov}× kolík od vrchu {SuflikContactBuilder.OdVrchuBokMm}{roztecTxt}.";
            RebuildScene();
            TryAutoSaveSession();
        }
        finally
        {
            _suflikPromptOpen = false;
        }
    }

    private void RefreshContactListUi()
    {
        if (_doc == null)
        {
            ContactList.ItemsSource = null;
            return;
        }
        ContactList.ItemsSource = null;
        ContactList.ItemsSource = _doc.Dotyky
            .Where(c => !c.JeSuflikAuto && !PartRules.IsPolicaContact(c))
            .ToList();
    }

    private void PartList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var item = ItemsControl.ContainerFromElement(PartList, e.OriginalSource as DependencyObject) as TreeViewItem;
        if (item != null)
        {
            item.IsSelected = true;
            item.Focus();
        }
    }

    private void PartListContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (PartHideMenuItem == null) return;
        if (SelectedDielec() is DielecModel d)
        {
            PartHideMenuItem.Header = d.JeSkryty ? "Zobraziť diel" : "Skryť diel";
            PartHideMenuItem.IsEnabled = true;
        }
        else
        {
            PartHideMenuItem.Header = "Skryť diel";
            PartHideMenuItem.IsEnabled = false;
        }
    }

    private void PartHide_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedDielec() is not DielecModel d)
            return;

        bool hide = !d.JeSkryty;
        foreach (var inst in SameNamedKorpusInstances(d))
            inst.JeSkryty = hide;

        RefreshPartList(d);
        StatusText.Text = hide
            ? $"„{d.Nazov}“ skrytý v 3D" + (d.PocetKusov > 1 ? $" ({d.PocetKusov} ks)" : "")
            : $"„{d.Nazov}“ zobrazený v 3D";
        RebuildScene();
    }

    private void PartProperties_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedDielec() is not DielecModel d)
        {
            MessageBox.Show(this, "Najprv vyber dielec v zozname.", "Vlastnosti",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new PartPropertiesWindow(d) { Owner = this };
        if (dlg.ShowDialog() != true)
            return;

        d.OtocitSpodkomHore = dlg.OtocitSpodkomHore;
        d.AbsJeDole = dlg.AbsJeDole;
        d.AbsPaskaMm = dlg.AbsPaskaMm;
        d.XcsInfoMessage = dlg.XcsInfoMessage;
        d.DruheUpnutie = dlg.DruheUpnutie;
        d.XcsInfoMessageB = string.IsNullOrWhiteSpace(dlg.XcsInfoMessageB)
            ? "po ABS - odsadit o listu - otoc"
            : dlg.XcsInfoMessageB;

        foreach (var inst in SameNamedKorpusInstances(d).Where(x => !ReferenceEquals(x, d)))
        {
            inst.OtocitSpodkomHore = d.OtocitSpodkomHore;
            inst.AbsJeDole = d.AbsJeDole;
            inst.AbsPaskaMm = d.AbsPaskaMm;
            inst.XcsInfoMessage = d.XcsInfoMessage;
            inst.DruheUpnutie = d.DruheUpnutie;
            inst.XcsInfoMessageB = d.XcsInfoMessageB;
        }

        RefreshPartList(d);
        RefreshSelectedPartProps();
        string absTip = d.ResolveInfoMessage() ?? "bez poznámky";
        string up2 = d.DruheUpnutie ? " · 2. upnutie (_A/_B)" : "";
        StatusText.Text = d.OtocitSpodkomHore
            ? $"„{d.Nazov}“ — otočiť spodkom hore · {absTip}{up2}"
            : $"„{d.Nazov}“ — Top · {absTip}{up2}";
        TryAutoSaveSession();
    }

    private void RefreshSelectedPartProps()
    {
        if (PartPropsName == null)
            return;

        _suppressPartProps = true;
        try
        {
            if (SelectedDielec() is not DielecModel d)
            {
                PartPropsPanel.IsEnabled = false;
                PartPropsName.Text = "(žiadny výber)";
                PartPropsSize.Text = "";
                PartPropsFlip.IsChecked = false;
                PartPropsAbsDole.IsChecked = false;
                PartPropsAbsPaska.Text = "";
                PartPropsDruheUpnutie.IsChecked = false;
                PartPropsDruheUpnutiePanel.Visibility = Visibility.Collapsed;
                PartPropsInfoB.Text = "";
                PartPropsInfo.Text = "";
                PartPropsPreview.Text = "";
                return;
            }

            PartPropsPanel.IsEnabled = true;
            PartPropsName.Text = d.Nazov;
            PartPropsSize.Text = $"{d.RozmerX:0.##} × {d.RozmerY:0.##} × {d.RozmerZ:0.##} mm";
            PartPropsFlip.IsChecked = d.OtocitSpodkomHore;
            PartPropsAbsDole.IsChecked = d.AbsJeDole;
            PartPropsAbsDole.Visibility = d.JeBok ? Visibility.Visible : Visibility.Collapsed;
            PartPropsAbsPaska.Text = (d.AbsPaskaMm > 0 ? d.AbsPaskaMm : 0.8)
                .ToString("0.##", CultureInfo.CurrentCulture);
            PartPropsAbsPaska.IsEnabled = d.MaAbs;
            PartPropsDruheUpnutie.IsChecked = d.DruheUpnutie;
            PartPropsDruheUpnutiePanel.Visibility =
                d.DruheUpnutie ? Visibility.Visible : Visibility.Collapsed;
            PartPropsInfoB.Text = !string.IsNullOrWhiteSpace(d.XcsInfoMessageB)
                ? d.XcsInfoMessageB
                : "po ABS - odsadit o listu - otoc";
            PartPropsInfo.Text = !string.IsNullOrWhiteSpace(d.XcsInfoMessage)
                ? d.XcsInfoMessage
                : (d.BuildAbsMessageText() ?? "");
            UpdatePartPropsPreview(d);
        }
        finally
        {
            _suppressPartProps = false;
        }
    }

    private void UpdatePartPropsPreview(DielecModel d)
    {
        string text = d.ResolveInfoMessage() ?? "";
        PartPropsPreview.Text = text.Length == 0
            ? "Bez poznámky (CreateMessage sa neexportuje)."
            : $"Náhľad XCS: CreateMessage(\"Info\",\"{text}\",…)";
    }

    private void PartPropsField_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            ApplyPartPropsFromUi();
        }
    }

    private void PartProps_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressPartProps)
            return;
        ApplyPartPropsFromUi();
    }

    private void ApplyPartPropsFromUi()
    {
        if (_suppressPartProps || SelectedDielec() is not DielecModel d)
            return;

        string paskaTxt = (PartPropsAbsPaska.Text ?? "").Trim().Replace(',', '.');
        if (!double.TryParse(paskaTxt, NumberStyles.Float, CultureInfo.InvariantCulture, out double paska)
            || paska <= 0)
            paska = d.AbsPaskaMm > 0 ? d.AbsPaskaMm : 0.8;

        d.OtocitSpodkomHore = PartPropsFlip.IsChecked == true;
        d.AbsJeDole = PartPropsAbsDole.IsChecked == true;
        d.AbsPaskaMm = paska;
        d.DruheUpnutie = PartPropsDruheUpnutie.IsChecked == true;
        d.XcsInfoMessage = (PartPropsInfo.Text ?? "").Trim();
        if (d.DruheUpnutie)
        {
            string msgB = (PartPropsInfoB.Text ?? "").Trim();
            d.XcsInfoMessageB = string.IsNullOrWhiteSpace(msgB)
                ? "po ABS - odsadit o listu - otoc"
                : msgB;
            PartPropsDruheUpnutiePanel.Visibility = Visibility.Visible;
            if (string.IsNullOrWhiteSpace(PartPropsInfoB.Text))
            {
                _suppressPartProps = true;
                PartPropsInfoB.Text = d.XcsInfoMessageB;
                _suppressPartProps = false;
            }
        }
        else
        {
            PartPropsDruheUpnutiePanel.Visibility = Visibility.Collapsed;
        }

        foreach (var inst in SameNamedKorpusInstances(d).Where(x => !ReferenceEquals(x, d)))
        {
            inst.OtocitSpodkomHore = d.OtocitSpodkomHore;
            inst.AbsJeDole = d.AbsJeDole;
            inst.AbsPaskaMm = d.AbsPaskaMm;
            inst.XcsInfoMessage = d.XcsInfoMessage;
            inst.DruheUpnutie = d.DruheUpnutie;
            inst.XcsInfoMessageB = d.XcsInfoMessageB;
        }

        if (PartList.SelectedItem is PartTreeNode node)
            node.RefreshDisplay();

        UpdatePartPropsPreview(d);
        string absTip = d.ResolveInfoMessage() ?? "bez poznámky";
        string up2 = d.DruheUpnutie ? " · 2. upnutie (_A/_B)" : "";
        StatusText.Text = d.OtocitSpodkomHore
            ? $"„{d.Nazov}“ — otočiť spodkom hore · {absTip}{up2}"
            : $"„{d.Nazov}“ — Top · {absTip}{up2}";
        TryAutoSaveSession();
    }

    private void RefreshPartList(DielecModel? keep)
    {
        if (_doc == null) return;
        var tree = PartTreeBuilder.Build(_doc);
        if (keep != null)
        {
            var node = PartTreeBuilder.FindNode(tree, keep);
            if (node != null)
            {
                ExpandAncestors(tree, keep);
                node.IsSelected = true;
            }
        }
        else if (tree.Count > 0)
        {
            tree[0].IsSelected = true;
        }

        PartList.ItemsSource = null;
        PartList.ItemsSource = tree;
    }

    /// <summary>
    /// Rovnaký názov korpusu = jedna CNC položka (napr. 4× polica) — všetky inštancie v 3D.
    /// </summary>
    private IEnumerable<DielecModel> SameNamedKorpusInstances(DielecModel d)
    {
        if (_doc == null || d.JeSuflik)
            return new[] { d };
        return _doc.KorpusDiely.Where(x =>
            string.Equals(PartRules.CncGroupKey(x, _doc), PartRules.CncGroupKey(d, _doc),
                StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Rozbalí rodičov vybraného dielca (inak by bol skrytý v zbalenom strome).</summary>
    private static void ExpandAncestors(IEnumerable<PartTreeNode> roots, DielecModel dielec)
    {
        foreach (var root in roots)
        {
            if (ExpandPath(root, dielec))
                return;
        }
    }

    private static bool ExpandPath(PartTreeNode node, DielecModel dielec)
    {
        if (node.Dielec != null && ReferenceEquals(node.Dielec, dielec))
            return true;

        foreach (var child in node.Children)
        {
            if (ExpandPath(child, dielec))
            {
                node.IsExpanded = true;
                return true;
            }
        }
        return false;
    }

    private void ContactList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressContactUi) return;
        int n = ContactList.SelectedItems.Count;
        if (n == 1 && ContactList.SelectedItem is ContactMark c)
        {
            StatusText.Text = $"Dotyk {c.Cislo}: {c.PartA} ↔ {c.PartB}" +
                              (c.Oznaceny ? $"  |  označené: {c.Oznacenie}" : "  |  klikni v 3D");
        }
        else if (n > 1)
        {
            StatusText.Text = $"Vybrané {n} plôch — Kolíkovať nastaví rovnaké parametre všetkým.";
        }
        RebuildScene();
    }

    private void ContactList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var dep = e.OriginalSource as DependencyObject;
        while (dep != null && dep is not ListBoxItem)
            dep = VisualTreeHelper.GetParent(dep);
        if (dep is ListBoxItem item)
        {
            item.IsSelected = true;
            item.Focus();
        }
    }

    private void ContactList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete)
        {
            RemoveSelectedContacts();
            e.Handled = true;
        }
    }

    private void RemoveContacts_Click(object sender, RoutedEventArgs e)
        => RemoveSelectedContacts();

    private void RemoveSelectedContacts()
    {
        if (_doc == null) return;
        var targets = ContactList.SelectedItems.Cast<ContactMark>().ToList();
        if (targets.Count == 0)
        {
            MessageBox.Show(this, "Najprv vyber dotyk(y) v zozname.", "Odstrániť",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string tip = targets.Count == 1
            ? $"Odstrániť dotyk „{targets[0].LabelText}“ ({targets[0].PartA} ↔ {targets[0].PartB})?"
            : $"Odstrániť {targets.Count} vybraných dotykov zo zoznamu?";
        if (MessageBox.Show(this, tip, "Odstrániť dotyk",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        foreach (var c in targets)
            _doc.Dotyky.Remove(c);

        for (int i = 0; i < _doc.Dotyky.Count; i++)
            _doc.Dotyky[i].Cislo = i + 1;

        ContactList.ItemsSource = null;
        RefreshContactListUi();
        int visible = _doc.Dotyky.Count(c => !c.JeSuflikAuto && !PartRules.IsPolicaContact(c));
        StatusText.Text = $"Odstránené: {targets.Count}. Zostáva {visible} dotykov.";
        RebuildScene();
    }

    private void ContactListContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        var selected = ContactList.SelectedItems.Cast<ContactMark>().ToList();
        var one = selected.Count == 1 ? selected[0] : null;

        if (CopyKolikyMenuItem != null)
            CopyKolikyMenuItem.IsEnabled = one != null && one.MaKoliky;

        if (PasteKolikyMenuItem != null)
        {
            PasteKolikyMenuItem.IsEnabled =
                _kolikyClipboard is { Count: > 0 } && selected.Count > 0;
            PasteKolikyMenuItem.Header = _kolikyClipboard is { Count: > 0 }
                ? $"Vložiť kolíky ({_kolikyClipboard.Count} sér.)"
                : "Vložiť kolíky";
        }
    }

    private void CopyKoliky_Click(object sender, RoutedEventArgs e)
    {
        if (ContactList.SelectedItem is not ContactMark c || !c.MaKoliky)
        {
            MessageBox.Show(this,
                "Vyber plochu s nakonfigurovanými kolíkmi.",
                "Kopírovať kolíky", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _kolikyClipboard = c.KolikSerie.Select(CloneKolikSerie).ToList();
        UiInputLogger.Write("CopyKoliky",
            $"{UiInputLogger.FormatContact(c)} | {UiInputLogger.FormatKolikSerieList(_kolikyClipboard)}");
        StatusText.Text =
            $"Skopírované kolíky z „{c.LabelText}“: {_kolikyClipboard.Count} sér./{c.CelkovyPocetKolikov}×.";
    }

    private void PasteKoliky_Click(object sender, RoutedEventArgs e)
    {
        if (_kolikyClipboard is not { Count: > 0 })
        {
            MessageBox.Show(this,
                "Schránka je prázdna — najprv Kopírovať kolíky z inej plochy.",
                "Vložiť kolíky", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var targets = ContactList.SelectedItems.Cast<ContactMark>().ToList();
        if (targets.Count == 0)
        {
            MessageBox.Show(this, "Vyber cieľovú plochu v zozname.",
                "Vložiť kolíky", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        foreach (var c in targets)
        {
            if (!c.Oznaceny && string.IsNullOrWhiteSpace(c.Oznacenie))
                c.Oznacenie = c.LabelText;

            c.ReplaceAllSerie(_kolikyClipboard.Select(CloneKolikSerie));
        }

        RefreshContactList(targets);
        UiInputLogger.Write("PasteKoliky",
            $"plôch={targets.Count}; ciele=[{string.Join(", ", targets.Select(UiInputLogger.FormatContact))}] | " +
            UiInputLogger.FormatKolikSerieList(_kolikyClipboard));
        StatusText.Text = targets.Count == 1
            ? $"Vložené kolíky na „{targets[0].LabelText}“: {targets[0].KolikSerie.Count} sér./{targets[0].CelkovyPocetKolikov}×."
            : $"Vložené kolíky na {targets.Count} plôch ({_kolikyClipboard.Count} sér. každá).";
        RebuildScene();
        TryAutoSaveSession();
    }

    private static KolikSerie CloneKolikSerie(KolikSerie s) => new()
    {
        OdPredu = s.OdPredu,
        PocetKolikov = s.PocetKolikov,
        RoztecKolikov = s.RoztecKolikov,
        ZDruhejStrany = s.ZDruhejStrany,
        ZoStredu = s.ZoStredu,
    };

    private void EditKoliky_Click(object sender, RoutedEventArgs e)
    {
        var targets = ContactList.SelectedItems.Cast<ContactMark>().ToList();
        if (targets.Count == 0)
        {
            MessageBox.Show(this,
                "Vyber jednu alebo viac plôch v zozname (Ctrl+klik).",
                "Upraviť kolíky", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new EditKolikyWindow(targets) { Owner = this };
        if (dlg.ShowDialog() != true)
            return;

        UiInputLogger.Write("EditKoliky",
            string.Join(" || ", targets.Select(c =>
                $"{UiInputLogger.FormatContact(c)} | " +
                (c.MaKoliky
                    ? UiInputLogger.FormatKolikSerieList(c.KolikSerie)
                    : "(odstránené)"))));
        RefreshContactList(targets);
        StatusText.Text = targets.Count == 1
            ? targets[0].MaKoliky
                ? $"Upravené kolíky „{targets[0].LabelText}“: {targets[0].KolikSerie.Count} sér./{targets[0].CelkovyPocetKolikov}×."
                : $"Kolíky na „{targets[0].LabelText}“ odstránené."
            : $"Upravené kolíky na {targets.Count} plôch.";
        RebuildScene();
        TryAutoSaveSession();
    }

    private void Kolikovat_Click(object sender, RoutedEventArgs e)
    {
        var targets = ContactList.SelectedItems.Cast<ContactMark>().ToList();
        if (targets.Count == 0)
        {
            MessageBox.Show(this,
                "Vyber jednu alebo viac styčných plôch (Ctrl+klik v 3D / v zozname).",
                "Kolíkovať", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        foreach (var c in targets)
        {
            if (!c.Oznaceny)
            {
                if (string.IsNullOrWhiteSpace(c.Oznacenie))
                    c.Oznacenie = c.LabelText;
                c.Oznaceny = true;
            }
        }

        var first = targets[0];
        string info = targets.Count == 1
            ? $"Plocha: {first.LabelText}\n{first.PartA}  ↔  {first.PartB}\n" +
              $"Existujúce série: {first.KolikSerie.Count} (pridá sa nová)"
            : $"Počet plôch: {targets.Count}\nNa každú sa pridá nová séria kolíkov.";

        var dlg = new KolikovatDialog(info)
        {
            Owner = this
        };

        if (dlg.ShowDialog() != true)
            return;

        foreach (var c in targets)
        {
            foreach (var s in dlg.SerieList)
            {
                c.AddSerie(new KolikSerie
                {
                    OdPredu = s.OdPredu,
                    PocetKolikov = s.PocetKolikov,
                    RoztecKolikov = s.RoztecKolikov,
                    ZDruhejStrany = s.ZDruhejStrany,
                    ZoStredu = s.ZoStredu
                });
            }
        }

        RefreshContactList(targets);
        UiInputLogger.Write("Kolikovat",
            $"plôch={targets.Count}; ciele=[{string.Join(", ", targets.Select(UiInputLogger.FormatContact))}] | " +
            UiInputLogger.FormatKolikSerieList(dlg.SerieList));
        int nSer = dlg.SerieList.Count;
        var serie = dlg.SerieList[0];
        if (serie.ZoStredu)
        {
            StatusText.Text =
                $"Pridaná séria ({targets.Count} plôch): zo stredu, {serie.PocetKolikov} ks, rozteč {serie.RoztecKolikov:0.##} mm.";
        }
        else if (nSer > 1)
        {
            var s2 = dlg.SerieList[1];
            StatusText.Text =
                $"Pridané 2 série ({targets.Count} plôch): " +
                $"od predu {serie.OdPredu:0.##}/{serie.PocetKolikov}/{serie.RoztecKolikov:0.##} + " +
                $"z druhej {s2.OdPredu:0.##}/{s2.PocetKolikov}/{s2.RoztecKolikov:0.##}.";
        }
        else
        {
            StatusText.Text =
                $"Pridaná séria ({targets.Count} plôch): od predu {serie.OdPredu:0.##} mm, " +
                $"{serie.PocetKolikov} ks, rozteč {serie.RoztecKolikov:0.##} mm.";
        }
        RebuildScene();
        TryAutoSaveSession();
    }

    private void Skrutky_Click(object sender, RoutedEventArgs e)
    {
        var targets = ContactList.SelectedItems.Cast<ContactMark>().ToList();
        if (targets.Count == 0)
        {
            MessageBox.Show(this,
                "Vyber jednu alebo viac styčných plôch (Ctrl+klik v 3D / v zozname).",
                "Skrutky", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        foreach (var c in targets)
        {
            if (!c.Oznaceny)
            {
                if (string.IsNullOrWhiteSpace(c.Oznacenie))
                    c.Oznacenie = c.LabelText;
                c.Oznaceny = true;
            }
        }

        var first = targets[0];
        double len1 = SkrutkaLayout.PrimaryLength(first);
        string info = targets.Count == 1
            ? $"Plocha: {first.LabelText}\n{first.PartA}  ↔  {first.PartB}\n" +
              $"Dĺžka styčnej plochy: {len1:0.##} mm\n" +
              $"Existujúce série skrutiek: {first.SkrutkySerie.Count} (pridá sa nová)"
            : $"Počet plôch: {targets.Count}\nNa každú sa pridá nová séria (pri Symetrické sa rozteč spočíta podľa dĺžky každej plochy).";

        var dlg = new SkrutkyDialog(info) { Owner = this };
        if (dlg.ShowDialog() != true)
            return;

        foreach (var c in targets)
        {
            var serie = new SkrutkaSerie
            {
                OdPredu = dlg.Serie.OdPredu,
                PocetSkrutiek = dlg.Serie.PocetSkrutiek,
                RoztecSkrutiek = dlg.Serie.RoztecSkrutiek,
                ZDruhejStrany = dlg.Serie.ZDruhejStrany,
                Symetricke = dlg.Serie.Symetricke,
                SymetriaMedziKolikmi = dlg.Serie.SymetriaMedziKolikmi
            };
            if (!SkrutkaLayout.ApplySymetricIfNeeded(c, serie, out string? err))
            {
                MessageBox.Show(this,
                    $"Plocha „{c.LabelText}“: {err}",
                    "Skrutky", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            c.AddSkrutkaSerie(serie);
        }

        RefreshContactList(targets);
        UiInputLogger.Write("Skrutky",
            $"plôch={targets.Count}; ciele=[{string.Join(", ", targets.Select(UiInputLogger.FormatContact))}] | " +
            string.Join(" | ", targets.Select(t => t.SkrutkySerie.Last()).Select(UiInputLogger.FormatSkrutkaSerie)));
        if (dlg.Serie.Symetricke)
        {
            string mode = dlg.Serie.SymetriaMedziKolikmi ? "medzi kolíkmi" : "podľa dotyku";
            StatusText.Text =
                $"Symetrické skrutky ({mode}, {targets.Count} plôch): od predu {dlg.Serie.OdPredu:0.##} mm, " +
                $"{dlg.Serie.PocetSkrutiek} ks — rozteč = (L − 2·Od predu)/(n−1).";
        }
        else
        {
            string strana = dlg.Serie.ZDruhejStrany ? "z druhej strany" : "od predu";
            StatusText.Text =
                $"Pridaná séria skrutiek ({targets.Count} plôch): {strana} {dlg.Serie.OdPredu:0.##} mm, " +
                $"{dlg.Serie.PocetSkrutiek} ks, rozteč {dlg.Serie.RoztecSkrutiek:0.##} mm.";
        }
        RebuildScene();
        TryAutoSaveSession();
    }

    private void EditSkrutky_Click(object sender, RoutedEventArgs e)
    {
        var targets = ContactList.SelectedItems.Cast<ContactMark>().ToList();
        if (targets.Count == 0)
        {
            MessageBox.Show(this,
                "Vyber jednu alebo viac plôch v zozname (Ctrl+klik).",
                "Upraviť skrutky", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new EditSkrutkyWindow(targets) { Owner = this };
        if (dlg.ShowDialog() != true)
            return;

        UiInputLogger.Write("EditSkrutky",
            string.Join(" || ", targets.Select(c =>
                $"{UiInputLogger.FormatContact(c)} | " +
                (c.MaSkrutky
                    ? UiInputLogger.FormatSkrutkaSerieList(c.SkrutkySerie)
                    : "(odstránené)"))));
        RefreshContactList(targets);
        StatusText.Text = targets.Count == 1
            ? targets[0].MaSkrutky
                ? $"Upravené skrutky „{targets[0].LabelText}“: {targets[0].SkrutkySerie.Count} sér./{targets[0].CelkovyPocetSkrutiek}×."
                : $"Skrutky na „{targets[0].LabelText}“ odstránené."
            : $"Upravené skrutky na {targets.Count} plôch.";
        RebuildScene();
        TryAutoSaveSession();
    }

    private void Generovat_Click(object sender, RoutedEventArgs e)
    {
        if (_doc == null)
        {
            MessageBox.Show(this, "Najprv otvor Excel export.", "Generovať",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string? outDir = null;
        if (!string.IsNullOrEmpty(_sourceExcelPath))
        {
            outDir = Path.Combine(Path.GetDirectoryName(_sourceExcelPath)!, "XCS");
        }
        else
        {
            var save = new SaveFileDialog
            {
                Title = "Vyber priečinok pre XCS (zadaj názov ľubovoľného súboru)",
                FileName = "XCS",
                Filter = "Priečinok|*.*"
            };
            if (save.ShowDialog() != true)
                return;
            outDir = Path.GetDirectoryName(save.FileName);
        }

        if (string.IsNullOrEmpty(outDir))
            return;

        try
        {
            Directory.CreateDirectory(outDir);
            int n = XcsProgramGenerator.GenerateAll(_doc, outDir);

            // Viewer: kolíky/skrutky do plochy (vizuál), nie XCS pravidlá
            var drills = DrillGenerator.Generate(_doc);
            if (drills.Count > 0)
                DrillGenerator.ApplyToCncZnacenia(_doc, drills);

            RebuildScene();
            UiInputLogger.Write("Generovat", $"súborov={n}; výstup={outDir}");
            TryAutoSaveSession();
            StatusText.Text = $"Generovať: {n}× .xcs → {outDir}";

            var ask = MessageBox.Show(this,
                $"Vygenerované {n} programov (.xcs).\n\n" +
                "• Kolíky dno/vrch/priecka/chrbat ↔ bok + dno/vrch↔chrbat (A/B)\n" +
                "• Skrutky do plochy (rovnaká os/pattern ako kolíky)\n" +
                "• Movento + podperky + závesy plošné (Y od predku; hranové závesy nie)\n" +
                "• Nohy: _A = kolíky Top, _B = nohy Bottom po otočení (po ABS)\n" +
                "• Výrezy / zafrezy (CreatePolyline z Excel Vyrezy)\n\n" +
                $"Priečinok:\n{outDir}\n\n" +
                "Spustiť XConverter (.xcs → .pgmx)?",
                "Generovať",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (ask == MessageBoxResult.Yes)
            {
                StatusText.Text = "XConverter…";
                var conv = XConverterRunner.ConvertFolder(outDir, this);
                StatusText.Text = conv.Ok
                    ? $"XConverter OK → {outDir}"
                    : $"XConverter: {conv.Message.Split('\n')[0]}";
                MessageBox.Show(this, conv.Message,
                    conv.Ok ? "XConverter" : "XConverter — chyba",
                    MessageBoxButton.OK,
                    conv.Ok ? MessageBoxImage.Information : MessageBoxImage.Warning);

                if (!conv.Ok && !string.IsNullOrEmpty(conv.LogPath) && File.Exists(conv.LogPath))
                {
                    var openLog = MessageBox.Show(this, "Otvoriť log súbor?",
                        "XConverter", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (openLog == MessageBoxResult.Yes)
                    {
                        try
                        {
                            Process.Start(new ProcessStartInfo
                            {
                                FileName = conv.LogPath,
                                UseShellExecute = true
                            });
                        }
                        catch { /* ignore */ }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Chyba generovania", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ViewOption_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        RebuildScene();
    }
    private void ViewTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;

        bool cela = CurrentSceneGroup() == SceneBuilder.SceneGroup.CelaSkrinka;
        if (CelaSkrinkaOpacityPanel != null)
            CelaSkrinkaOpacityPanel.Visibility = cela ? Visibility.Visible : Visibility.Collapsed;

        if (_doc != null && CurrentSceneGroup() == SceneBuilder.SceneGroup.Sufliky)
        {
            // Istota: auto-dotyky + kolíky podľa aktuálneho počtu na pozíciách
            if (!_doc.Dotyky.Any(c => c.JeSuflikAuto))
                SuflikContactBuilder.Attach(_doc);
            else
            {
                foreach (var poz in _doc.SuflikDiely.Where(d => d.JeSuflikPozicia && d.PocetKolikov > 0))
                    SuflikContactBuilder.RefreshKolikyForPozicia(_doc, poz);
            }

            int auto = _doc.Dotyky.Count(c => c.JeSuflikAuto);
            int kol = _doc.Dotyky.Where(c => c.JeSuflikAuto).Sum(c => c.CelkovyPocetKolikov);
            int diely = _doc.SuflikDiely.Count(d => !d.JeSuflikPozicia);
            StatusText.Text =
                $"Šuflíky: {diely} dielov, {auto} stykov, {kol} kolíkov (červené značky).";
        }
        else if (_doc != null && cela)
        {
            int absEdges = _doc.Diely.Count(d => d.MaAbs && !d.JeSuflikPozicia && !d.JeSkryty);
            StatusText.Text =
                $"Celá skrinka — ABS hrany modré ({absEdges} dielcov s ABS), kolíky/diery červené.";
        }
        RebuildScene();
        Viewport.ZoomExtents();
    }

    private void PanelOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded || PanelOpacityLabel == null) return;
        PanelOpacityLabel.Text = $"{PanelOpacitySlider.Value:0} %";
        if (CurrentSceneGroup() == SceneBuilder.SceneGroup.CelaSkrinka)
            RebuildScene();
    }

    private void ZoomExtents_Click(object sender, RoutedEventArgs e)
        => Viewport.ZoomExtents();

    private void UpdateSuflikTabVisibility()
    {
        bool hasSuflik = _doc != null && _doc.SuflikDiely.Any();
        TabSufliky.Visibility = hasSuflik ? Visibility.Visible : Visibility.Collapsed;
        if (!hasSuflik && ViewTabs.SelectedItem == TabSufliky)
            ViewTabs.SelectedItem = TabSkrinka;
    }

    private SceneBuilder.SceneGroup CurrentSceneGroup()
    {
        if (ReferenceEquals(ViewTabs.SelectedItem, TabSufliky))
            return SceneBuilder.SceneGroup.Sufliky;
        if (ReferenceEquals(ViewTabs.SelectedItem, TabCelaSkrinka))
            return SceneBuilder.SceneGroup.CelaSkrinka;
        return SceneBuilder.SceneGroup.Korpus;
    }

    private void RebuildScene()
    {
        try
        {
            RebuildSceneCore();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Chyba scény: {ex.Message}";
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    private void RebuildSceneCore()
    {
        foreach (var v in _sceneVisuals)
            Viewport.Children.Remove(v);
        _sceneVisuals.Clear();

        if (_doc == null)
            return;

        var selected = SelectedDielec();
        var selectedContacts = ContactList.SelectedItems.Cast<ContactMark>().ToList();
        var group = CurrentSceneGroup();

        // Celá skrinka + Šuflíky = zostava (kolíky / celý náhľad).
        var mode = group is SceneBuilder.SceneGroup.CelaSkrinka or SceneBuilder.SceneGroup.Sufliky
            ? SceneBuilder.ViewMode.Assembly
            : (RbSingle.IsChecked == true
                ? SceneBuilder.ViewMode.SinglePart
                : SceneBuilder.ViewMode.Assembly);

        // Jednodielny režim: ak výber nepatrí do aktívnej karty, nič nekresli
        if (mode == SceneBuilder.ViewMode.SinglePart && selected != null)
        {
            bool selSuflik = selected.JeSuflik;
            if (group == SceneBuilder.SceneGroup.Korpus && selSuflik)
                selected = null;
            if (group == SceneBuilder.SceneGroup.Sufliky && !selSuflik)
                selected = null;
        }

        bool showContacts = group != SceneBuilder.SceneGroup.CelaSkrinka
                            && TogContacts.IsChecked == true;

        double opacityOverride = group == SceneBuilder.SceneGroup.CelaSkrinka
            ? PanelOpacitySlider.Value / 100.0
            : -1;

        foreach (var visual in SceneBuilder.BuildVisuals(
                     _doc, selected, selectedContacts, mode, group,
                     TogPanels.IsChecked == true,
                     group == SceneBuilder.SceneGroup.CelaSkrinka
                         ? false
                         : TogBody.IsChecked == true,
                     group == SceneBuilder.SceneGroup.CelaSkrinka
                         ? false
                         : TogCnc.IsChecked == true,
                     showContacts,
                     TogContactLabels.IsChecked == true,
                     opacityOverride))
        {
            Viewport.Children.Add(visual);
            _sceneVisuals.Add(visual);
        }
    }
}
