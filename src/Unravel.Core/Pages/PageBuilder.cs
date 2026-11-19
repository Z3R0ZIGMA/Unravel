using System.Globalization;
using Unravel.Core.Collectors;
using Unravel.Core.Diagnostics;
using Unravel.Core.Infrastructure;
using Unravel.Core.Models;

namespace Unravel.Core.Pages;

/// <summary>Builds the Device, Hardware, OS and Security pages from a (possibly still running) scan.</summary>
public static class PageBuilder
{
    private static readonly CultureInfo Culture = CultureInfo.CurrentCulture;

    // ================================================================ Device (plan.md 4.2)

    public static PageModel Device(ScanResult scan, ICollection<string> pending)
    {
        var d = scan.Device;
        if (d is null)
        {
            return new PageModel("Device", null, [Cards.Pending("Identity", Sections.Device, pending, scan)]);
        }

        var identity = new CardModel
        {
            Title = "Identity",
            Fields =
            [
                new("Make", OemValues.OrNotSet(d.Make)),
                new("Model", d.FriendlyModel is null ? OemValues.OrNotSet(d.Model) : d.FriendlyModel),
                .. d.FriendlyModel is null ? Array.Empty<PageField>() : [new PageField("Machine type", OemValues.OrNotSet(d.Model), true)],
                new("Manufacturer (as reported)", OemValues.OrNotSet(d.Manufacturer)),
                new("Serial number / service tag", OemValues.OrNotSet(d.SerialNumber), d.SerialNumber is not null),
                new("SKU / product number", OemValues.OrNotSet(d.Sku), d.Sku is not null),
                new("UUID", OemValues.OrNotSet(d.Uuid), d.Uuid is not null),
                new("Type", d.ChassisText + (d.ChassisCode is { } c ? $" (chassis type {c})" : "")),
                new("Virtual machine", d.IsVirtualMachine ? $"Yes ({d.VmPlatform})" : "No"),
            ],
            Actions = Warranty(d),
            Notes = Cards.Notes(scan, Sections.Device),
        };

        var id = scan.Identity;
        var names = new CardModel
        {
            Title = "Name and domain",
            Fields =
            [
                new("Computer name", d.ComputerName ?? "?", true),
                d.PartOfDomain ? new PageField("Domain", d.Domain ?? "?", true) : new PageField("Workgroup", d.Workgroup ?? "?"),
                new("Join type", id?.JoinType ?? (pending.Contains(Sections.Identity) ? "Reading..." : "Unknown")),
                .. id?.TenantName is { } t ? [new PageField("Entra tenant", t)] : Array.Empty<PageField>(),
                .. id?.Mdm is { } m ? [new PageField("Managed by", m)] : Array.Empty<PageField>(),
            ],
        };

        var board = scan.Firmware is { } f
            ? new CardModel
            {
                Title = "Motherboard and BIOS",
                Fields =
                [
                    new("Board", Join(" ", f.BoardManufacturer, f.BoardModel) ?? OemValues.NotSet),
                    new("BIOS version", OemValues.OrNotSet(f.BiosVersion), f.BiosVersion is not null),
                    new("BIOS date", BiosDate(f, scan.StartedAt)),
                ],
            }
            : Cards.Pending("Motherboard and BIOS", Sections.Firmware, pending, scan);

        return new PageModel("Device", d.DisplayName, [identity, names, board]);
    }

    /// <summary>
    /// Warranty lookup (plan.md 4.2): the vendor page with the serial filled in where the vendor's URL allows it,
    /// otherwise copy the serial and open the vendor's lookup page.
    /// </summary>
    public static IReadOnlyList<PageAction> Warranty(DeviceInfo d)
    {
        if (d.SerialNumber is not { } serial || d.IsVirtualMachine)
        {
            return [];
        }

        var s = Uri.EscapeDataString(serial);
        PageAction? action = d.Make switch
        {
            "Dell" => new("Check warranty", PageActionKind.OpenUrl, $"https://www.dell.com/support/home/en-us/product-support/servicetag/{s}/overview",
                ToolTip: "Opens Dell's support page for this service tag"),
            "Lenovo" => new("Check warranty", PageActionKind.OpenUrl, $"https://pcsupport.lenovo.com/products/{s}/warranty",
                ToolTip: "Opens Lenovo's warranty page for this serial number"),
            "HP" => new("Check warranty", PageActionKind.CopyThenOpenUrl, serial, "https://support.hp.com/us-en/check-warranty",
                "Copies the serial number and opens HP's warranty check: paste it there"),
            "ASUS" => new("Check warranty", PageActionKind.CopyThenOpenUrl, serial, "https://www.asus.com/support/warranty-status-inquiry/",
                "Copies the serial number and opens ASUS's warranty check: paste it there"),
            "Acer" => new("Check warranty", PageActionKind.CopyThenOpenUrl, serial, "https://www.acer.com/us-en/support/warranty",
                "Copies the serial number and opens Acer's warranty check: paste it there"),
            "Microsoft" => new("Check warranty", PageActionKind.CopyThenOpenUrl, serial, "https://mybusinessservice.surface.com/",
                "Copies the serial number and opens Surface's warranty check: paste it there"),
            "MSI" => new("Check warranty", PageActionKind.CopyThenOpenUrl, serial, "https://www.msi.com/support/warranty-check",
                "Copies the serial number and opens MSI's warranty check: paste it there"),
            _ => null,
        };
        return action is null ? [] : [action];
    }

    // ================================================================ Hardware (plan.md 4.3)

    public static PageModel Hardware(ScanResult scan, ICollection<string> pending) => new("Hardware", null,
    [
        Processor(scan, pending),
        Memory(scan, pending),
        Storage(scan, pending),
        Graphics(scan, pending),
        Firmware(scan, pending),
        Network(scan, pending),
        Battery(scan, pending),
        Drivers(scan, pending),
    ]);

    private static CardModel Processor(ScanResult scan, ICollection<string> pending)
    {
        if (scan.Cpu?.Primary is not { } p)
        {
            return Cards.Pending("Processor", Sections.Cpu, pending, scan);
        }

        var cpu = scan.Cpu;
        return new CardModel
        {
            Title = "Processor",
            Subtitle = cpu.Sockets > 1 ? $"{cpu.Sockets} × {p.Name}" : p.Name,
            Fields =
            [
                new("Manufacturer", p.Manufacturer ?? "?"),
                new("Cores / threads", cpu.TotalCores is null ? "?" : $"{cpu.TotalCores} / {cpu.TotalThreads}"),
                new("Base clock", Formatting.Clock(p.BaseClockMhz)),
                new("Current clock", Formatting.Clock(p.CurrentClockMhz)),
                new("Generation", p.Generation ?? "Unknown"),
                new("L2 / L3 cache", $"{Formatting.Cache(p.L2CacheKb)} / {Formatting.Cache(p.L3CacheKb)}"),
                new("Architecture", p.Architecture ?? "?"),
                new("Socket", OemValues.OrNotSet(p.Socket)),
                new("Virtualisation", p.VirtualizationEnabled switch
                {
                    true => "Enabled in firmware",
                    false => "Disabled in firmware",
                    null => "Not reported (a hypervisor is running)",
                }, Severity: p.VirtualizationEnabled == false ? Severity.Info : null),
            ],
            Notes = Cards.Notes(scan, Sections.Cpu),
        };
    }

    private static CardModel Memory(ScanResult scan, ICollection<string> pending)
    {
        if (scan.Memory is not { } m)
        {
            return Cards.Pending("Memory", Sections.Memory, pending, scan);
        }

        return new CardModel
        {
            Title = "Memory",
            Subtitle = $"{Formatting.Memory(m.InstalledBytes)}{(m.Generation is { } g ? " " + g : "")}",
            Fields =
            [
                new("Installed", Formatting.Memory(m.InstalledBytes)),
                new("Usable by Windows", Formatting.Memory(m.UsableBytes, exact: true)),
                new("Type", m.Generation is null ? "Unknown" : m.GenerationInferred ? $"{m.Generation} (likely, from part number or speed)" : m.Generation),
                new("Slots", m.TotalSlots is { } t ? $"{m.SlotsUsed} of {t} used" : m.AllSoldered ? "Soldered to the board" : $"{m.SlotsUsed} module(s)"),
                new("Maximum supported", m.MaxCapacityBytes is { } max ? Formatting.Memory(max) : "Not reported"),
                new("Upgrade", HealthRules.MemoryUpgradeText(m) ?? "Not reported by the firmware"),
            ],
            Tables =
            [
                new(null, ["Slot", "Size", "Type", "Speed", "Form", "Maker", "Part number"],
                    m.Modules.Select(x => (IReadOnlyList<string>)
                    [
                        OemValues.OrNotSet(x.Slot ?? x.BankLabel), Formatting.Memory(x.CapacityBytes),
                        x.Generation is null ? "Unknown" : x.GenerationInferred ? $"{x.Generation} (likely)" : x.Generation,
                        x.RatedSpeed is null ? "?" : x.RunningSpeed is { } run && run != x.RatedSpeed ? $"{run} of {x.RatedSpeed} MT/s" : $"{x.RatedSpeed} MT/s",
                        x.FormFactor ?? "?", OemValues.OrNotSet(x.Manufacturer), OemValues.OrNotSet(x.PartNumber),
                    ]).ToList(),
                    "The firmware does not list the memory modules."),
            ],
            Notes = Cards.Notes(scan, Sections.Memory),
        };
    }

    private static CardModel Storage(ScanResult scan, ICollection<string> pending)
    {
        if (scan.Storage is not { } s)
        {
            return Cards.Pending("Storage", Sections.Storage, pending, scan);
        }

        return new CardModel
        {
            Title = "Storage",
            Tables =
            [
                new("Disks", ["Disk", "Model", "Type", "Size", "Partitions", "Health", "Serial number"],
                    s.Disks.Select(d => (IReadOnlyList<string>)
                    [
                        d.Number?.ToString(Culture) ?? "?", d.Model ?? "?", d.Kind, Formatting.DiskSize(d.SizeBytes),
                        (d.PartitionStyle ?? "?") + (d.IsSystemDisk ? " · Windows" : ""), d.Health.ToString(), OemValues.OrNotSet(d.SerialNumber),
                    ]).ToList()),
                new("Drives", ["Drive", "Label", "File system", "Size", "Free", "Used"],
                    s.Volumes.Select(v => (IReadOnlyList<string>)
                    [
                        v.Letter, v.Label ?? "", v.FileSystem ?? "?", Formatting.VolumeSize(v.SizeBytes), Formatting.VolumeSize(v.FreeBytes),
                        Formatting.Percent(v.PercentUsed),
                    ]).ToList()),
            ],
            Notes = [.. Cards.Notes(scan, Sections.Storage), "SMART data (temperature, wear, power-on hours) and BitLocker per drive come with administrator rights in a later phase."],
        };
    }

    private static CardModel Graphics(ScanResult scan, ICollection<string> pending)
    {
        if (scan.Graphics is not { } g)
        {
            return Cards.Pending("Graphics and displays", Sections.Graphics, pending, scan);
        }

        return new CardModel
        {
            Title = "Graphics and displays",
            Tables =
            [
                new("Graphics", ["Adapter", "Video memory", "Driver", "Driver date", "Current mode"],
                    g.Gpus.Select(x => (IReadOnlyList<string>)
                    [
                        x.Name ?? "?", x.VramBytes is { } v ? Formatting.Memory(v) : "Shared", x.DriverVersion ?? "?",
                        Formatting.Date(x.DriverDate), x.CurrentMode ?? "",
                    ]).ToList()),
                new("Displays", ["Make", "Model", "Size", "Connection", "Year", "Serial number"],
                    g.Monitors.Select(x => (IReadOnlyList<string>)
                    [
                        x.Make ?? "?", x.Model ?? "?", x.SizeInches is { } inch ? $"{inch.ToString("0.#", Culture)}\"" : "?", x.Connection ?? "?",
                        x.YearOfManufacture?.ToString(Culture) ?? "?", OemValues.OrNotSet(x.SerialNumber),
                    ]).ToList(),
                    "No displays reported."),
            ],
            Notes = Cards.Notes(scan, Sections.Graphics),
        };
    }

    private static CardModel Firmware(ScanResult scan, ICollection<string> pending)
    {
        if (scan.Firmware is not { } f)
        {
            return Cards.Pending("Motherboard and firmware", Sections.Firmware, pending, scan);
        }

        return new CardModel
        {
            Title = "Motherboard and firmware",
            Fields =
            [
                new("Board", Join(" ", f.BoardManufacturer, f.BoardModel) ?? OemValues.NotSet),
                new("Board version", OemValues.OrNotSet(f.BoardVersion)),
                new("Board serial", OemValues.OrNotSet(f.BoardSerial), f.BoardSerial is not null),
                new("BIOS", Join(" ", f.BiosVendor, f.BiosVersion) ?? OemValues.NotSet),
                new("BIOS date", BiosDate(f, scan.StartedAt), Severity: f.BiosAgeYears(scan.StartedAt) > 3 ? Severity.Info : null),
                new("Boot mode", f.BootMode ?? "Unknown", Severity: f.BootMode == "Legacy BIOS" ? Severity.Warning : null),
                new("Secure Boot", f.SecureBoot switch { true => "On", false => "Off", null => "Unknown" }, Severity: Cards.OnOff(f.SecureBoot)),
                Tpm(f.Tpm),
            ],
            Notes = Cards.Notes(scan, Sections.Firmware),
        };
    }

    private static PageField Tpm(TpmInfo? tpm)
    {
        if (tpm is null)
        {
            return new("TPM", "Unknown");
        }

        if (!tpm.Present)
        {
            return new("TPM", "Not found", Severity: Severity.Warning);
        }

        var parts = new List<string> { tpm.Version is { } v ? $"TPM {v}" : "Present" };
        if (tpm.Detailed)
        {
            parts.Add(tpm.Enabled == true && tpm.Activated != false ? "enabled" : "not enabled");
            if (tpm.Manufacturer is { } maker)
            {
                parts.Add(maker);
            }
        }

        var ok = tpm.Version == "2.0" && (!tpm.Detailed || tpm.Enabled == true);
        return new("TPM", string.Join(" · ", parts), Severity: ok ? Severity.Ok : Severity.Warning);
    }

    private static CardModel Network(ScanResult scan, ICollection<string> pending)
    {
        if (scan.Network is not { } n)
        {
            return Cards.Pending("Network", Sections.Network, pending, scan);
        }

        var wifi = n.Wifi;
        var fields = wifi is null ? [] : new List<PageField>
        {
            new("Wi-Fi network", wifi.Ssid ?? (wifi.State == "disconnected" ? "Not connected" : "Hidden by Windows"), wifi.Ssid is not null),
            new("Signal", wifi.SignalPercent is { } sig ? $"{sig}%" : "?", Severity: wifi.SignalPercent switch
            {
                >= 60 => Severity.Ok,
                >= 35 => Severity.Info,
                not null => Severity.Warning,
                _ => null,
            }),
            new("Band / channel", Join(" · ", wifi.Band, wifi.Channel is { } ch ? $"channel {ch}" : null) ?? "?"),
            new("Radio type", wifi.RadioType ?? "?"),
            new("Link rate", wifi.ReceiveRateMbps is { } rx ? $"{rx} Mbps down / {wifi.TransmitRateMbps ?? "?"} Mbps up" : "?"),
        };

        return new CardModel
        {
            Title = "Network",
            Fields = fields,
            Tables =
            [
                new(null, ["Adapter", "Type", "Status", "IP address", "Gateway", "DNS", "DHCP", "MAC address"],
                    n.Adapters.Where(a => !a.IsVirtual || a.Connected).Select(a => (IReadOnlyList<string>)
                    [
                        a.Name ?? "?", a.Kind,
                        a.Connected ? a.LinkSpeedBps is { } bps ? $"Connected · {NetworkRules.LinkSpeed(bps)}" : "Connected" : "Not connected",
                        string.Join(", ", a.IPv4), string.Join(", ", a.Gateways), string.Join(", ", a.DnsServers),
                        a.Dhcp switch { true => "On", false => "Off", null => "" }, a.MacAddress ?? "",
                    ]).ToList(),
                    "No network adapters found."),
            ],
            Notes = [.. Cards.Notes(scan, Sections.Network), .. wifi?.Note is { } note ? [note] : Array.Empty<string>()],
        };
    }

    private static CardModel Battery(ScanResult scan, ICollection<string> pending)
    {
        if (scan.Battery is not { } b)
        {
            return Cards.Pending("Battery", Sections.Battery, pending, scan);
        }

        if (!b.HasBattery)
        {
            return new CardModel { Title = "Battery", Subtitle = "No battery (desktop or virtual machine)." };
        }

        var p = b.Primary!;
        var health = p.HealthPercent;
        return new CardModel
        {
            Title = "Battery",
            Subtitle = b.Batteries.Count > 1 ? $"{b.Batteries.Count} batteries; the first is shown" : null,
            Fields =
            [
                new("Battery", Join(" · ", p.Manufacturer, p.Name) ?? "?"),
                new("Health", health is { } h ? $"{h:0}% of original capacity" : "Unknown",
                    Severity: health switch { >= 80 => Severity.Ok, >= 50 => Severity.Warning, not null => Severity.Critical, _ => null }),
                new("Design capacity", p.DesignCapacityMWh is { } dc ? $"{dc.ToString("N0", Culture)} mWh" : "?"),
                new("Full charge capacity", p.FullChargeCapacityMWh is { } fc ? $"{fc.ToString("N0", Culture)} mWh" : "?"),
                new("Charge cycles", p.CycleCount?.ToString(Culture) ?? "Not reported"),
                new("Charge now", p.ChargePercent is { } c ? $"{c}%" : "?"),
            ],
            Actions = [new("Open in Unveil", PageActionKind.OpenUnveil, ToolTip: "Unveil shows what drains the battery, its history and a full test")],
            Notes = Cards.Notes(scan, Sections.Battery),
        };
    }

    private static CardModel Drivers(ScanResult scan, ICollection<string> pending)
    {
        if (scan.Drivers is not { } d)
        {
            return Cards.Pending("Devices and drivers", Sections.Drivers, pending, scan);
        }

        return new CardModel
        {
            Title = "Devices and drivers",
            Lines = d.ProblemDevices.Count == 0
                ? [new StatusLine(Severity.Ok, "No devices with problems")]
                : d.ProblemDevices.Select(p => new StatusLine(p.IsDisabled ? Severity.Info : Severity.Warning, p.Name,
                    Join(" · ", p.DeviceClass, p.Problem))).ToList(),
            Tables = d.OldestThirdParty is { Count: > 0 } old
                ?
                [
                    new("Oldest third-party drivers", ["Device", "Provider", "Version", "Date"],
                        old.Select(x => (IReadOnlyList<string>)[x.Device, x.Provider ?? "?", x.Version ?? "?", Formatting.Date(x.Date)]).ToList()),
                ]
                : [],
            Notes = Cards.Notes(scan, Sections.Drivers),
        };
    }

    // ================================================================ OS (plan.md 4.4 and 4.7)

    public static PageModel Os(ScanResult scan, ICollection<string> pending)
    {
        var cards = new List<CardModel>();
        if (scan.Os is { } os)
        {
            cards.Add(new CardModel
            {
                Title = "Windows",
                Subtitle = os.Name,
                Fields =
                [
                    new("Edition", os.EditionId ?? "?"),
                    new("Version", os.DisplayVersion ?? "?"),
                    new("Build", os.FullBuild ?? "?", os.FullBuild is not null),
                    new("Architecture", os.Architecture ?? "?"),
                    new("Installed", Formatting.Date(os.InstallDate)),
                    new("Last restart", Formatting.DateTime(os.LastBoot)),
                    new("Uptime", Formatting.Duration(os.Uptime), Severity: os.Uptime?.TotalDays > 14 ? Severity.Info : null),
                    new("Activation", os.ActivationStatus ?? "Couldn't read", Severity: Cards.OnOff(os.IsActivated)),
                    new("License channel", os.LicenseChannel ?? "?"),
                    new("Pending restart", os.PendingReboot ? $"Yes ({string.Join(", ", os.PendingRebootReasons)})" : "No",
                        Severity: os.PendingReboot ? Severity.Warning : Severity.Ok),
                    new("Locale", os.Locale ?? "?"),
                    new("Display language", os.Language ?? "?"),
                    new("Time zone", os.TimeZone ?? "?"),
                    new("Registered owner", OemValues.OrNotSet(os.RegisteredOwner)),
                ],
                Notes = [.. Cards.Notes(scan, Sections.Os), "Update history and the end-of-support check come with the diagnostics phase."],
            });
        }
        else
        {
            cards.Add(Cards.Pending("Windows", Sections.Os, pending, scan));
        }

        if (scan.Startup is { } st)
        {
            cards.Add(new CardModel
            {
                Title = "Startup programs",
                Subtitle = $"{st.StartupItems.Count(i => i.Enabled)} on, {st.StartupItems.Count(i => !i.Enabled)} turned off",
                Tables =
                [
                    new(null, ["Name", "Status", "Publisher", "Where", "Command"],
                        st.StartupItems.OrderByDescending(i => i.Enabled).Select(i => (IReadOnlyList<string>)
                        [
                            i.Name, i.Enabled ? "On" : "Off", i.Publisher ?? "", i.Location, i.Command ?? "",
                        ]).ToList(),
                        "Nothing starts automatically."),
                ],
                Notes = Cards.Notes(scan, Sections.Startup),
            });

            var stopped = st.StoppedAutomatic.ToList();
            cards.Add(new CardModel
            {
                Title = "Services",
                Lines = stopped.Count == 0
                    ? [new StatusLine(Severity.Ok, "All automatic services are running")]
                    : stopped.Select(s => new StatusLine(s.TriggerStart ? Severity.Info : Severity.Warning,
                        $"{s.DisplayName ?? s.Name} is set to start automatically but is {s.State?.ToLowerInvariant() ?? "not running"}",
                        s.TriggerStart ? "Starts when needed (trigger start): this is normal." : null)).ToList(),
                Tables =
                [
                    new("Non-Microsoft services", ["Service", "Status", "Start", "Company", "Runs as"],
                        st.ThirdPartyServices.Select(s => (IReadOnlyList<string>)
                        [
                            s.DisplayName ?? s.Name, s.State ?? "?", s.StartMode ?? "?", s.Company ?? "", s.Account ?? "",
                        ]).ToList(),
                        "Only Microsoft services are installed."),
                ],
            });
        }
        else
        {
            cards.Add(Cards.Pending("Startup and services", Sections.Startup, pending, scan));
        }

        return new PageModel("OS", scan.Os?.Name, cards);
    }

    // ================================================================ Security & management (plan.md 4.6)

    public static PageModel Security(ScanResult scan, ICollection<string> pending)
    {
        var cards = new List<CardModel>();
        var s = scan.Security;
        if (s is not null)
        {
            var avLines = s.Antivirus.Select(a => new StatusLine(
                a.Enabled == true ? a.UpToDate == false ? Severity.Critical : Severity.Ok : Severity.Info,
                a.Name,
                Join(" · ", a.Enabled switch { true => "On", false => "Off", null => "State unknown" },
                    a.UpToDate switch { true => "definitions up to date", false => "definitions out of date", null => null }))).ToList();
            if (avLines.Count == 0)
            {
                avLines.Add(new StatusLine(null, "Windows Security Center is not available (Windows Server?)"));
            }

            var defender = s.Defender;
            var otherActive = s.ThirdPartyAntivirus.FirstOrDefault(a => a.Enabled == true);
            cards.Add(new CardModel
            {
                Title = "Antivirus",
                Subtitle = s.HasActiveAntivirus ? null : "No active antivirus protection",
                Lines = avLines,
                // With another antivirus in charge Defender switches off by design: its details would only look alarming.
                Fields = defender is null ? [] : defender.RealTimeProtection != true && otherActive is not null
                    ? [new PageField("Microsoft Defender", $"Off: {otherActive.Name} protects this PC", Severity: Severity.Info)]
                    :
                [
                    new("Defender real-time protection", Cards.YesNo(defender.RealTimeProtection, "On", "Off"),
                        Severity: defender.RealTimeProtection == true ? Severity.Ok : s.ThirdPartyAntivirus.Any(a => a.Enabled == true) ? Severity.Info : Severity.Critical),
                    new("Defender mode", defender.Mode ?? "?"),
                    new("Definitions updated", defender.SignaturesUpdated is { } u ? $"{Formatting.DateTime(u)} ({defender.SignatureAgeDays} days ago)" : "?",
                        Severity: defender.SignatureAgeDays > 7 && defender.RealTimeProtection == true ? Severity.Critical : null),
                    new("Tamper protection", Cards.YesNo(defender.TamperProtection, "On", "Off"), Severity: defender.RealTimeProtection == true ? Cards.OnOff(defender.TamperProtection, Severity.Info) : null),
                ],
                Notes = Cards.Notes(scan, Sections.Security),
            });

            cards.Add(new CardModel
            {
                Title = "Firewall",
                Fields = s.Firewall.Select(p => new PageField($"{p.Name} profile",
                    Cards.YesNo(p.Enabled, "On", "Off") + (p.SetByPolicy ? " (set by Group Policy)" : ""), Severity: Cards.OnOff(p.Enabled))).ToList(),
            });

            cards.Add(s.BitLocker is null
                ? new CardModel
                {
                    Title = "BitLocker",
                    Subtitle = "Needs administrator rights to read.",
                    Actions = scan.IsAdmin ? [] : [new PageAction("Restart as administrator", PageActionKind.RestartAsAdmin)],
                }
                : new CardModel
                {
                    Title = "BitLocker",
                    Tables =
                    [
                        new(null, ["Drive", "Protection", "Status", "Encryption"],
                            s.BitLocker.Select(v => (IReadOnlyList<string>)
                            [
                                v.Volume + (v.IsSystemVolume ? " (Windows)" : ""), Cards.YesNo(v.ProtectionOn, "On", "Off"), v.Status ?? "?", v.Method ?? "",
                            ]).ToList(),
                            "No drives can be encrypted."),
                    ],
                    Notes = ["Whether the recovery key is backed up to Entra ID cannot be read from the PC: check the device in Entra or Intune."],
                });
        }
        else
        {
            cards.Add(Cards.Pending("Antivirus and firewall", Sections.Security, pending, scan));
        }

        var f = scan.Firmware;
        cards.Add(new CardModel
        {
            Title = "Device security",
            Fields =
            [
                new("Secure Boot", f?.SecureBoot switch { true => "On", false => "Off", null => "Unknown" }, Severity: Cards.OnOff(f?.SecureBoot)),
                Tpm(f?.Tpm),
                new("Boot mode", f?.BootMode ?? "Unknown"),
                new("User Account Control", s?.UacLevel ?? "Unknown", Severity: s?.UacEnabled == false ? Severity.Warning : null),
            ],
        });

        if (scan.Identity is { } id)
        {
            cards.Add(new CardModel
            {
                Title = "Join and management",
                Subtitle = id.JoinType,
                Fields =
                [
                    new("Join type", id.JoinType),
                    .. id.DomainName is { } dn ? [new PageField("Domain", dn, true)] : Array.Empty<PageField>(),
                    new("Entra tenant", id.TenantName ?? (id.EntraJoined ? "?" : "Not Entra joined")),
                    .. id.TenantId is { } tid ? [new PageField("Tenant ID", tid, true)] : Array.Empty<PageField>(),
                    .. id.DeviceId is { } did ? [new PageField("Entra device ID", did, true)] : Array.Empty<PageField>(),
                    new("MDM", id.Mdm ?? "Not enrolled"),
                    .. id.MdmUser is { } mu ? [new PageField("Enrolled by", mu, true)] : Array.Empty<PageField>(),
                    new("Work account registered", id.WorkplaceJoined ? "Yes" : "No"),
                    new("Entra sign-in token (PRT)", Cards.YesNo(id.HasPrt, "Present", "Not present"),
                        Severity: id.EntraJoined ? Cards.OnOff(id.HasPrt) : null),
                ],
                Notes = Cards.Notes(scan, Sections.Identity),
            });

            cards.Add(new CardModel
            {
                Title = "Local administrators",
                Lines = id.LocalAdmins is null
                    ? [new StatusLine(null, "Couldn't read the Administrators group")]
                    : id.LocalAdmins.Select(a => new StatusLine(null, a.Name, a.Kind)).ToList(),
            });
        }
        else
        {
            cards.Add(Cards.Pending("Join and management", Sections.Identity, pending, scan));
        }

        if (scan.Software is { } sw && sw.RemoteAccess.Any())
        {
            cards.Add(new CardModel
            {
                Title = "Remote access tools",
                Subtitle = "Check each one is expected.",
                Lines = sw.RemoteAccess.Select(r => new StatusLine(Severity.Info, r.Name, Join(" · ", r.RemoteAccessTool, r.Version, r.Publisher))).ToList(),
            });
        }

        return new PageModel("Security", null, cards);
    }

    // ================================================================ Software highlights (plan.md 4.5)

    public static IReadOnlyList<CardModel> SoftwareHighlights(ScanResult scan, ICollection<string> pending)
    {
        if (scan.Software is not { } sw)
        {
            return [Cards.Pending("Software", Sections.Software, pending, scan)];
        }

        var cards = new List<CardModel>();
        if (sw.Office is { } o)
        {
            cards.Add(new CardModel
            {
                Title = "Microsoft 365 Apps",
                Fields =
                [
                    new("Product", o.Products ?? "?"),
                    new("Version", o.Version ?? "?", o.Version is not null),
                    new("Update channel", o.Channel ?? "Unknown"),
                    new("Bitness", o.Bitness ?? "?", Severity: o.Bitness == "32-bit" ? Severity.Info : null),
                    new("Signed-in account", o.Account ?? "Not signed in (for this user)", o.Account is not null),
                ],
            });
        }

        var avs = scan.Security?.Antivirus.Where(a => !a.IsDefender).ToList() ?? [];
        var lines = new List<StatusLine>();
        if (avs.Count > 1)
        {
            lines.Add(new StatusLine(Severity.Warning, $"{avs.Count} antivirus products installed", string.Join(", ", avs.Select(a => a.Name))));
        }

        lines.AddRange(sw.RemoteAccess.Select(r => new StatusLine(Severity.Info, $"Remote access: {r.Name}", Join(" · ", r.Version, r.Publisher))));
        lines.AddRange(sw.Duplicates.Select(d => new StatusLine(Severity.Info, $"Several versions of {d.Name}", string.Join(", ", d.Versions))));
        cards.Add(new CardModel
        {
            Title = "Highlights",
            Subtitle = $"{sw.Visible.Count()} programs and {sw.VisibleStoreApps.Count()} Store apps",
            Lines = lines.Count == 0 ? [new StatusLine(Severity.Ok, "No remote access tools, antivirus conflicts or duplicate versions found")] : lines,
            Notes = Cards.Notes(scan, Sections.Software),
        });
        return cards;
    }

    // ================================================================ helpers

    private static string BiosDate(FirmwareInfo f, DateTimeOffset now) =>
        f.BiosDate is null ? "?" : f.BiosAgeYears(now) is { } y && y >= 1 ? $"{Formatting.Date(f.BiosDate)} ({y:0} years ago)" : Formatting.Date(f.BiosDate);

    private static string? Join(string separator, params string?[] parts)
    {
        var present = parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        return present.Count == 0 ? null : string.Join(separator, present);
    }
}
