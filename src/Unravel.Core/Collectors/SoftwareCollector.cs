using System.Globalization;
using System.Text.RegularExpressions;
using Unravel.Core.Infrastructure;
using Unravel.Core.Models;

namespace Unravel.Core.Collectors;

/// <summary>
/// Installed software from the uninstall registry keys (64-bit, 32-bit and per-user) plus Store apps (plan.md 4.5).
/// Never uses Win32_Product: it is slow and makes every MSI package run a consistency check (self-repair).
/// </summary>
public sealed class SoftwareCollector : CollectorBase<SoftwareInfo>
{
    public override string Section => Sections.Software;

    private static readonly (string Key, string Scope, string Architecture)[] UninstallKeys =
    [
        (@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", "Machine", "64-bit"),
        (@"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", "Machine", "32-bit"),
        (@"HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall", "User", "64-bit"),
    ];

    protected override SoftwareInfo Collect(CollectContext ctx, CollectorNotes notes, CancellationToken ct)
    {
        var programs = new List<SoftwareEntry>();
        foreach (var (key, scope, arch) in UninstallKeys)
        {
            foreach (var sub in ctx.Source.RegistrySubKeys(key))
            {
                ct.ThrowIfCancellationRequested();
                var entry = SoftwareRules.FromUninstallKey(ctx.Source.RegistryValues($@"{key}\{sub}"), scope, arch);
                if (entry is not null)
                {
                    programs.Add(entry);
                }
            }
        }

        // The same MSI can appear under both views: keep one per name + version + scope.
        programs = programs
            .GroupBy(p => (p.Name.ToUpperInvariant(), p.Version, p.Scope))
            .Select(g => g.OrderBy(p => p.IsHidden).First())
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        if (programs.Count == 0)
        {
            notes.Warn("Couldn't read installed programs (the uninstall registry keys were empty or unreadable)");
        }

        var store = TryRows(ctx, notes, "Store apps", RowKinds.StoreApps)
            .Select(SoftwareRules.FromStorePackage)
            .OfType<SoftwareEntry>()
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return new SoftwareInfo
        {
            Programs = programs,
            StoreApps = store,
            Office = ReadOffice(ctx),
            Duplicates = SoftwareRules.Duplicates(programs.Where(p => !p.IsHidden)),
        };
    }

    private static OfficeInfo? ReadOffice(CollectContext ctx)
    {
        const string config = @"HKLM\SOFTWARE\Microsoft\Office\ClickToRun\Configuration";
        var values = ctx.Source.RegistryValues(config);
        if (values.Count == 0)
        {
            return null;
        }

        string? Get(string name) => values.TryGetValue(name, out var v) ? OemValues.Clean(v as string) : null;

        // The signed-in account: HKCU\...\Common\Identity\Identities\<id>\EmailAddress.
        const string identities = @"HKCU\Software\Microsoft\Office\16.0\Common\Identity\Identities";
        var account = ctx.Source.RegistrySubKeys(identities)
            .Select(id => RegString(ctx, $@"{identities}\{id}", "EmailAddress"))
            .FirstOrDefault(e => e is not null);

        return new OfficeInfo
        {
            Products = SoftwareRules.OfficeProducts(Get("ProductReleaseIds")),
            Version = Get("VersionToReport") ?? Get("ClientVersionToReport"),
            Channel = SoftwareRules.OfficeChannel(Get("UpdateChannel")) ?? SoftwareRules.OfficeChannel(Get("CDNBaseUrl")),
            Bitness = Get("Platform") switch
            {
                "x64" => "64-bit",
                "x86" => "32-bit",
                var other => other,
            },
            Account = account,
        };
    }
}

public static partial class SoftwareRules
{
    public static SoftwareEntry? FromUninstallKey(IReadOnlyDictionary<string, object?> v, string scope, string architecture)
    {
        string? Text(string name) => v.TryGetValue(name, out var x) && x is string s && s.Trim().Length > 0 ? s.Trim() : null;
        long? Number(string name) => v.TryGetValue(name, out var x) ? CollectorBase<SoftwareInfo>.ToLong(x) : null;

        var name = Text("DisplayName");
        if (name is null)
        {
            return null;
        }

        var releaseType = Text("ReleaseType");
        var hidden = Number("SystemComponent") == 1
                     || Text("ParentKeyName") is not null
                     || releaseType is "Update" or "Hotfix" or "Security Update" or "ServicePack"
                     || UpdateName().IsMatch(name);

        return new SoftwareEntry
        {
            Name = name,
            Version = Text("DisplayVersion"),
            Publisher = Text("Publisher"),
            InstallDate = ParseInstallDate(Text("InstallDate")),
            SizeBytes = Number("EstimatedSize") is > 0 and var kb ? (ulong)kb * 1024 : null,
            Architecture = architecture,
            Scope = scope,
            Source = "Installed",
            UninstallString = Text("QuietUninstallString") ?? Text("UninstallString"),
            IsHidden = hidden,
            RemoteAccessTool = RemoteAccessTool(name, Text("Publisher")),
        };
    }

    public static SoftwareEntry? FromStorePackage(WmiRecord p)
    {
        var id = p.RawString("Name");
        if (id is null || p.Bool("IsResource") == true)
        {
            return null;
        }

        var display = p.RawString("DisplayName");
        // Unresolved "ms-resource:" names are useless; fall back to the package name.
        if (display is null || display.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
        {
            display = id;
        }

        var signature = p.RawString("SignatureKind");
        return new SoftwareEntry
        {
            Name = display,
            Version = p.RawString("Version"),
            Publisher = p.RawString("Publisher"),
            InstallDate = DateTimeOffset.TryParse(p.RawString("InstalledDate"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) && d.Year > 2000
                ? DateOnly.FromDateTime(d.LocalDateTime) : null,
            Architecture = p.RawString("Architecture") switch
            {
                "X64" => "64-bit",
                "X86" => "32-bit",
                "Arm64" => "ARM64",
                "Neutral" => "Any",
                var a => a,
            },
            Scope = "User",
            Source = "Store",
            IsSystemPackage = p.Bool("IsFramework") == true || string.Equals(signature, "System", StringComparison.OrdinalIgnoreCase),
            RemoteAccessTool = RemoteAccessTool(display, p.RawString("Publisher")),
        };
    }

    /// <summary>"20240115" (the usual), or a date in another format some installers write.</summary>
    public static DateOnly? ParseInstallDate(string? text)
    {
        if (text is null)
        {
            return null;
        }

        if (DateOnly.TryParseExact(text, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
        {
            return d;
        }

        return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) && dt.Year > 1990 ? DateOnly.FromDateTime(dt) : null;
    }

    /// <summary>Known remote-access tools (plan.md 4.5): listed separately for security checks.</summary>
    private static readonly (string Match, string Tool)[] RemoteTools =
    [
        ("AnyDesk", "AnyDesk"),
        ("TeamViewer", "TeamViewer"),
        ("ScreenConnect", "ConnectWise ScreenConnect"),
        ("ConnectWise Control", "ConnectWise ScreenConnect"),
        ("Splashtop", "Splashtop"),
        ("RustDesk", "RustDesk"),
        ("LogMeIn", "LogMeIn"),
        ("GoTo Resolve", "GoTo Resolve"),
        ("GoToAssist", "GoToAssist"),
        ("RemotePC", "RemotePC"),
        ("Chrome Remote Desktop", "Chrome Remote Desktop"),
        ("RealVNC", "VNC"),
        ("VNC Server", "VNC"),
        ("VNC Viewer", "VNC"),
        ("TightVNC", "VNC"),
        ("UltraVNC", "VNC"),
        ("TigerVNC", "VNC"),
        ("Take Control", "N-able Take Control"),
        ("BeAnywhere", "N-able Take Control"),
        ("Advanced Monitoring Agent", "N-able N-sight agent"),
        ("Windows Agent", "N-able N-central agent"),
        ("Atera", "Atera"),
        ("NinjaOne", "NinjaOne"),
        ("NinjaRMM", "NinjaOne"),
        ("Kaseya", "Kaseya"),
        ("Datto RMM", "Datto RMM"),
        ("Zoho Assist", "Zoho Assist"),
        ("Remote Utilities", "Remote Utilities"),
        ("Parsec", "Parsec"),
        ("DWService", "DWService"),
        ("DWAgent", "DWService"),
        ("Supremo", "Supremo"),
        ("Radmin", "Radmin"),
        ("NoMachine", "NoMachine"),
        ("Action1", "Action1"),
        ("Pulseway", "Pulseway"),
        ("SimpleHelp", "SimpleHelp"),
        ("BeyondTrust", "BeyondTrust Remote Support"),
        ("Bomgar", "BeyondTrust Remote Support"),
        ("Dameware", "Dameware"),
        ("ISL Light", "ISL Online"),
        ("ShowMyPC", "ShowMyPC"),
        ("Quick Assist", "Quick Assist"),
        ("MicrosoftCorporationII.QuickAssist", "Quick Assist"),
    ];

    public static string? RemoteAccessTool(string name, string? publisher)
    {
        foreach (var (match, tool) in RemoteTools)
        {
            if (name.Contains(match, StringComparison.OrdinalIgnoreCase))
            {
                // "Windows Agent" is too generic unless it comes from N-able.
                if (match == "Windows Agent" && publisher?.Contains("N-able", StringComparison.OrdinalIgnoreCase) != true)
                {
                    continue;
                }

                return tool;
            }
        }

        return publisher?.Contains("AnyDesk", StringComparison.OrdinalIgnoreCase) == true ? "AnyDesk"
            : publisher?.Contains("TeamViewer", StringComparison.OrdinalIgnoreCase) == true ? "TeamViewer"
            : null;
    }

    /// <summary>Microsoft 365 Apps update channel from the channel GUID in UpdateChannel / CDNBaseUrl.</summary>
    public static string? OfficeChannel(string? url)
    {
        if (url is null)
        {
            return null;
        }

        var guid = url.TrimEnd('/').Split('/')[^1].ToLowerInvariant();
        return guid switch
        {
            "492350f6-3a01-4f97-b9c0-c7c6ddf67d60" => "Current Channel",
            "64256afe-f5d9-4f86-8936-8840a6a4f5be" => "Current Channel (Preview)",
            "55336b82-a18d-4dd6-b5f6-9e5095c314a6" => "Monthly Enterprise Channel",
            "7ffbc6bf-bc32-4f92-8982-f9dd17fd3114" => "Semi-Annual Enterprise Channel",
            "b8f9b850-328d-4355-9145-c59439a0c4cf" => "Semi-Annual Enterprise Channel (Preview)",
            "5440fd1f-7ecb-4221-8110-145efaa6372f" => "Beta Channel",
            "f2e724c1-748f-4b47-8fb8-8e0d210e9208" => "LTSC 2019 / 2021 (volume)",
            "5030841d-c919-4594-8d2d-84ae4f96e58e" => "LTSC 2021 (volume)",
            "7983bac0-e531-40cf-be00-fd24fe66619c" => "LTSC 2024 (volume)",
            _ => null,
        };
    }

    private static readonly Dictionary<string, string> OfficeProductNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["O365ProPlusRetail"] = "Microsoft 365 Apps for enterprise",
        ["O365BusinessRetail"] = "Microsoft 365 Apps for business",
        ["O365HomePremRetail"] = "Microsoft 365 Family / Personal",
        ["O365SmallBusPremRetail"] = "Microsoft 365 Business",
        ["ProPlus2024Volume"] = "Office LTSC Professional Plus 2024",
        ["ProPlus2021Volume"] = "Office LTSC Professional Plus 2021",
        ["ProPlus2019Volume"] = "Office Professional Plus 2019",
        ["HomeBusiness2024Retail"] = "Office Home & Business 2024",
        ["HomeBusiness2021Retail"] = "Office Home & Business 2021",
        ["HomeBusiness2019Retail"] = "Office Home & Business 2019",
        ["HomeStudent2021Retail"] = "Office Home & Student 2021",
        ["VisioProRetail"] = "Visio Professional",
        ["VisioPro2021Volume"] = "Visio LTSC Professional 2021",
        ["ProjectProRetail"] = "Project Professional",
        ["ProjectPro2021Volume"] = "Project LTSC Professional 2021",
    };

    /// <summary>"O365ProPlusRetail,VisioProRetail" to "Microsoft 365 Apps for enterprise, Visio Professional".</summary>
    public static string? OfficeProducts(string? ids)
    {
        if (ids is null)
        {
            return null;
        }

        var names = ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(id => OfficeProductNames.TryGetValue(id, out var n) ? n : id)
            .Distinct()
            .ToList();
        return names.Count == 0 ? null : string.Join(", ", names);
    }

    /// <summary>Products that legitimately install side by side and are not worth flagging.</summary>
    private static readonly string[] SideBySide =
    [
        "Redistributable", "Runtime", ".NET", "SDK", "Driver", "Windows Kit", "Microsoft Edge WebView2", "Python Launcher",
        "Visual Studio", "Update Health", "Microsoft Update", "Teams Machine-Wide", "IIS Express", "Help Viewer",
    ];

    /// <summary>The same product installed more than once with different versions (plan.md 4.5).</summary>
    public static IReadOnlyList<DuplicateGroup> Duplicates(IEnumerable<SoftwareEntry> programs) => programs
        .Where(p => !SideBySide.Any(s => p.Name.Contains(s, StringComparison.OrdinalIgnoreCase)))
        .GroupBy(p => NormalizeName(p.Name), StringComparer.OrdinalIgnoreCase)
        .Where(g => g.Key.Length > 2 && g.Select(p => p.Version ?? p.Name).Distinct().Count() > 1)
        .Select(g => new DuplicateGroup(g.First().Name, g.Select(p => p.Version ?? "?").Distinct().OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList()))
        .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
        .ToList();

    /// <summary>"Zoom Workplace (64-bit) 6.1.0" and "Zoom Workplace" to "zoom workplace".</summary>
    public static string NormalizeName(string name)
    {
        var s = Bitness().Replace(name, " ");
        s = VersionToken().Replace(s, " ");
        return Spaces().Replace(s, " ").Trim(' ', '-', '–').ToLowerInvariant();
    }

    [GeneratedRegex(@"^(Security Update|Update|Hotfix) for ", RegexOptions.IgnoreCase)]
    private static partial Regex UpdateName();

    [GeneratedRegex(@"\(?\b(x64|x86|64-bit|32-bit|amd64|arm64)\b\)?", RegexOptions.IgnoreCase)]
    private static partial Regex Bitness();

    [GeneratedRegex(@"\bv?\d+(\.\d+)+\b|\(\s*\)")]
    private static partial Regex VersionToken();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Spaces();
}
