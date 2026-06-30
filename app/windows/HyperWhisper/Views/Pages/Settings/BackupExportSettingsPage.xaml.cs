using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using HyperWhisper.Localization;
using HyperWhisper.Services;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace HyperWhisper.Views.Pages.Settings;

public partial class BackupExportSettingsPage : Page
{
    /// <summary>Path of the backup file currently staged for selective import.</summary>
    private string? _pendingImportPath;

    public BackupExportSettingsPage()
    {
        InitializeComponent();

        // Vocabulary export label carries the live count for context.
        var vocabCount = VocabularyService.Instance.GetAll().Count;
        ExportVocabularyCheckbox.Content =
            Loc.S("settings.backup.export.section.vocabulary", vocabCount);

        UpdateExportButtonState();
    }

    // =========================================================================
    // EXPORT
    // =========================================================================

    private void ExportSection_Changed(object sender, RoutedEventArgs e)
    {
        UpdateExportButtonState();
    }

    private void UpdateExportButtonState()
    {
        // Setting a checkbox's IsChecked in XAML fires Checked/ExportSection_Changed
        // *during* InitializeComponent, before the sibling checkboxes below it have
        // been created. Bail out until the whole control tree is built so
        // BuildExportSelection never dereferences a not-yet-created checkbox.
        if (!IsInitialized)
            return;

        ExportButton.IsEnabled = BuildExportSelection().HasAnySection;
    }

    private BackupExportSelection BuildExportSelection()
    {
        return new BackupExportSelection
        {
            IncludeSettings = ExportSettingsCheckbox.IsChecked == true,
            IncludeModes = ExportModesCheckbox.IsChecked == true,
            IncludeVocabulary = ExportVocabularyCheckbox.IsChecked == true,
            IncludeApiKeys = IncludeApiKeysCheckbox.IsChecked == true
        };
    }

    private void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        var selection = BuildExportSelection();
        if (!selection.HasAnySection)
            return;

        // No extension here — DefaultExt (.hwbackup.json) is appended by the dialog,
        // matching the legacy backup filename behavior.
        var defaultFileName = selection.IsVocabularyOnly
            ? $"HyperWhisper Vocabulary {System.DateTime.Now:yyyy-MM-dd}"
            : $"HyperWhisper-Backup-{System.DateTime.Now:yyyy-MM-dd}";

        var dialog = new SaveFileDialog
        {
            Title = Loc.S("settings.backup.export.dialogTitle"),
            Filter = Loc.S("settings.backup.fileFilter.universal"),
            FileName = defaultFileName,
            DefaultExt = ".hwbackup.json"
        };

        if (dialog.ShowDialog() != true)
            return;

        var filePath = dialog.FileName;

        ExportButton.IsEnabled = false;
        ExportButton.Content = Loc.S("settings.backup.export.exporting");
        ShowStatus(ExportStatusText, "", isError: false);

        Task.Run(() =>
        {
            var result = BackupService.Instance.Export(filePath, selection);

            Dispatcher.Invoke(() =>
            {
                ExportButton.Content = Loc.S("settings.backup.export.button");
                UpdateExportButtonState();

                if (result.IsSuccess)
                    ShowStatus(ExportStatusText, Loc.S("settings.backup.export.success"), isError: false);
                else
                    ShowStatus(ExportStatusText, result.Error!, isError: true);
            });
        });
    }

    // =========================================================================
    // IMPORT (selective, merge-only)
    // =========================================================================

    private void ChooseFileButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = Loc.S("settings.backup.import.dialogTitle"),
            Filter = Loc.S("settings.backup.fileFilter.universal"),
            DefaultExt = ".hwbackup.json"
        };

        if (dialog.ShowDialog() != true)
            return;

        var filePath = dialog.FileName;
        ShowStatus(ImportStatusText, "", isError: false);

        var inspectResult = BackupService.Instance.Inspect(filePath);
        if (inspectResult.IsFailure)
        {
            _pendingImportPath = null;
            ImportSelectionPanel.Visibility = Visibility.Collapsed;
            ShowStatus(ImportStatusText, inspectResult.Error!, isError: true);
            return;
        }

        _pendingImportPath = filePath;
        PopulateImportSelection(inspectResult.Value!);
    }

    private void PopulateImportSelection(BackupContents contents)
    {
        ImportContainsText.Text = Loc.S("settings.backup.import.contains");

        ConfigureSectionCheckbox(ImportSettingsCheckbox, contents.HasSettings);
        ConfigureSectionCheckbox(ImportModesCheckbox, contents.HasModes);
        ConfigureSectionCheckbox(ImportVocabularyCheckbox, contents.HasVocabulary);
        ConfigureSectionCheckbox(ImportApiKeysCheckbox, contents.HasApiKeys);

        // Vocabulary label carries the count present in the file.
        ImportVocabularyCheckbox.Content =
            Loc.S("settings.backup.import.section.vocabulary", contents.VocabularyCount);

        // Conflict policy only matters when vocabulary is present and selectable.
        // Reset to the safe default (Skip) for every newly populated file so a "Replace"
        // choice from a previous import never silently carries over to the next one.
        VocabConflictSkipRadio.IsChecked = true;
        VocabConflictReplaceRadio.IsChecked = false;
        VocabConflictPanel.Visibility = contents.HasVocabulary
            ? Visibility.Visible
            : Visibility.Collapsed;

        ImportSelectionPanel.Visibility = Visibility.Visible;
    }

    private static void ConfigureSectionCheckbox(WpfCheckBox checkbox, bool present)
    {
        checkbox.IsEnabled = present;
        checkbox.IsChecked = present;
    }

    private void ImportSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        var filePath = _pendingImportPath;
        if (string.IsNullOrEmpty(filePath))
            return;

        var selection = new ImportSelection
        {
            IncludeSettings = ImportSettingsCheckbox.IsEnabled && ImportSettingsCheckbox.IsChecked == true,
            IncludeModes = ImportModesCheckbox.IsEnabled && ImportModesCheckbox.IsChecked == true,
            IncludeVocabulary = ImportVocabularyCheckbox.IsEnabled && ImportVocabularyCheckbox.IsChecked == true,
            IncludeApiKeys = ImportApiKeysCheckbox.IsEnabled && ImportApiKeysCheckbox.IsChecked == true,
            VocabularyConflict = VocabConflictReplaceRadio.IsChecked == true
                ? VocabConflict.Replace
                : VocabConflict.Skip
        };

        if (!selection.IncludeSettings && !selection.IncludeModes
            && !selection.IncludeVocabulary && !selection.IncludeApiKeys)
        {
            ShowStatus(ImportStatusText, Loc.S("settings.backup.import.nothingSelected"), isError: true);
            return;
        }

        // Pre-merge confirmation for vocabulary (Add N new, M conflicts).
        if (selection.IncludeVocabulary)
        {
            var preview = BackupService.Instance.PreviewVocabularyMerge(filePath);
            if (preview.IsFailure)
            {
                ShowStatus(ImportStatusText, preview.Error!, isError: true);
                return;
            }

            var confirm = WpfMessageBox.Show(
                Loc.S("settings.backup.import.premerge.message",
                    preview.Value!.NewCount, preview.Value!.ConflictCount),
                Loc.S("settings.backup.import.premerge.title"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes)
                return;
        }

        ImportSelectedButton.IsEnabled = false;
        ImportSelectedButton.Content = Loc.S("settings.backup.import.importing");
        ShowStatus(ImportStatusText, "", isError: false);

        Task.Run(() =>
        {
            var result = BackupService.Instance.ImportSelective(filePath, selection);

            Dispatcher.Invoke(() =>
            {
                ImportSelectedButton.IsEnabled = true;
                ImportSelectedButton.Content = Loc.S("settings.backup.import.applyButton");

                if (result.IsSuccess)
                {
                    var s = result.Value!;
                    var summary = Loc.S("settings.backup.import.summary",
                        s.ModesImported, s.VocabularyAdded, s.VocabularyConflicts);
                    ShowStatus(ImportStatusText,
                        Loc.S("settings.backup.import.success", summary), isError: false);
                    ImportSelectionPanel.Visibility = Visibility.Collapsed;
                    _pendingImportPath = null;
                }
                else
                {
                    ShowStatus(ImportStatusText, result.Error!, isError: true);
                }
            });
        });
    }

    private static void ShowStatus(TextBlock textBlock, string message, bool isError)
    {
        if (string.IsNullOrEmpty(message))
        {
            textBlock.Visibility = Visibility.Collapsed;
            return;
        }

        textBlock.Text = message;
        textBlock.Foreground = isError
            ? System.Windows.Media.Brushes.OrangeRed
            : textBlock.TryFindResource("TextSecondaryBrush") as System.Windows.Media.Brush
              ?? System.Windows.Media.Brushes.Gray;
        textBlock.Visibility = Visibility.Visible;
    }
}
