using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FiveMClipPatcher.Models;
using FiveMClipPatcher.Services;
using Microsoft.Win32;

namespace FiveMClipPatcher.ViewModels;

public partial class MainViewModel : ObservableObject
{
    public const string DefaultPatterns = """
        # Mods courants (README GitHub)
        17mov_GarbageCollector
        17mov_*
        scully_emotemenu
        scully_*
        *_emotemenu
        bzzz_food_*
        bzzz_*
        pprp_*

        # MLO / escrow — crashs documentés
        griz_cayo_restaurant
        amb-roxwood-interiors
        prompt_vfd_4bays

        # Créateurs MLO / maps (escrow ITYP fréquent)
        prompt_*
        gabz_*
        kiiya_*
        k4mb1_*
        molo_*

        # Ajoute tes mods ici (exact = substring, * = nom isolé)
        """;

    private readonly ClipPatcherService _patcher = new();
    private readonly ClipCatalogService _catalog = new();
    private readonly ClipThumbnailService _thumbnails = new();
    private readonly ClipPatternDiscoveryService _discovery = new();
    private AppSettings _settings = AppSettingsStore.Load();
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _discoverCts;

    public ObservableCollection<ClipItemViewModel> Clips { get; } = [];

    [ObservableProperty] private string _clipsPath = "";
    [ObservableProperty] private string _patternsText = DefaultPatterns;
    [ObservableProperty] private bool _recursive;
    [ObservableProperty] private bool _caseInsensitive;
    [ObservableProperty] private bool _usePlaceholderMode;
    [ObservableProperty] private string _placeholder = "REMOVED";
    [ObservableProperty] private string _extensions = ".clip";
    [ObservableProperty] private string _backupLocation = "";
    [ObservableProperty] private string _statusText = "Coche les séquences à traiter, puis scanne ou patche.";
    [ObservableProperty] private string _logText = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isLoadingClips;
    [ObservableProperty] private string? _lastRunDirectory;
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private int _progressValue;
    [ObservableProperty] private int _progressMaximum = 1;
    [ObservableProperty] private bool _isProgressIndeterminate;

    public string SelectionSummary => Clips.Count == 0
        ? "Aucune séquence"
        : $"{SelectedCount} / {Clips.Count} sélectionnée(s)";

    public bool CanRunActions => SelectedCount > 0 && !IsBusy && !IsLoadingClips;

    public bool CanDiscoverPatterns => Clips.Count > 0 && !IsBusy && !IsLoadingClips;

    public MainViewModel()
    {
        try
        {
            ClipsPath = ClipPatcherService.GetDefaultGtaClipsPath();
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }

        BackupLocation = ClipPatcherService.GetDefaultBackupBasePath();
        _ = LoadClipsAsync();
    }

    partial void OnSelectedCountChanged(int value)
    {
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(CanRunActions));
        ScanCommand.NotifyCanExecuteChanged();
        PatchCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanRunActions));
        OnPropertyChanged(nameof(CanDiscoverPatterns));
        ScanCommand.NotifyCanExecuteChanged();
        PatchCommand.NotifyCanExecuteChanged();
        DiscoverPatternsCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsLoadingClipsChanged(bool value)
    {
        OnPropertyChanged(nameof(CanRunActions));
        OnPropertyChanged(nameof(CanDiscoverPatterns));
        ScanCommand.NotifyCanExecuteChanged();
        PatchCommand.NotifyCanExecuteChanged();
        DiscoverPatternsCommand.NotifyCanExecuteChanged();
    }

    partial void OnClipsPathChanged(string value) => _ = LoadClipsAsync();

    partial void OnRecursiveChanged(bool value) => _ = LoadClipsAsync();

    [RelayCommand]
    private void BrowseFolder()
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Dossier des clips FiveM / GTA V",
            Multiselect = false
        };

        if (!string.IsNullOrWhiteSpace(ClipsPath) && Directory.Exists(ClipsPath))
            dlg.InitialDirectory = ClipsPath;

        if (dlg.ShowDialog() == true)
            ClipsPath = dlg.FolderName;
    }

    [RelayCommand]
    private void BrowseFile()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Fichier clip",
            Filter = "Clips Rockstar (*.clip)|*.clip|Tous les fichiers (*.*)|*.*",
            CheckFileExists = true
        };

        if (!string.IsNullOrWhiteSpace(ClipsPath))
        {
            if (Directory.Exists(ClipsPath))
                dlg.InitialDirectory = ClipsPath;
            else if (File.Exists(ClipsPath))
                dlg.InitialDirectory = Path.GetDirectoryName(ClipsPath);
        }

        if (dlg.ShowDialog() == true)
            ClipsPath = dlg.FileName;
    }

    [RelayCommand]
    private void UseDefaultGtaPath()
    {
        try
        {
            ClipsPath = ClipPatcherService.GetDefaultGtaClipsPath();
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    [RelayCommand]
    private void BrowseBackup()
    {
        var dlg = new OpenFolderDialog { Title = "Dossier des backups" };
        if (!string.IsNullOrWhiteSpace(BackupLocation) && Directory.Exists(BackupLocation))
            dlg.InitialDirectory = BackupLocation;

        if (dlg.ShowDialog() == true)
            BackupLocation = dlg.FolderName;
    }

    [RelayCommand]
    private void LoadPatternsFile()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Fichier de patterns",
            Filter = "Texte (*.txt)|*.txt|Tous les fichiers (*.*)|*.*"
        };

        if (dlg.ShowDialog() != true)
            return;

        try
        {
            PatternsText = File.ReadAllText(dlg.FileName);
            StatusText = $"Patterns chargés depuis {Path.GetFileName(dlg.FileName)}.";
        }
        catch (Exception ex)
        {
            StatusText = $"Impossible de lire le fichier : {ex.Message}";
        }
    }

    [RelayCommand]
    private void ResetPatterns()
    {
        PatternsText = DefaultPatterns;
        StatusText = "Patterns par défaut restaurés.";
    }

    [RelayCommand]
    private void CleanPatterns()
    {
        var before = ParsePatterns(PatternsText).Count;
        PatternsText = PatternSafetyService.RemoveUnsafePatternLines(PatternsText);
        var removed = before - ParsePatterns(PatternsText).Count;
        StatusText = removed == 0
            ? "Aucun pattern dangereux trouvé."
            : $"{removed} pattern(s) dangereux retirés (ex: j_*, cfx_*). Utilise les backups si un clip est déjà cassé.";
    }

    [RelayCommand(CanExecute = nameof(CanDiscoverPatterns), AllowConcurrentExecutions = false)]
    private Task DiscoverPatternsAsync() => RunPatternDiscoveryAsync();

    [RelayCommand]
    private void OpenClipsFolder()
    {
        var path = ClipsPath;
        if (File.Exists(path))
            path = Path.GetDirectoryName(path) ?? path;
        OpenPath(path);
    }

    [RelayCommand]
    private void OpenBackupFolder()
    {
        var path = LastRunDirectory;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            path = string.IsNullOrWhiteSpace(BackupLocation)
                ? ClipPatcherService.GetDefaultBackupBasePath()
                : ClipPatcherService.ResolveRunDirectory(BackupLocation.Trim(), "preview");
            path = Path.GetDirectoryName(path);
        }

        OpenPath(path);
    }

    [RelayCommand]
    private void ReloadClips() => _ = LoadClipsAsync();

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var clip in Clips)
            clip.IsSelected = true;
        RefreshSelectedCount();
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var clip in Clips)
            clip.IsSelected = false;
        RefreshSelectedCount();
    }

    [RelayCommand]
    private void ToggleClip(ClipItemViewModel? clip)
    {
        if (clip is null)
            return;
        clip.IsSelected = !clip.IsSelected;
        RefreshSelectedCount();
    }

    [RelayCommand(CanExecute = nameof(CanRunActions), AllowConcurrentExecutions = false)]
    private Task ScanAsync() => RunAsync(dryRun: true, confirmWrite: false);

    [RelayCommand(CanExecute = nameof(CanRunActions), AllowConcurrentExecutions = false)]
    private Task PatchAsync() => RunAsync(dryRun: false, confirmWrite: true);

    public void SetDroppedPath(string path)
    {
        if (Directory.Exists(path) || File.Exists(path))
        {
            ClipsPath = path;
            StatusText = "Chemin déposé.";
        }
    }

    private async Task LoadClipsAsync()
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();
        var token = _loadCts.Token;

        foreach (var clip in Clips.ToList())
            clip.PropertyChanged -= ClipOnPropertyChanged;

        Clips.Clear();
        SelectedCount = 0;
        OnPropertyChanged(nameof(SelectionSummary));

        if (string.IsNullOrWhiteSpace(ClipsPath))
        {
            StatusText = "Choisis un dossier de clips.";
            return;
        }

        var path = ClipsPath.Trim();
        if (!Directory.Exists(path) && !File.Exists(path))
        {
            StatusText = "Dossier ou fichier introuvable.";
            return;
        }

        IsLoadingClips = true;
        StatusText = "Chargement des séquences…";

        try
        {
            var entries = await Task.Run(() =>
                _catalog.ListClips(path, Recursive, Extensions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)), token);

            token.ThrowIfCancellationRequested();

            foreach (var entry in entries)
            {
                var vm = new ClipItemViewModel(entry);
                vm.PropertyChanged += ClipOnPropertyChanged;
                Clips.Add(vm);
            }

            OnPropertyChanged(nameof(SelectionSummary));
            StatusText = Clips.Count == 0
                ? "Aucun .clip trouvé dans ce dossier."
                : $"{Clips.Count} séquence(s) — coche celles à traiter.";

            _ = LoadThumbnailsAsync(token);
            _ = MaybePromptPatternDiscoveryAsync();
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
        catch (Exception ex)
        {
            StatusText = $"Erreur chargement : {ex.Message}";
        }
        finally
        {
            IsLoadingClips = false;
            OnPropertyChanged(nameof(CanDiscoverPatterns));
            DiscoverPatternsCommand.NotifyCanExecuteChanged();
        }
    }

    private async Task MaybePromptPatternDiscoveryAsync()
    {
        if (_settings.DiscoveryPromptShown || Clips.Count == 0)
            return;

        if (!string.Equals(PatternsText.Trim(), DefaultPatterns.Trim(), StringComparison.Ordinal))
            return;

        _settings.DiscoveryPromptShown = true;
        AppSettingsStore.Save(_settings);

        var answer = MessageBox.Show(
            "Scanner tes clips pour suggérer des patterns anti-crash supplémentaires ?\n\n" +
            "Analyse les noms de ressources présents dans tes .clip (peut prendre 1–2 min).",
            "FiveM Clip Patcher",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.Yes);

        if (answer == MessageBoxResult.Yes)
            await RunPatternDiscoveryAsync();
    }

    private async Task RunPatternDiscoveryAsync()
    {
        if (Clips.Count == 0)
        {
            StatusText = "Aucun clip à analyser.";
            return;
        }

        _discoverCts?.Cancel();
        _discoverCts?.Dispose();
        _discoverCts = new CancellationTokenSource();
        var token = _discoverCts.Token;

        IsBusy = true;
        IsProgressIndeterminate = true;
        StatusText = "Analyse des clips pour suggérer des patterns…";

        try
        {
            var clipPaths = Clips.Select(c => c.FilePath).ToList();
            var existing = ParsePatterns(PatternsText);

            var suggestions = await Task.Run(() =>
                _discovery.DiscoverFromClips(
                    clipPaths,
                    existing,
                    new Progress<string>(line => StatusText = line),
                    token), token);

            token.ThrowIfCancellationRequested();

            if (suggestions.Count == 0)
            {
                StatusText = "Aucun pattern supplémentaire détecté (liste actuelle déjà couverte).";
                return;
            }

            PatternsText = MergeSuggestedPatterns(PatternsText, suggestions);
            StatusText = $"{suggestions.Count} pattern(s) suggéré(s) ajoutés en fin de liste.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Analyse annulée.";
        }
        catch (Exception ex)
        {
            StatusText = $"Erreur analyse : {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
            ProgressValue = 0;
        }
    }

    internal static string MergeSuggestedPatterns(string current, IReadOnlyList<SuggestedPattern> suggestions)
    {
        var existing = ParsePatterns(current).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var toAdd = suggestions
            .Where(s => !existing.Contains(s.Pattern))
            .Where(s => PatternSafetyService.IsSafePattern(s.Pattern))
            .ToList();

        if (toAdd.Count == 0)
            return current;

        var sb = new StringBuilder(current.TrimEnd());
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine($"# Suggérés depuis tes clips ({DateTime.Now:yyyy-MM-dd})");
        foreach (var suggestion in toAdd)
            sb.AppendLine(suggestion.Pattern);

        return sb.ToString();
    }

    private async Task LoadThumbnailsAsync(CancellationToken token)
    {
        using var gate = new SemaphoreSlim(8);
        var tasks = Clips.Select(async clip =>
        {
            await gate.WaitAsync(token);
            try
            {
                await clip.LoadThumbnailAsync(_thumbnails, token);
            }
            finally
            {
                gate.Release();
            }
        });

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
    }

    private void ClipOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ClipItemViewModel.IsSelected))
            RefreshSelectedCount();
    }

    private void RefreshSelectedCount()
    {
        SelectedCount = Clips.Count(c => c.IsSelected);
    }

    private async Task RunAsync(bool dryRun, bool confirmWrite)
    {
        var selected = Clips.Where(c => c.IsSelected).Select(c => c.FilePath).ToList();
        if (selected.Count == 0)
        {
            StatusText = "Coche au moins une séquence.";
            return;
        }

        var options = TryBuildOptions(dryRun: true, selected);
        if (options is null)
            return;

        IsBusy = true;
        LogText = "";
        ProgressValue = 0;
        ProgressMaximum = selected.Count;
        IsProgressIndeterminate = false;
        StatusText = dryRun ? "Scan en cours…" : "Scan avant patch…";
        _cts = new CancellationTokenSource();

        foreach (var clip in Clips)
            clip.MatchCount = -1;

        try
        {
            var preview = await Task.Run(() => _patcher.Patch(options, CreateProgress(), _cts.Token), _cts.Token);
            ApplyMatchCounts(preview.Hits);

            if (!preview.Success)
            {
                StatusText = preview.Error ?? "Échec.";
                AppendLog(preview.Error ?? "Échec.");
                return;
            }

            if (dryRun)
            {
                StatusText = preview.FilesPatched == 0
                    ? $"{preview.FilesProcessed} séquence(s) scannée(s), aucun match."
                    : $"Scan : {preview.FilesPatched}/{preview.FilesProcessed} avec match, {preview.PatternsPatched} remplacement(s).";
                return;
            }

            if (preview.FilesPatched == 0)
            {
                StatusText = "Aucun match sur la sélection. Rien à patcher.";
                return;
            }

            if (confirmWrite)
            {
                var unsafeLeft = PatternSafetyService.GetUnsafePatterns(ParsePatterns(PatternsText));
                var extraWarn = unsafeLeft.Count > 0
                    ? $"\n\n⚠ {unsafeLeft.Count} pattern(s) dangereux seront IGNORÉS (j_*, cfx_*, etc.)."
                    : "";

                var confirm = MessageBox.Show(
                    $"{preview.FilesPatched} séquence(s) seront patchées ({preview.PatternsPatched} remplacement(s)).\n\n" +
                    "Backup auto, remplacement in-place (même taille).\nFerme GTA / FiveM avant.\n\nContinuer ?" +
                    extraWarn,
                    "FiveM Clip Patcher",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No);

                if (confirm != MessageBoxResult.Yes)
                {
                    StatusText = "Patch annulé.";
                    return;
                }
            }

            LogText = "";
            ProgressValue = 0;
            StatusText = "Patch en cours…";
            var writeOptions = options with { DryRun = false };
            var result = await Task.Run(() => _patcher.Patch(writeOptions, CreateProgress(), _cts.Token), _cts.Token);
            ApplyMatchCounts(result.Hits);
            LastRunDirectory = result.RunDirectory;

            if (!result.Success)
            {
                StatusText = result.Error ?? "Échec.";
                AppendLog(result.Error ?? "Échec.");
                return;
            }

            StatusText = $"{result.FilesPatched} séquence(s) patchée(s), {result.PatternsPatched} remplacement(s). Backups OK.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Annulé.";
        }
        catch (Exception ex)
        {
            StatusText = $"Erreur : {ex.Message}";
            AppendLog(ex.ToString());
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
            ProgressValue = 0;
            _cts.Dispose();
            _cts = null;
        }
    }

    private void ApplyMatchCounts(IReadOnlyList<PatchHit> hits)
    {
        var grouped = hits.GroupBy(h => h.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        foreach (var clip in Clips)
            clip.MatchCount = grouped.TryGetValue(clip.FilePath, out var count) ? count : 0;
    }

    private PatchOptions? TryBuildOptions(bool dryRun, IReadOnlyList<string> selectedFiles)
    {
        var allPatterns = ParsePatterns(PatternsText);
        var unsafePatterns = PatternSafetyService.GetUnsafePatterns(allPatterns);
        var patterns = PatternSafetyService.FilterSafePatterns(allPatterns);

        if (unsafePatterns.Count > 0)
        {
            AppendLog($"Patterns dangereux ignorés ({unsafePatterns.Count}) : {string.Join(", ", unsafePatterns.Take(8))}" +
                      (unsafePatterns.Count > 8 ? "…" : ""));
        }

        if (patterns.Count == 0)
        {
            StatusText = unsafePatterns.Count > 0
                ? "Tous les patterns sont dangereux — clique Nettoyer ou corrige la liste."
                : "Ajoute au moins un pattern (ex: 17mov_*).";
            return null;
        }

        if (string.IsNullOrWhiteSpace(ClipsPath))
        {
            StatusText = "Choisis un dossier ou un fichier .clip.";
            return null;
        }

        var backupBase = string.IsNullOrWhiteSpace(BackupLocation)
            ? ClipPatcherService.GetDefaultBackupBasePath()
            : BackupLocation.Trim();

        return new PatchOptions
        {
            InputPath = ClipsPath.Trim(),
            SelectedFiles = selectedFiles,
            Patterns = patterns,
            Recursive = Recursive,
            CaseInsensitive = CaseInsensitive,
            Mode = UsePlaceholderMode ? PatchMode.Placeholder : PatchMode.Null,
            Placeholder = string.IsNullOrWhiteSpace(Placeholder) ? "REMOVED" : Placeholder.Trim(),
            Extensions = Extensions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            BackupBasePath = backupBase,
            DryRun = dryRun
        };
    }

    private IProgress<string> CreateProgress() => new Progress<string>(line =>
    {
        if (line.StartsWith("Fichier ", StringComparison.Ordinal))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[1].Contains('/'))
            {
                var idxPart = parts[1].TrimEnd(':');
                if (int.TryParse(idxPart.Split('/')[0], out var idx))
                {
                    ProgressValue = idx;
                    IsProgressIndeterminate = false;
                }
            }
        }

        AppendLog(line);
    });

    private void AppendLog(string line)
    {
        if (string.IsNullOrEmpty(LogText))
            LogText = line;
        else
            LogText += Environment.NewLine + line;
    }

    internal static List<string> ParsePatterns(string text)
    {
        var patterns = new List<string>();
        foreach (var rawLine in text.Replace(',', '\n').Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            patterns.Add(line);
        }

        return patterns;
    }

    private static void OpenPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "FiveM Clip Patcher", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
