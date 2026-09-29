using System.Drawing;

namespace Hbpos.Client.Tests;

public sealed class InnoInstallerScriptTests
{
    [Fact]
    public void Inno_script_launches_hb_pos_after_silent_update()
    {
        var script = ReadRepoFile("apps/pos-wpf/installer/inno/Hbpos.Client.Wpf.iss");

        Assert.Contains("[Run]", script);
        Assert.Contains("Filename: \"{app}\\{#AppExeName}\"", script);
        Assert.Contains("Flags: nowait runasoriginaluser", script);
        Assert.DoesNotContain("skipifsilent", script.ToLowerInvariant());
    }

    [Fact]
    public void Inno_script_reports_install_progress_to_update_window()
    {
        var script = ReadRepoFile("apps/pos-wpf/installer/inno/Hbpos.Client.Wpf.iss");
        var progressParameter = Hbpos.Updater.UpdateSession.ProgressFileArgumentName.TrimStart('/');

        Assert.Contains($"ExpandConstant('{{param:{progressParameter}|}}')", script);
        Assert.Contains("procedure CurInstallProgressChanged(CurProgress, MaxProgress: Integer);", script);
        Assert.Contains("ReportUpdateProgress('install', Percent);", script);
        Assert.Contains("ReportUpdateProgress('prepare', 0);", script);
        Assert.Contains("ReportUpdateProgress('finish', 100);", script);
        Assert.Contains("SaveStringToFile(UpdateProgressFile", script);
    }

    [Fact]
    public void Build_script_publishes_update_window_into_updater_folder()
    {
        var script = ReadRepoFile("apps/pos-wpf/scripts/Build-WpfInnoInstaller.ps1");

        Assert.Contains("apps\\pos-wpf\\src\\Hbpos.Updater\\Hbpos.Updater.csproj", script);
        Assert.Contains($"Join-Path $publishDir '{Hbpos.Client.Wpf.Services.AppUpdateProgressWindowOptions.UpdaterDirectoryName}'", script);
        Assert.Contains($"'{Hbpos.Client.Wpf.Services.AppUpdateProgressWindowOptions.UpdaterExecutableName}'", script);
        Assert.Contains("dotnet publish $updaterProjectPath", script);
        Assert.Contains("throw \"Publish output is missing the update progress window", script);
    }

    [Fact]
    public void Inno_script_uninstalls_legacy_msi_by_configured_product_codes()
    {
        var script = ReadRepoFile("apps/pos-wpf/installer/inno/Hbpos.Client.Wpf.iss");

        Assert.Contains("#ifndef LegacyMsiProductCodes", script);
        Assert.Contains("function UninstallLegacyMsiProductCode", script);
        Assert.Contains("Exec('msiexec.exe'", script);
        Assert.Contains("function PrepareToInstall", script);
    }

    [Fact]
    public void Inno_script_uses_conservative_registry_fallback_for_legacy_msi()
    {
        var script = ReadRepoFile("apps/pos-wpf/installer/inno/Hbpos.Client.Wpf.iss");

        Assert.Contains("function IsLegacyHbPosMsiEntry", script);
        Assert.Contains("DisplayName", script);
        Assert.Contains("Publisher", script);
        Assert.Contains("InstallLocation", script);
        Assert.Contains("FindAndUninstallLegacyMsiEntries", script);
        Assert.Contains("{autopf32}\\HB POS", script);
    }

    [Fact]
    public void Build_script_passes_legacy_msi_product_codes_to_inno()
    {
        var script = ReadRepoFile("apps/pos-wpf/scripts/Build-WpfInnoInstaller.ps1");

        Assert.Contains("[string[]]$LegacyMsiProductCode", script);
        Assert.Contains("$legacyMsiProductCodePattern", script);
        Assert.Contains("Legacy MSI ProductCode must be a GUID", script);
        Assert.Contains("/DLegacyMsiProductCodes=", script);
    }

    [Fact]
    public void Build_script_requires_explicit_opt_in_for_noncommercial_inno_builds()
    {
        var script = ReadRepoFile("apps/pos-wpf/scripts/Build-WpfInnoInstaller.ps1");

        Assert.Contains("[switch]$AllowNonCommercialBuild", script);
        Assert.Contains("AllowNonCommercialBuild", script);
        Assert.Contains("throw \"Inno Setup commercial license key is not active", script);
    }

    [Fact]
    public void Build_script_places_self_contained_helper_and_agent_in_all_users_publish_output()
    {
        var script = ReadRepoFile("apps/pos-wpf/scripts/Build-WpfInnoInstaller.ps1");

        Assert.Contains("Hbpos.RemoteStatus\\Hbpos.RemoteStatus.csproj", script);
        Assert.Contains("Hbpos.RemoteMaintenance.Setup\\Hbpos.RemoteMaintenance.Setup.csproj", script);
        Assert.Contains("--self-contained true", script);
        Assert.Contains("-p:PublishSingleFile=true", script);
        Assert.Contains("-p:IncludeNativeLibrariesForSelfExtract=true", script);
        Assert.Contains("Copy-Item -LiteralPath $statusExe -Destination $publishDir", script);
        Assert.Contains("Copy-Item -LiteralPath $helperExe -Destination $publishDir", script);
    }

    [Fact]
    public void Remote_publish_script_refuses_non_empty_output_instead_of_recursive_delete()
    {
        var script = ReadRepoFile("apps/pos-wpf/scripts/Publish-RemoteMaintenance.ps1");

        Assert.Contains("输出目录必须为空", script);
        Assert.DoesNotContain("Remove-Item -LiteralPath $OutputDirectory -Recurse", script);
        Assert.Contains("WpfOutputDirectory", script);
        Assert.Contains("IncludeNativeLibrariesForSelfExtract=true", script);
        Assert.Contains("ordinary", script);
    }

    [Fact]
    public void Inno_script_uses_versioned_icon_file_for_shell_shortcuts()
    {
        var script = ReadRepoFile("apps/pos-wpf/installer/inno/Hbpos.Client.Wpf.iss");
        const string versionedIconPath = "{app}\\AppIcon-{#AppVersion}.ico";

        Assert.Contains(
            "Source: \"..\\..\\src\\Hbpos.Client.Wpf\\Resources\\AppIcon.ico\"; DestDir: \"{app}\"; DestName: \"AppIcon-{#AppVersion}.ico\"",
            script);
        Assert.Equal(2, script.Split($"IconFilename: \"{versionedIconPath}\"", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("IconFilename: \"{app}\\{#AppExeName}\"", script);
    }

    [Fact]
    public void Wpf_brand_icon_is_centralized_for_executable_installer_shortcuts_and_uninstall_entry()
    {
        var project = ReadRepoFile("apps/pos-wpf/src/Hbpos.Client.Wpf/Hbpos.Client.Wpf.csproj");
        var installer = ReadRepoFile("apps/pos-wpf/installer/inno/Hbpos.Client.Wpf.iss");

        Assert.Contains("<ApplicationIcon>Resources\\AppIcon.ico</ApplicationIcon>", project);
        Assert.Contains("<Resource Include=\"Resources\\AppIcon.ico\" />", project);
        Assert.Contains("<Resource Include=\"Resources\\AppBrandIcon.png\" />", project);
        Assert.Contains("SetupIconFile=..\\..\\src\\Hbpos.Client.Wpf\\Resources\\AppIcon.ico", installer);
        Assert.Contains("UninstallDisplayIcon={app}\\{#AppExeName}", installer);
        Assert.Equal(
            2,
            installer.Split(
                "IconFilename: \"{app}\\AppIcon-{#AppVersion}.ico\"",
                StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void Wpf_app_icon_matches_ios_pos_brand_artwork()
    {
        var repoRoot = FindRepoRoot();
        var iconBytes = File.ReadAllBytes(Path.Combine(
            repoRoot,
            "apps",
            "pos-wpf",
            "src",
            "Hbpos.Client.Wpf",
            "Resources",
            "AppIcon.ico"));
        var imageCount = BitConverter.ToUInt16(iconBytes, 4);
        var entries = Enumerable.Range(0, imageCount)
            .Select(index =>
            {
                var entryOffset = 6 + (index * 16);
                return new
                {
                    Width = iconBytes[entryOffset] == 0 ? 256 : iconBytes[entryOffset],
                    Height = iconBytes[entryOffset + 1] == 0 ? 256 : iconBytes[entryOffset + 1],
                    ResourceLength = BitConverter.ToUInt32(iconBytes, entryOffset + 8),
                    ResourceOffset = BitConverter.ToUInt32(iconBytes, entryOffset + 12)
                };
            })
            .ToArray();

        var expectedFrameSizes = new[] { 16, 24, 32, 48, 64, 128, 256 };
        Assert.Equal(expectedFrameSizes, entries.Select(entry => entry.Width).Order());
        Assert.Equal(expectedFrameSizes, entries.Select(entry => entry.Height).Order());

        var largestFrame = Assert.Single(entries, entry => entry.Width == 256);
        var inAppBrandImage = File.ReadAllBytes(Path.Combine(
            repoRoot,
            "apps",
            "pos-wpf",
            "src",
            "Hbpos.Client.Wpf",
            "Resources",
            "AppBrandIcon.png"));
        Assert.Equal(
            iconBytes
                .Skip(checked((int)largestFrame.ResourceOffset))
                .Take(checked((int)largestFrame.ResourceLength)),
            inAppBrandImage);
        using var frameStream = new MemoryStream(
            iconBytes,
            checked((int)largestFrame.ResourceOffset),
            checked((int)largestFrame.ResourceLength),
            writable: false);
        using var bitmap = new Bitmap(frameStream);

        Assert.Equal(256, bitmap.Width);
        Assert.Equal(256, bitmap.Height);
        Assert.Equal(0, bitmap.GetPixel(0, 0).A);
        Assert.Equal(0, bitmap.GetPixel(bitmap.Width - 1, 0).A);
        Assert.Equal(0, bitmap.GetPixel(0, bitmap.Height - 1).A);
        Assert.Equal(0, bitmap.GetPixel(bitmap.Width - 1, bitmap.Height - 1).A);

        var bounds = FindOpaqueBounds(bitmap);
        Assert.InRange(bounds.Left, 10, 16);
        Assert.InRange(bounds.Top, 10, 16);
        Assert.InRange(bitmap.Width - 1 - bounds.Right, 10, 16);
        Assert.InRange(bitmap.Height - 1 - bounds.Bottom, 10, 16);
        Assert.InRange(Math.Abs(bounds.Left - (bitmap.Width - 1 - bounds.Right)), 0, 1);
        Assert.InRange(Math.Abs(bounds.Top - (bitmap.Height - 1 - bounds.Bottom)), 0, 1);

        using var iosIcon = new Bitmap(Path.Combine(repoRoot, "apps", "pos-ipad", "assets", "icon.png"));
        var palette = new[]
        {
            Color.FromArgb(255, 230, 90, 47),
            Color.FromArgb(255, 255, 249, 245),
            Color.FromArgb(255, 16, 37, 58)
        };
        var paletteCounts = new int[palette.Length];
        var comparablePixels = 0;
        var matchingPixels = 0;

        for (var y = bounds.Top; y <= bounds.Bottom; y++)
        {
            for (var x = bounds.Left; x <= bounds.Right; x++)
            {
                var windowsPixel = bitmap.GetPixel(x, y);
                if (windowsPixel.A < 250)
                {
                    continue;
                }

                var windowsClass = ClassifyBrandColor(windowsPixel, palette);
                if (windowsClass is null)
                {
                    continue;
                }

                paletteCounts[windowsClass.Value]++;
                var normalizedX = (double)(x - bounds.Left) / (bounds.Right - bounds.Left);
                var normalizedY = (double)(y - bounds.Top) / (bounds.Bottom - bounds.Top);
                var iosX = (int)Math.Round(normalizedX * (iosIcon.Width - 1));
                var iosY = (int)Math.Round(normalizedY * (iosIcon.Height - 1));
                var iosClass = ClassifyBrandColor(iosIcon.GetPixel(iosX, iosY), palette);
                if (iosClass is null)
                {
                    continue;
                }

                comparablePixels++;
                if (windowsClass == iosClass)
                {
                    matchingPixels++;
                }
            }
        }

        Assert.All(paletteCounts, count => Assert.True(count >= 100, "Each iOS brand color must be present in the Windows icon."));
        Assert.True(comparablePixels >= 40_000, "The normalized comparison must cover the complete brand artwork.");
        var agreement = (double)matchingPixels / comparablePixels;
        Assert.True(
            agreement >= 0.98,
            $"The Windows icon must match the normalized iOS artwork by at least 98%; actual {agreement:P2}.");
    }

    private static (int Left, int Top, int Right, int Bottom) FindOpaqueBounds(Bitmap bitmap)
    {
        var left = bitmap.Width;
        var top = bitmap.Height;
        var right = -1;
        var bottom = -1;

        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).A <= 16)
                {
                    continue;
                }

                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
            }
        }

        Assert.True(right >= left && bottom >= top, "The Windows icon must contain opaque artwork.");
        return (left, top, right, bottom);
    }

    private static int? ClassifyBrandColor(Color pixel, IReadOnlyList<Color> palette)
    {
        const int maximumSquaredDistance = 900;
        var closestIndex = -1;
        var closestDistance = int.MaxValue;

        for (var index = 0; index < palette.Count; index++)
        {
            var red = pixel.R - palette[index].R;
            var green = pixel.G - palette[index].G;
            var blue = pixel.B - palette[index].B;
            var distance = (red * red) + (green * green) + (blue * blue);
            if (distance < closestDistance)
            {
                closestDistance = distance;
                closestIndex = index;
            }
        }

        return closestDistance <= maximumSquaredDistance ? closestIndex : null;
    }

    private static string ReadRepoFile(string relativePath)
    {
        var repoRoot = FindRepoRoot();
        return File.ReadAllText(Path.Combine(repoRoot, relativePath));
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "apps", "pos-wpf")) &&
                Directory.Exists(Path.Combine(current.FullName, "services", "backend")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
