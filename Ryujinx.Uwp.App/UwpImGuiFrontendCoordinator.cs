using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.AccessCache;
using Windows.Storage.Pickers;
using Windows.Storage.Search;
using Windows.System;
using Windows.Graphics.Imaging;

namespace Ryujinx.Uwp.App
{
    internal sealed class UwpImGuiFrontendCoordinator
    {
        private const string GamesToken = "Ryubox.GamesFolder.v3";
        private const string KeysToken = "Ryubox.KeysFolder.v1";
        private const string FirmwareToken = "Ryubox.FirmwareFile.v4";
        private const string LaunchGameToken = "Ryubox.LaunchGame.v1";
        private const string CustomCoversToken = "Ryubox.CustomCoversFolder.v1";
        private const string OnboardingVersionSetting = "Ryubox.Onboarding.Version";
        private const string LanguageSetting = "Ryubox.Interface.Language";
        private const string ThemeSetting = "Ryubox.Interface.Theme";
        private const int CurrentOnboardingVersion = 1;
        private readonly UwpCoreSession _coreSession;
        private readonly List<StorageFile> _games = new List<StorageFile>();
        private bool _actionInProgress;
        private bool _catalogueReady;
        private int _lastLaunchIndex = -1;

        internal bool IsActionInProgress => _actionInProgress;
        internal event Action<string> BootPhaseChanged;
        internal event Action<string> BootFailed;

        internal UwpImGuiFrontendCoordinator(UwpCoreSession coreSession)
        {
            _coreSession = coreSession;
        }

        internal async Task InitializeAsync()
        {
            // A stale FutureAccessList token is expected when an external
            // disk is disconnected. Never let it escape the Loaded handler.
            try
            {
                UwpGraphicsHost.SetNativePtcEnabled(RyuboxCoreSettings.Load().Ptc);
                UwpGraphicsHost.SetNativeFpsEnabled(RyuboxCoreSettings.Load().ShowFpsCounter ?? true);
                // Publish the authoritative setup gate before awaiting access
                // to external folders. On Xbox those broker calls can take
                // minutes, and the native shell must not expose the dashboard
                // in the meantime.
                PublishOnboardingState();
                await RestoreGamesFolderAsync();
                await RestoreKeysFolderAsync();
                PublishRuntimeStatus();
                PublishOnboardingState();
                if (ReadLocalInt(OnboardingVersionSetting, 0) >= CurrentOnboardingVersion)
                {
                    PublishMissingRequirementsNotice();
                }
                await ProcessOneShotLaunchRequestAsync();
            }
            catch (Exception exception)
            {
                UwpFrontendLog.Error("inicializacao do frontend", exception);
                _games.Clear();
                _catalogueReady = false;
                try
                {
                    PublishCatalogue();
                    UwpGraphicsHost.SetNativeImGuiStatus(HasProdKeys() && HasTitleKeys(), HasInstalledFirmware());
                    ShowError("Frontend iniciado em modo seguro; selecione novamente a pasta de jogos se necessário.");
                }
                catch (Exception publishException)
                {
                    UwpFrontendLog.Error("recuperacao da inicializacao do frontend", publishException);
                }
            }
        }

        private async Task ProcessOneShotLaunchRequestAsync()
        {
            string requestPath = Path.Combine(ApplicationData.Current.LocalFolder.Path, "launch-once.txt");
            if (!File.Exists(requestPath))
            {
                return;
            }

            string requestedTitle = string.Empty;
            try
            {
                requestedTitle = (await FileIO.ReadTextAsync(
                    await ApplicationData.Current.LocalFolder.GetFileAsync("launch-once.txt"))).Trim();
            }
            finally
            {
                try { File.Delete(requestPath); } catch { }
            }

            if (string.IsNullOrWhiteSpace(requestedTitle))
            {
                return;
            }

            int index = _games.FindIndex(file =>
                file.Name.IndexOf(requestedTitle, StringComparison.OrdinalIgnoreCase) >= 0 ||
                GameTitle(file).IndexOf(requestedTitle, StringComparison.OrdinalIgnoreCase) >= 0);
            if (index < 0)
            {
                // A one-shot request can contain a full path from a host-side
                // test. Acquire that file through the broker and add it to the
                // current catalogue so a folder scan is not required first.
                try
                {
                    StorageFile directFile = await StorageFile.GetFileFromPathAsync(requestedTitle);
                    StorageApplicationPermissions.FutureAccessList.AddOrReplace(
                        LaunchGameToken, directFile);
                    _games.Add(directFile);
                    index = _games.Count - 1;
                    PublishCatalogue();
                }
                catch (Exception exception)
                {
                    UwpFrontendLog.Error("acesso direto do teste", exception);
                    ShowError("Teste de inicializacao: jogo nao encontrado ou sem permissao: " + requestedTitle);
                    return;
                }
            }

            UwpFrontendLog.Write("session", "Processing one-shot launch request for " + _games[index].Name);
            await StartGameAsync(index);
        }

        internal void Pump()
        {
            int fpsChange = UwpGraphicsHost.ConsumeNativeFpsChange();
            if (fpsChange >= 0)
            {
                try
                {
                    RyuboxCoreSettings settings = RyuboxCoreSettings.Load();
                    settings.ShowFpsCounter = fpsChange != 0;
                    settings.Save();
                    UwpFrontendLog.Write("settings", "Game FPS counter persisted=" + settings.ShowFpsCounter);
                }
                catch (Exception exception)
                {
                    UwpFrontendLog.Error("persist game FPS counter", exception);
                    UwpGraphicsHost.SetNativeFpsEnabled(RyuboxCoreSettings.Load().ShowFpsCounter ?? true);
                }
            }
            // Persist settings independently of the command queue. A game
            // launch in the same frame must observe the latest chosen value.
            int ptcChange = UwpGraphicsHost.ConsumeNativePtcChange();
            if (ptcChange >= 0)
            {
                try
                {
                    RyuboxCoreSettings settings = RyuboxCoreSettings.Load();
                    settings.Ptc = ptcChange != 0;
                    settings.Save();
                    UwpFrontendLog.Write("settings", "PTC persisted=" + settings.Ptc + "; applies at next game start");
                }
                catch (Exception exception)
                {
                    UwpGraphicsHost.SetNativePtcEnabled(RyuboxCoreSettings.Load().Ptc);
                    UwpFrontendLog.Error("saving PTC setting", exception);
                    ShowError("Nao foi possivel salvar a configuracao PTC.");
                }
            }
            if (_coreSession.IsRunning || _coreSession.IsGameActive)
            {
                return;
            }

            int action = UwpGraphicsHost.ConsumeNativeImGuiAction();
            if (action == -1 || _actionInProgress)
            {
                return;
            }

            _actionInProgress = true;
            // Consume the activating A/click before the next Present. Without
            // this same-frame neutralisation, a retained control is rendered
            // once more in its pressed state while an async action is already
            // opening a picker or a settings page, causing a visible flash.
            UwpGraphicsHost.SetNativeImGuiInput(default(UwpInputSnapshot));
            UwpGraphicsHost.SetNativeImGuiPointer(-float.MaxValue, -float.MaxValue, false, false, 0f);
            UwpFrontendLog.Write("input", "Native ImGui action=" + action);
            HandleActionAsync(action).ContinueWith(task =>
            {
                _actionInProgress = false;
                if (task.Exception != null)
                {
                    UwpFrontendLog.Error("executando comando ImGui", task.Exception.GetBaseException());
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private async Task HandleActionAsync(int action)
        {
            try
            {
                if (action >= 0)
                {
                    await StartGameAsync(action);
                    return;
                }

                switch (action)
                {
                    case -1000:
                        await PickGamesFolderAsync();
                        break;
                    case -1001:
                        await PickAndInstallFirmwareFileAsync();
                        break;
                    case -1003:
                        await PickAndImportKeysFolderAsync();
                        break;
                    case -1004:
                        await PickAndImportKeysFolderAsync();
                        break;
                    case -1005:
                        await RestoreGamesFolderAsync();
                        break;
                    case -1006:
                        StorageApplicationPermissions.FutureAccessList.Remove(GamesToken);
                        _games.Clear();
                        PublishCatalogue();
                        break;
                    case -1007:
                        await PickCustomCoversFolderAsync();
                        break;
                    case -1010:
                        Windows.UI.Xaml.Application.Current.Exit();
                        break;
                    case -1100:
                        SaveLocalSetting(LanguageSetting, 0);
                        PublishOnboardingState();
                        break;
                    case -1101:
                        SaveLocalSetting(LanguageSetting, 1);
                        PublishOnboardingState();
                        break;
                    case -1110:
                    case -1111:
                        if (!HasProdKeys() || !HasInstalledFirmware() ||
                            !StorageApplicationPermissions.FutureAccessList.ContainsItem(GamesToken))
                        {
                            ShowError(Localize(
                                "Conclua keys, firmware e pasta de jogos antes de continuar.",
                                "Complete keys, firmware, and games folder before continuing."));
                            break;
                        }
                        SaveLocalSetting(ThemeSetting, action == -1111 ? 1 : 0);
                        SaveLocalSetting(OnboardingVersionSetting, CurrentOnboardingVersion);
                        PublishOnboardingState();
                        break;
                }
            }
            catch (Exception exception)
            {
                UwpFrontendLog.Error("executando comando ImGui", exception);
                if (action >= 0)
                {
                    string error = "Falha ao preparar o jogo: " + FirstUsefulLine(exception.Message);
                    BootFailed?.Invoke(error);
                    ShowError(error);
                    _coreSession.IsGameActive = false;
                }
            }
            finally
            {
                PublishRuntimeStatus();
                PublishOnboardingState();
            }
        }

        private static int ReadLocalInt(string key, int fallback)
        {
            object value = ApplicationData.Current.LocalSettings.Values[key];
            if (value is int integer) return integer;
            if (value != null && int.TryParse(value.ToString(), out int parsed)) return parsed;
            return fallback;
        }

        private static void SaveLocalSetting(string key, int value)
        {
            ApplicationData.Current.LocalSettings.Values[key] = value;
        }

        private static string Localize(string portuguese, string english)
        {
            return ReadLocalInt(LanguageSetting, 0) == 1 ? english : portuguese;
        }

        private static void PublishOnboardingState()
        {
            bool completed = ReadLocalInt(OnboardingVersionSetting, 0) >= CurrentOnboardingVersion;
            int language = ReadLocalInt(LanguageSetting, 0) == 1 ? 1 : 0;
            int theme = ReadLocalInt(ThemeSetting, 0) == 1 ? 1 : 0;
            bool gamesReady = StorageApplicationPermissions.FutureAccessList.ContainsItem(GamesToken);
            UwpGraphicsHost.SetNativeImGuiOnboardingState(completed, language, theme,
                HasProdKeys(), HasInstalledFirmware(), gamesReady);
        }

        private async Task RestoreGamesFolderAsync()
        {
            if (!StorageApplicationPermissions.FutureAccessList.ContainsItem(GamesToken))
            {
                _games.Clear();
                PublishCatalogue();
                return;
            }

            try
            {
                StorageFolder folder = await StorageApplicationPermissions.FutureAccessList.GetFolderAsync(GamesToken);
                await ScanFolderAsync(folder);
            }
            catch (Exception exception)
            {
                UwpFrontendLog.Error("restaurando pasta de jogos", exception);
                StorageApplicationPermissions.FutureAccessList.Remove(GamesToken);
                _games.Clear();
                PublishCatalogue();
            }
        }

        private async Task PickGamesFolderAsync()
        {
            FolderPicker picker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.ComputerFolder,
                ViewMode = PickerViewMode.Thumbnail
            };
            picker.FileTypeFilter.Add(".nsp");
            picker.FileTypeFilter.Add(".xci");
            picker.FileTypeFilter.Add(".nca");
            picker.FileTypeFilter.Add(".nsz");
            picker.FileTypeFilter.Add(".xcz");
            StorageFolder folder = await picker.PickSingleFolderAsync();
            if (folder == null)
            {
                return;
            }

            StorageApplicationPermissions.FutureAccessList.AddOrReplace(GamesToken, folder);
            await ScanFolderAsync(folder);
        }

        private async Task ScanFolderAsync(StorageFolder folder)
        {
            QueryOptions options = new QueryOptions(CommonFileQuery.OrderByName,
                new[] { ".nsp", ".xci", ".nca", ".nsz", ".xcz" })
            {
                FolderDepth = FolderDepth.Deep
            };
            StorageFileQueryResult query = folder.CreateFileQueryWithOptions(options);
            IReadOnlyList<StorageFile> files = await query.GetFilesAsync();
            _games.Clear();
            _games.AddRange(files.OrderBy(file => file.DisplayName, StringComparer.CurrentCultureIgnoreCase));
            UwpFrontendLog.Write("library", string.Format("Catalogued {0} games from '{1}'", _games.Count, folder.Path));
            PublishCatalogue();
            // Embedded art is the baseline. A custom folder is an override,
            // not a replacement: users commonly have only a handful of
            // custom covers for a large library.
            await PublishEmbeddedCoversAsync();
            if (StorageApplicationPermissions.FutureAccessList.ContainsItem(CustomCoversToken))
            {
                try
                {
                    StorageFolder coversFolder = await StorageApplicationPermissions.FutureAccessList.GetFolderAsync(CustomCoversToken);
                    await LoadCustomCoversFromFolderAsync(coversFolder);
                }
                catch (Exception exception)
                {
                    UwpFrontendLog.Error("carregando pasta salva de capas personalizadas", exception);
                }
            }
        }

        private void PublishCatalogue()
        {
            UwpGraphicsHost.SetNativeImGuiCatalogue(_games.Select(GameTitle));
            _catalogueReady = true;
        }

        private async Task PublishEmbeddedCoversAsync()
        {
            Dictionary<string, string> index = await LoadCoverIndexAsync();
            int matchedCount = 0;
            int missingCount = 0;
            for (int gameIndex = 0; gameIndex < _games.Count; gameIndex++)
            {
                string gameTitle = GameTitle(_games[gameIndex]);
                string packageName = FindEmbeddedCover(index, gameTitle);
                if (string.IsNullOrEmpty(packageName))
                {
                    missingCount++;
                    continue;
                }

                try
                {
                    StorageFile cover = await StorageFile.GetFileFromApplicationUriAsync(
                        new Uri("ms-appx:///Assets/CoverArt/" + packageName));
                    using (var stream = await cover.OpenReadAsync())
                    {
                        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
                        const uint targetWidth = 320;
                        uint targetHeight = Math.Max(1u, (uint)Math.Round(decoder.PixelHeight * (targetWidth / (double)decoder.PixelWidth)));
                        BitmapTransform transform = new BitmapTransform
                        {
                            ScaledWidth = targetWidth,
                            ScaledHeight = targetHeight,
                            InterpolationMode = BitmapInterpolationMode.Fant
                        };
                        var pixels = await decoder.GetPixelDataAsync(
                            BitmapPixelFormat.Rgba8,
                            BitmapAlphaMode.Premultiplied,
                            transform,
                            ExifOrientationMode.RespectExifOrientation,
                            ColorManagementMode.ColorManageToSRgb);
                        byte[] rgba = pixels.DetachPixelData();
                        UwpGraphicsHost.SetNativeImGuiCover(gameIndex, rgba, targetWidth, targetHeight, targetWidth * 4u);
                        matchedCount++;
                    }
                }
                catch (Exception exception)
                {
                    UwpFrontendLog.Error("carregando capa embutida para " + gameTitle, exception);
                }
            }
            UwpFrontendLog.Write("library", string.Format(
                "Embedded covers: {0} matched, {1} without an indexed match, catalogue={2}, index={3}.",
                matchedCount, missingCount, _games.Count, index.Count));
        }

        private static string FindEmbeddedCover(Dictionary<string, string> index, string title)
        {
            string normalized = NormalizeTitle(title);
            string packageName;
            if (index.TryGetValue(normalized, out packageName)) return packageName;

            // Region/version tags in No-Intro names often differ from the
            // filenames in a user's ROM folder.
            string normalizedCore = NormalizeTitle(RemoveTrailingQualifier(title));
            if (index.TryGetValue(normalizedCore, out packageName)) return packageName;

            int bestScore = 0;
            string bestPath = null;
            foreach (KeyValuePair<string, string> pair in index)
            {
                string candidate = pair.Key;
                if (candidate.Length < 6) continue;
                if (candidate == normalized || candidate == normalizedCore) return pair.Value;

                string shorter = candidate.Length <= normalizedCore.Length ? candidate : normalizedCore;
                string longer = candidate.Length > normalizedCore.Length ? candidate : normalizedCore;
                if (shorter.Length >= 6 && longer.IndexOf(shorter, StringComparison.Ordinal) >= 0)
                {
                    int score = shorter.Length * 2 - Math.Abs(candidate.Length - normalizedCore.Length);
                    if (score > bestScore) { bestScore = score; bestPath = pair.Value; }
                }
            }

            return bestPath;
        }

        private static string RemoveTrailingQualifier(string value)
        {
            string result = (value ?? string.Empty).Trim();
            while (result.EndsWith(")", StringComparison.Ordinal))
            {
                int opening = result.LastIndexOf('(');
                if (opening <= 0) break;
                result = result.Substring(0, opening).TrimEnd();
            }
            return result;
        }

        private static async Task<Dictionary<string, string>> LoadCoverIndexAsync()
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                StorageFile file = await StorageFile.GetFileFromApplicationUriAsync(
                    new Uri("ms-appx:///Assets/CoverArt/index.tsv"));
                foreach (string line in await FileIO.ReadLinesAsync(file))
                {
                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#", StringComparison.Ordinal)) continue;
                    string[] parts = line.Split('\t');
                    if (parts.Length >= 2) result[NormalizeTitle(RemoveTrailingQualifier(parts[1]))] = parts[0];
                }
            }
            catch (Exception exception)
            {
                UwpFrontendLog.Error("lendo indice de capas", exception);
            }
            return result;
        }

        private async Task PickCustomCoversFolderAsync()
        {
            FolderPicker picker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.PicturesLibrary,
                ViewMode = PickerViewMode.Thumbnail
            };
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            StorageFolder folder = await picker.PickSingleFolderAsync();
            if (folder == null)
            {
                return;
            }

            StorageApplicationPermissions.FutureAccessList.AddOrReplace(CustomCoversToken, folder);
            await LoadCustomCoversFromFolderAsync(folder);
        }

        private async Task LoadCustomCoversFromFolderAsync(StorageFolder folder)
        {
            try
            {
                UwpGraphicsHost.SetNativeImGuiOperation("Lendo capas customizadas da pasta...", 0.05f, 1);
                QueryOptions options = new QueryOptions(CommonFileQuery.OrderByName,
                    new[] { ".png", ".jpg", ".jpeg" })
                {
                    FolderDepth = FolderDepth.Deep
                };
                StorageFileQueryResult query = folder.CreateFileQueryWithOptions(options);
                IReadOnlyList<StorageFile> coverFiles = await query.GetFilesAsync();
                UwpFrontendLog.Write("library", string.Format("Encontrados {0} arquivos de capa na pasta '{1}'", coverFiles.Count, folder.Path));

                int matchedCount = 0;
                for (int gameIndex = 0; gameIndex < _games.Count; gameIndex++)
                {
                    StorageFile gameFile = _games[gameIndex];
                    string rawTitle = GameTitle(gameFile);
                    string normalizedTitle = NormalizeTitle(rawTitle);
                    string gameTitleId = ExtractTitleId(gameFile.Name);
                    if (string.IsNullOrEmpty(gameTitleId))
                    {
                        gameTitleId = ExtractTitleId(rawTitle);
                    }

                    StorageFile matchedFile = null;
                    if (!string.IsNullOrEmpty(gameTitleId))
                    {
                        matchedFile = coverFiles.FirstOrDefault(file =>
                            file.Name.IndexOf(gameTitleId, StringComparison.OrdinalIgnoreCase) >= 0);
                    }

                    if (matchedFile == null && !string.IsNullOrEmpty(normalizedTitle))
                    {
                        matchedFile = coverFiles.FirstOrDefault(file =>
                            string.Equals(NormalizeTitle(Path.GetFileNameWithoutExtension(file.Name)), normalizedTitle, StringComparison.OrdinalIgnoreCase));
                    }

                    if (matchedFile == null && !string.IsNullOrEmpty(normalizedTitle))
                    {
                        matchedFile = coverFiles.FirstOrDefault(file =>
                        {
                            string fileNorm = NormalizeTitle(Path.GetFileNameWithoutExtension(file.Name));
                            return fileNorm.Contains(normalizedTitle) || normalizedTitle.Contains(fileNorm);
                        });
                    }

                    if (matchedFile != null)
                    {
                        try
                        {
                            using (var stream = await matchedFile.OpenReadAsync())
                            {
                                BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
                                const uint targetWidth = 320;
                                uint targetHeight = Math.Max(1u, (uint)Math.Round(decoder.PixelHeight * (targetWidth / (double)decoder.PixelWidth)));
                                BitmapTransform transform = new BitmapTransform
                                {
                                    ScaledWidth = targetWidth,
                                    ScaledHeight = targetHeight,
                                    InterpolationMode = BitmapInterpolationMode.Fant
                                };
                                var pixels = await decoder.GetPixelDataAsync(
                                    BitmapPixelFormat.Rgba8,
                                    BitmapAlphaMode.Premultiplied,
                                    transform,
                                    ExifOrientationMode.RespectExifOrientation,
                                    ColorManagementMode.ColorManageToSRgb);
                                byte[] rgba = pixels.DetachPixelData();
                                UwpGraphicsHost.SetNativeImGuiCover(gameIndex, rgba, targetWidth, targetHeight, targetWidth * 4u);
                                matchedCount++;
                            }
                        }
                        catch (Exception coverException)
                        {
                            UwpFrontendLog.Error("carregando capa customizada para " + gameFile.Name, coverException);
                        }
                    }

                    float progress = 0.05f + (0.90f * (gameIndex + 1) / Math.Max(1, _games.Count));
                    UwpGraphicsHost.SetNativeImGuiOperation(
                        string.Format("Aplicando capas customizadas: {0}/{1}...", gameIndex + 1, _games.Count),
                        progress,
                        1);
                }

                UwpGraphicsHost.SetNativeImGuiOperation(
                    string.Format("Capas personalizadas aplicadas: {0} associadas.", matchedCount),
                    1.0f,
                    2);
                await Task.Delay(2000);
                UwpGraphicsHost.SetNativeImGuiOperation(string.Empty, 0.0f, 0);
            }
            catch (Exception exception)
            {
                UwpFrontendLog.Error("processamento de capas customizadas", exception);
                ShowError("Falha ao carregar capas da pasta: " + exception.Message);
            }
        }

        private async Task PickAndInstallFirmwareFileAsync()
        {
            string keysDirectory = Path.Combine(ApplicationData.Current.LocalFolder.Path, "system");
            if (!HasProdKeys())
            {
                ShowError(Localize(
                    "prod.keys não encontrado. Importe primeiro a pasta das keys.",
                    "prod.keys was not found. Import the keys folder first."));
                return;
            }

            UwpGraphicsHost.SetNativeImGuiOperation(Localize(
                "Selecione um arquivo de firmware ZIP ou XCI.",
                "Select a ZIP or XCI firmware file."), 0.05f, 1);
            FileOpenPicker picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.ComputerFolder,
                ViewMode = PickerViewMode.List
            };
            picker.FileTypeFilter.Add(".zip");
            picker.FileTypeFilter.Add(".xci");
            StorageFile selected = await picker.PickSingleFileAsync();
            if (selected == null)
            {
                UwpGraphicsHost.SetNativeImGuiOperation(string.Empty, 0.0f, 0);
                return;
            }

            UwpGraphicsHost.SetNativeImGuiOperation(Localize(
                "Copiando o firmware para o armazenamento do Ryubox...",
                "Copying firmware to Ryubox storage..."), 0.25f, 1);
            StorageFolder cache = await ApplicationData.Current.LocalFolder.CreateFolderAsync(
                "firmware-cache", CreationCollisionOption.OpenIfExists);
            StorageFile localCopy = await selected.CopyAsync(
                cache, selected.Name, NameCollisionOption.ReplaceExisting);
            UwpGraphicsHost.SetNativeImGuiOperation(Localize(
                "Validando keys e instalando arquivos NCA...",
                "Validating keys and installing NCA files..."), 0.60f, 1);
            string error = string.Empty;
            bool installed = await Task.Run(() =>
            {
                string detail;
                bool success = _coreSession.TryInstallFirmware(keysDirectory, localCopy.Path, out detail);
                error = detail;
                return success;
            });
            if (installed)
            {
                StorageApplicationPermissions.FutureAccessList.AddOrReplace(FirmwareToken, selected);
                UwpFrontendLog.Write("firmware", "Firmware installed immediately from file: " + selected.Name);
                UwpGraphicsHost.SetNativeImGuiOperation(Localize(
                    "Firmware instalado e validado com sucesso.",
                    "Firmware installed and validated successfully."), 1.0f, 2);
                PublishRuntimeStatus();
                PublishOnboardingState();
                await Task.Delay(2500);
                UwpGraphicsHost.SetNativeImGuiOperation(string.Empty, 0.0f, 0);
            }
            else
            {
                UwpFrontendLog.Write("errors", error);
                ShowError(Localize("Falha ao instalar firmware: ", "Firmware installation failed: ") +
                    FirstUsefulLine(error));
            }
        }

        private static async Task PickAndImportKeysFolderAsync()
        {
            UwpGraphicsHost.SetNativeImGuiOperation(Localize(
                "Selecione a pasta que contém prod.keys e title.keys.",
                "Select the folder containing prod.keys and title.keys."), 0.05f, 1);
            FolderPicker picker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.ComputerFolder,
                ViewMode = PickerViewMode.List
            };
            picker.FileTypeFilter.Add("*");
            StorageFolder selected = await picker.PickSingleFolderAsync();
            if (selected == null)
            {
                UwpGraphicsHost.SetNativeImGuiOperation(string.Empty, 0.0f, 0);
                return;
            }
            StorageApplicationPermissions.FutureAccessList.AddOrReplace(KeysToken, selected);
            UwpGraphicsHost.SetNativeImGuiOperation(Localize(
                "Importando e validando as keys...",
                "Importing and validating keys..."), 0.55f, 1);
            StorageFolder system = await ApplicationData.Current.LocalFolder.CreateFolderAsync(
                "system", CreationCollisionOption.OpenIfExists);
            IReadOnlyList<StorageFile> files = await selected.GetFilesAsync();
            int imported = 0;
            foreach (string destinationName in new[] { "prod.keys", "title.keys" })
            {
                StorageFile source = files.FirstOrDefault(file =>
                    string.Equals(file.Name, destinationName, StringComparison.OrdinalIgnoreCase));
                if (source == null) continue;
                await source.CopyAsync(system, destinationName, NameCollisionOption.ReplaceExisting);
                imported++;
            }
            UwpFrontendLog.Write("keys", imported > 0
                ? imported + " arquivo(s) de keys importado(s) da pasta " + selected.Path
                : "A pasta selecionada não contém prod.keys/title.keys: " + selected.Path);
            bool prodReady = HasProdKeys();
            bool titleReady = HasTitleKeys();
            if (!prodReady || !titleReady)
            {
                ShowError(!prodReady && !titleReady
                    ? "Erro: prod.keys e title.keys não encontrados na pasta selecionada."
                    : (!prodReady ? "Erro: prod.keys não encontrado." : "Erro: title.keys não encontrado."));
            }
            else
            {
                UwpGraphicsHost.SetNativeImGuiOperation(Localize(
                    "prod.keys e title.keys importados com sucesso.",
                    "prod.keys and title.keys imported successfully."), 1.0f, 2);
            }
        }

        private static async Task RestoreKeysFolderAsync()
        {
            if (!StorageApplicationPermissions.FutureAccessList.ContainsItem(KeysToken))
            {
                return;
            }

            try
            {
                StorageFolder source = await StorageApplicationPermissions.FutureAccessList.GetFolderAsync(KeysToken);
                StorageFolder system = await ApplicationData.Current.LocalFolder.CreateFolderAsync(
                    "system", CreationCollisionOption.OpenIfExists);
                foreach (string name in new[] { "prod.keys", "title.keys" })
                {
                    try
                    {
                        StorageFile key = await source.GetFileAsync(name);
                        await key.CopyAsync(system, name, NameCollisionOption.ReplaceExisting);
                    }
                    catch (FileNotFoundException)
                    {
                        // The selected folder may contain only one of the two key files.
                    }
                }
                UwpFrontendLog.Write("keys", "Stored keys folder restored from FutureAccessList.");
            }
            catch (Exception exception)
            {
                UwpFrontendLog.Error("restauracao automatica das keys", exception);
            }
        }

        private async Task StartGameAsync(int index)
        {
            if (index < 0 || index >= _games.Count)
            {
                UwpFrontendLog.Write("errors", "Frontend requested an invalid catalogue index: " + index);
                BootFailed?.Invoke("O jogo selecionado nao esta mais disponivel na biblioteca.");
                return;
            }
            _lastLaunchIndex = index;
            BootPhaseChanged?.Invoke("Preparando jogo...");
            string keysDirectory = Path.Combine(ApplicationData.Current.LocalFolder.Path, "system");
            if (!HasProdKeys() || !HasTitleKeys())
            {
                string message = !HasProdKeys() && !HasTitleKeys()
                    ? "Erro: prod.keys e title.keys não encontrados. Abra Configurações > Setup."
                    : (!HasProdKeys()
                        ? "Erro: prod.keys não encontrado. Abra Configurações > Setup."
                        : "Erro: title.keys não encontrado. Abra Configurações > Setup.");
                UwpFrontendLog.Write("errors", "Launch blocked: " + message);
                BootFailed?.Invoke(message);
                ShowError(message);
                return;
            }

            if (!HasInstalledFirmware())
            {
                UwpFrontendLog.Write("errors", "Launch blocked: firmware is not installed. Open Settings > Setup.");
                BootFailed?.Invoke("Erro: firmware nao instalado. Abra Configuracoes > Setup.");
                ShowError("Erro: firmware não instalado. Abra Configurações > Setup e instale um arquivo ZIP/XCI válido.");
                return;
            }

            StorageFile selectedGame = _games[index];
            BootPhaseChanged?.Invoke("Carregando arquivos do jogo...");
            UwpGraphicsHost.SetNativeImGuiOperation("Validando acesso a " + GameTitle(selectedGame) + "...", 0.15f, 1);

            // Folder tokens are sufficient for StorageFile enumeration, but a
            // NativeAOT core opening the path through FileStream also needs the
            // selected file registered and activated through the broker. Keep
            // the brokered read alive until LoadNsp has opened its own handle.
            StorageApplicationPermissions.FutureAccessList.AddOrReplace(LaunchGameToken, selectedGame);
            StorageFile launchFile = await StorageApplicationPermissions.FutureAccessList.GetFileAsync(LaunchGameToken);
            string error;
            bool started;
            if (!StorageFileHandleAccess.TryOpenRead(
                launchFile, out IntPtr gameReadHandle, out string handleError))
            {
                UwpFrontendLog.Write("errors", handleError);
                BootFailed?.Invoke("Falha ao iniciar o jogo: " + handleError);
                ShowError("Falha ao iniciar o jogo: " + handleError);
                return;
            }

            try
            {
                using (var brokeredRead = await launchFile.OpenReadAsync())
                {
                if (brokeredRead.Size == 0)
                {
                    BootFailed?.Invoke("Falha ao iniciar o jogo: o arquivo selecionado esta vazio.");
                    ShowError("Falha ao iniciar o jogo: o arquivo selecionado estÃ¡ vazio.");
                    return;
                }

                UwpFrontendLog.Write("session", string.Format(
                    "Brokered game handle activated: name='{0}', path='{1}', size={2}, handle=0x{3:X}",
                    launchFile.Name,
                    launchFile.Path,
                    brokeredRead.Size,
                    gameReadHandle.ToInt64()));
                UwpGraphicsHost.SetNativeImGuiOperation("Iniciando " + GameTitle(selectedGame) + "...", 0.35f, 1);
                BootPhaseChanged?.Invoke("Carregando shaders...");
                BootPhaseChanged?.Invoke("Iniciando o convidado...");
                ulong limit = MemoryManager.AppMemoryUsageLimit;
                Task<Tuple<bool, string>> startTask = Task.Run(() =>
                {
                    string detail;
                    bool success = _coreSession.TryStart(
                        launchFile.Path,
                        gameReadHandle,
                        keysDirectory,
                        string.Empty,
                        UwpGraphicsHost.ActiveBackend,
                        limit,
                        limit,
                        RyuboxCoreSettings.Load(),
                        out detail);
                    return Tuple.Create(success, detail);
                });
                float launchProgress = 0.35f;
                while (!startTask.IsCompleted)
                {
                    await Task.WhenAny(startTask, Task.Delay(300));
                    if (!startTask.IsCompleted)
                    {
                        launchProgress = Math.Min(0.92f, launchProgress + 0.02f);
                        UwpGraphicsHost.SetNativeImGuiOperation(
                            "Carregando " + GameTitle(selectedGame) + "...",
                            launchProgress,
                            1);
                    }
                }

                Tuple<bool, string> startResult = await startTask;
                started = startResult.Item1;
                error = startResult.Item2;
                }
            }
            finally
            {
                // StartWithHandle takes an owned duplicate before returning.
                StorageFileHandleAccess.Close(gameReadHandle);
            }
            UwpFrontendLog.Write("session", started ? "Premium frontend launched " + selectedGame.Name : error);
            if (started)
            {
                _coreSession.IsGameActive = true;
                BootPhaseChanged?.Invoke("Aguardando o primeiro quadro visivel...");
                UwpGraphicsHost.SetNativeImGuiOperation("Jogo iniciado", 1.0f, 1);
                await Task.Delay(250);
                UwpGraphicsHost.SetNativeImGuiOperation(string.Empty, 0.0f, 0);
            }
            else
            {
                _coreSession.IsGameActive = false;
                string launchError = "Falha ao iniciar o jogo: " + FirstUsefulLine(error);
                BootFailed?.Invoke(launchError);
                ShowError(launchError);
            }
        }

        internal async Task RetryLastGameAsync()
        {
            if (_actionInProgress || _lastLaunchIndex < 0 || _lastLaunchIndex >= _games.Count)
            {
                return;
            }

            await StartGameAsync(_lastLaunchIndex);
        }

        private static bool HasInstalledFirmware()
        {
            string registered = Path.Combine(ApplicationData.Current.LocalFolder.Path,
                "bis", "system", "Contents", "registered");
            if (!Directory.Exists(registered)) return false;
            return Directory.EnumerateFiles(registered, "*.nca", SearchOption.AllDirectories).Any()
                || Directory.EnumerateDirectories(registered, "*.nca", SearchOption.AllDirectories)
                    .Any(directory => File.Exists(Path.Combine(directory, "00")));
        }

        private static bool HasProdKeys()
        {
            return File.Exists(Path.Combine(ApplicationData.Current.LocalFolder.Path, "system", "prod.keys"));
        }

        private static bool HasTitleKeys()
        {
            return File.Exists(Path.Combine(ApplicationData.Current.LocalFolder.Path, "system", "title.keys"));
        }

        private void PublishRuntimeStatus()
        {
            if (!_catalogueReady)
            {
                PublishCatalogue();
            }
            UwpGraphicsHost.SetNativeImGuiStatus(HasProdKeys() && HasTitleKeys(), HasInstalledFirmware());
        }

        private static void PublishMissingRequirementsNotice()
        {
            if (!HasProdKeys() || !HasTitleKeys())
            {
                ShowError(!HasProdKeys() && !HasTitleKeys()
                    ? "Erro: prod.keys e title.keys não encontrados."
                    : (!HasProdKeys() ? "Erro: prod.keys não encontrado." : "Erro: title.keys não encontrado."));
            }
            else if (!HasInstalledFirmware())
            {
                ShowError("Erro: firmware não instalado. Abra Configurações > Setup.");
            }
        }

        private static void ShowError(string message)
        {
            UwpGraphicsHost.SetNativeImGuiOperation(message, 0.0f, 3);
        }

        private static string FirstUsefulLine(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "erro desconhecido";
            return value.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .FirstOrDefault(line => line.Length > 0) ?? "erro desconhecido";
        }

        private static string GameTitle(StorageFile file)
        {
            string title = Path.GetFileNameWithoutExtension(file.Name);
            int bracket = title.IndexOf('[');
            if (bracket > 0) title = title.Substring(0, bracket);
            return title.Trim();
        }

        internal void ShowErrorNotification(string message)
        {
            ShowError(message);
        }

        private static string ExtractTitleId(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return string.Empty;
            int start = -1;
            int count = 0;
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                bool isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (isHex)
                {
                    if (start < 0) start = i;
                    count++;
                    if (count == 16)
                    {
                        bool prevSafe = start == 0 || !char.IsLetterOrDigit(name[start - 1]);
                        bool nextSafe = (i + 1 == name.Length) || !char.IsLetterOrDigit(name[i + 1]);
                        if (prevSafe && nextSafe)
                        {
                            return name.Substring(start, 16).ToUpperInvariant();
                        }
                    }
                }
                else
                {
                    start = -1;
                    count = 0;
                }
            }
            return string.Empty;
        }

        private static string NormalizeTitle(string value)
        {
            string decomposed = (value ?? string.Empty).Normalize(NormalizationForm.FormD);
            StringBuilder result = new StringBuilder(decomposed.Length);
            foreach (char character in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
                if (char.IsLetterOrDigit(character)) result.Append(char.ToLowerInvariant(character));
            }
            return result.ToString();
        }
    }
}
