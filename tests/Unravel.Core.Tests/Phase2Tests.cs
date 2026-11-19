using Unravel.Core.Collectors;
using Unravel.Core.Diagnostics;
using Unravel.Core.Infrastructure;
using Unravel.Core.Models;
using Unravel.Core.Pages;
using Unravel.Core.Summary;

namespace Unravel.Core.Tests;

/// <summary>Phase 2 sections replayed from the synthetic hybrid-joined Dell (Fixtures/dell-hybrid-joined-problems.json).</summary>
public class Phase2FixtureTests
{
    private const string Dell = "dell-hybrid-joined-problems.json";

    [Fact]
    public async Task EveryPhase2SectionReads_NothingFails()
    {
        // The fixture holds the Phase 2 data only (plus the Device classes); the Phase 1 hardware tests use the other fixtures.
        var scan = await FixtureScanTests.Scan(Dell);
        string[] phase2Sections =
        [
            Sections.Graphics, Sections.Firmware, Sections.Network, Sections.Battery, Sections.Software,
            Sections.Security, Sections.Identity, Sections.Startup, Sections.Drivers,
        ];
        var phase2 = scan.Results.Where(r => phase2Sections.Contains(r.Section)).ToList();
        Assert.Equal(9, phase2.Count);
        Assert.All(phase2, r => Assert.False(r.Failed, $"{r.Section}: {string.Join("; ", r.Warnings)}"));
        Assert.Empty(phase2.SelectMany(r => r.Warnings));
        Assert.False(scan.ResultFor(Sections.Device)!.Failed);
        // BitLocker and the TPM state need admin; the fixture was "captured" as a standard user.
        Assert.True(scan.NeedsAdmin);
    }

    [Fact]
    public async Task Graphics_RealVramAndMonitors()
    {
        var g = (await FixtureScanTests.Scan(Dell)).Graphics!;

        // The remote display placeholder is dropped; NVIDIA VRAM comes from the driver key, not the 4 GB-capped AdapterRAM.
        Assert.Equal(["Intel(R) Iris(R) Xe Graphics", "NVIDIA GeForce MX450"], g.Gpus.Select(x => x.Name));
        Assert.Equal(2UL * 1024 * 1024 * 1024, g.Gpus[1].VramBytes);
        Assert.Equal("1920 × 1080 @ 60 Hz", g.Gpus[0].CurrentMode);

        // Inactive monitors are skipped; the built-in panel comes first.
        Assert.Equal(2, g.Monitors.Count);
        var panel = g.Monitors[0];
        Assert.True(panel.IsInternal);
        Assert.Equal("AU Optronics", panel.Make);
        Assert.Equal("B140HAN", panel.Model); // no friendly name: product code
        Assert.Null(panel.SerialNumber);     // "0" is a placeholder
        Assert.Equal(13.9, panel.SizeInches);
        var dell = g.Monitors[1];
        Assert.Equal(("Dell", "DELL P2422H", "HDMI", "TESTMON1"), (dell.Make, dell.Model, dell.Connection, dell.SerialNumber));
        Assert.Equal(24.0, dell.SizeInches);
    }

    [Fact]
    public async Task Firmware_SecureBootOffAndTpmFromDeviceList()
    {
        var f = (await FixtureScanTests.Scan(Dell)).Firmware!;
        Assert.Equal("UEFI", f.BootMode); // from the Secure Boot state value
        Assert.False(f.SecureBoot);
        Assert.Equal("1.21.0", f.BiosVersion);
        Assert.True(f.Tpm!.Present);
        Assert.Equal("2.0", f.Tpm.Version);
        Assert.False(f.Tpm.Detailed);
    }

    [Fact]
    public async Task Network_AdaptersAndHiddenSsid()
    {
        var n = (await FixtureScanTests.Scan(Dell)).Network!;
        var wifi = n.Adapters[0];
        Assert.Equal(("Wi-Fi", true, "A0:B1:C2:D3:E4:F5"), (wifi.Kind, wifi.Connected, wifi.MacAddress));
        Assert.Equal(["10.0.0.42"], wifi.IPv4);
        Assert.Equal(["10.0.0.10", "10.0.0.11"], wifi.DnsServers);
        Assert.Equal(86_700_000UL, wifi.LinkSpeedBps);
        Assert.Null(n.Adapters.Single(a => a.Name == "Ethernet").LinkSpeedBps); // disconnected
        Assert.True(n.Adapters.Last().IsVirtual);

        Assert.Null(n.Wifi!.Ssid);
        Assert.Equal(31, n.Wifi.SignalPercent);
        Assert.Contains("location", n.Wifi.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Battery_DesignCapacityFromPowercfgWhenStaticDataIsDenied()
    {
        var b = (await FixtureScanTests.Scan(Dell)).Battery!.Primary!;
        Assert.Equal(54000, b.DesignCapacityMWh);
        Assert.Equal(32400, b.FullChargeCapacityMWh);
        Assert.Equal(60, b.HealthPercent);
        Assert.Equal(410, b.CycleCount); // WMI said 0 (not reported): the report's count is used
        Assert.Equal(64, b.ChargePercent);
    }

    [Fact]
    public async Task Software_RegistryStoreOfficeAndHighlights()
    {
        var sw = (await FixtureScanTests.Scan(Dell)).Software!;

        Assert.Equal(["Google Chrome", "TeamViewer", "Zoom Workplace", "Zoom Workplace (64-bit)"], sw.Visible.Select(p => p.Name));
        Assert.Contains(sw.Programs, p => p is { Name: "Intel Hidden Component", IsHidden: true });
        Assert.Contains(sw.Programs, p => p.Name.StartsWith("Security Update", StringComparison.Ordinal) && p.IsHidden);
        Assert.DoesNotContain(sw.Programs, p => p.Version == "1.0" && p.Name.Length == 0);

        var chrome = sw.Programs.Single(p => p.Name == "Google Chrome");
        Assert.Equal(new DateOnly(2026, 11, 1), chrome.InstallDate);
        Assert.Equal(500UL * 1024 * 1024, chrome.SizeBytes);
        var teamViewer = sw.Programs.Single(p => p.Name == "TeamViewer");
        Assert.Equal(("32-bit", "TeamViewer", new DateOnly(2025, 7, 4)), (teamViewer.Architecture, teamViewer.RemoteAccessTool, teamViewer.InstallDate));
        Assert.Equal("User", sw.Programs.Single(p => p.Name == "Zoom Workplace").Scope);

        // Store: resource packages dropped, frameworks/system apps hidden, ms-resource names fall back to the package name.
        Assert.Equal(["Quick Assist", "SpotifyAB.SpotifyMusic"], sw.VisibleStoreApps.Select(a => a.Name));
        Assert.Equal(4, sw.StoreApps.Count);

        Assert.Equal(["TeamViewer", "Quick Assist"], sw.RemoteAccess.Select(r => r.RemoteAccessTool));
        var zoom = Assert.Single(sw.Duplicates);
        Assert.Equal(["5.17.11", "6.1.0"], zoom.Versions);

        var office = sw.Office!;
        Assert.Equal("Microsoft 365 Apps for enterprise, Visio Professional", office.Products);
        Assert.Equal(("Semi-Annual Enterprise Channel", "32-bit", "alex@contoso.com"), (office.Channel, office.Bitness, office.Account));
    }

    [Fact]
    public async Task Security_AvFirewallUac()
    {
        var s = (await FixtureScanTests.Scan(Dell)).Security!;
        Assert.Equal([false, true, true], s.Antivirus.Select(a => a.Enabled == true));
        Assert.True(s.HasActiveAntivirus);
        Assert.Equal("Passive", s.Defender!.Mode);
        Assert.Equal([true, true, false], s.Firewall.Select(f => f.Enabled == true));
        Assert.Null(s.BitLocker);
        Assert.Equal("Notify when apps make changes (default)", s.UacLevel);
    }

    [Fact]
    public async Task Identity_HybridJoinedWithIntune()
    {
        var id = (await FixtureScanTests.Scan(Dell)).Identity!;
        Assert.Equal("Hybrid Entra joined", id.JoinType);
        Assert.Equal(("CONTOSO", "Contoso Ltd", "11111111-2222-3333-4444-555555555555"), (id.DomainName, id.TenantName, id.TenantId));
        Assert.Equal(("Intune", "alex@contoso.com"), (id.Mdm, id.MdmUser)); // "Local Authority" is not an MDM
        Assert.True(id.HasPrt);
        Assert.Equal(["LAPTOP-042\\Administrator", "CONTOSO\\Domain Admins"], id.LocalAdmins!.Select(a => a.Name));
    }

    [Fact]
    public async Task Startup_ApprovalStatePublishersAndServices()
    {
        var st = (await FixtureScanTests.Scan(Dell)).Startup!;
        var byName = st.StartupItems.ToDictionary(i => i.Name);
        Assert.False(byName["Spotify"].Enabled);  // turned off in Task Manager
        Assert.True(byName["Teams"].Enabled);
        Assert.True(byName["SecurityHealth"].Enabled); // never touched: no approval value
        Assert.Equal("Microsoft Corporation", byName["Teams"].Publisher);
        Assert.Equal("Scheduled task (at logon)", byName["Contoso Updater"].Location);
        Assert.False(byName["Contoso Updater"].Enabled);

        // LSM had no path from WMI: the service key's ImagePath shows it is Windows' own.
        Assert.Equal(["Contoso Backup", "Sophos Agent"], st.ThirdPartyServices.Select(s => s.DisplayName));
        Assert.Equal(["Contoso Backup", "Microsoft Edge Update Service (edgeupdate)"], st.StoppedAutomatic.Select(s => s.DisplayName));
        Assert.Equal(["Contoso Backup"], st.UnexpectedlyStopped.Select(s => s.DisplayName));
        Assert.Equal("Automatic (delayed)", st.StoppedAutomatic[1].StartMode);
    }

    [Fact]
    public async Task Drivers_ProblemsAndOldest()
    {
        var d = (await FixtureScanTests.Scan(Dell)).Drivers!;
        // Code 45 (unplugged) is not a problem; disabled devices sort last.
        Assert.Equal([28, 22], d.ProblemDevices.Select(p => p.ErrorCode));
        Assert.Equal("Drivers are not installed (code 28)", d.ProblemDevices[0].Problem);
        // Microsoft drivers and the 2006-06-21 placeholder date are left out.
        Assert.Equal(["Realtek Audio", "Intel Wi-Fi"], d.OldestThirdParty!.Select(x => x.Device));
    }

    [Fact]
    public async Task Findings_CoverEveryPlantedProblem()
    {
        var findings = HealthRules.Evaluate(await FixtureScanTests.Scan(Dell));
        void Has(Severity severity, string area, string text) =>
            Assert.Contains(findings, f => f.Severity == severity && f.Area == area && f.Message.Contains(text, StringComparison.Ordinal));

        Has(Severity.Warning, HealthRules.SecurityArea, "More than one antivirus is active (Sophos Intercept X, Norton 360)");
        Has(Severity.Warning, HealthRules.SecurityArea, "Firewall is off for the Public profile");
        Has(Severity.Info, HealthRules.SecurityArea, "Remote access tools installed: TeamViewer, Quick Assist");
        Has(Severity.Warning, HealthRules.BatteryArea, "Battery holds 60% of its original capacity after 410 cycles");
        Has(Severity.Warning, HealthRules.FirmwareArea, "Secure Boot is off");
        Has(Severity.Info, HealthRules.FirmwareArea, "BIOS is 6 years old");
        Has(Severity.Warning, HealthRules.DevicesArea, "Device problem: PCI\\VEN_10EC&DEV_5260");
    }

    [Fact]
    public async Task HealthStrip_SecurityAndBattery()
    {
        var health = SummaryBuilder.Build(await FixtureScanTests.Scan(Dell)).Health.ToDictionary(h => h.Area);
        Assert.Equal(Severity.Warning, health["Security"].Severity);
        Assert.Equal(Severity.Warning, health["Battery"].Severity);
        Assert.Null(health["Windows 11"].Severity);
    }

    [Theory]
    [InlineData("asus-expertbook-b1403cva.json")]
    [InlineData("whitebox-desktop-problems.json")]
    [InlineData("hyperv-vm-limited.json")]
    [InlineData(Dell)]
    public async Task Pages_BuildForEveryFixture(string fixture)
    {
        var scan = await FixtureScanTests.Scan(fixture);
        string[] none = [];
        foreach (var page in new[] { PageBuilder.Device(scan, none), PageBuilder.Hardware(scan, none), PageBuilder.Os(scan, none), PageBuilder.Security(scan, none) })
        {
            Assert.NotEmpty(page.Cards);
            Assert.All(page.Cards, c => Assert.NotEqual("Reading...", c.State));
        }

        Assert.NotEmpty(PageBuilder.SoftwareHighlights(scan, none));
    }

    [Fact]
    public async Task Pages_ShowReadingWhileSectionsArePending()
    {
        var scan = new ScanResult { StartedAt = DateTimeOffset.Now };
        var pending = ScanRunner.CreateDefault().Sections.ToList();
        var hardware = PageBuilder.Hardware(scan, pending);
        Assert.All(hardware.Cards, c => Assert.Equal("Reading...", c.State));
        Assert.Equal("Reading...", Assert.Single(PageBuilder.Device(scan, pending).Cards).State);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Warranty_PrefillsWhereTheVendorAllows()
    {
        var device = (await FixtureScanTests.Scan(Dell)).Device!;
        var action = Assert.Single(PageBuilder.Warranty(device));
        Assert.Equal(PageActionKind.OpenUrl, action.Kind);
        Assert.Contains("/servicetag/TESTTAG/", action.Argument, StringComparison.Ordinal);

        var hp = PageBuilder.Warranty(device with { Make = "HP" }).Single();
        Assert.Equal((PageActionKind.CopyThenOpenUrl, "TESTTAG"), (hp.Kind, hp.Argument));
        Assert.Empty(PageBuilder.Warranty(device with { SerialNumber = null }));
        Assert.Empty(PageBuilder.Warranty(device with { VmPlatform = "Hyper-V" }));
    }
}

public class Phase2RuleTests
{
    [Theory]
    [InlineData(397568L, true, true)]   // 0x61100 Defender on, up to date
    [InlineData(393472L, false, true)]  // 0x60100 off
    [InlineData(397584L, true, false)]  // 0x61110 on, out of date
    public void SecurityCenterProductState(long state, bool enabled, bool upToDate)
    {
        Assert.Equal(enabled, SecurityRules.AvEnabled(state));
        Assert.Equal(upToDate, SecurityRules.AvUpToDate(state));
    }

    [Theory]
    [InlineData(1L, 2L, 1L, "Always notify")]
    [InlineData(1L, 5L, 0L, "Notify when apps make changes, without dimming")]
    [InlineData(1L, 0L, 0L, "Never notify")]
    [InlineData(0L, 5L, 1L, "Off (UAC disabled)")]
    public void Uac(long lua, long consent, long secure, string expected) => Assert.Equal(expected, SecurityRules.UacLevel(lua, consent, secure));

    [Fact]
    public void Dsregcmd_FirstValueWins()
    {
        var v = IdentityRules.ParseDsregcmd("  AzureAdJoined : YES\r\n  TenantName : A : B\r\n  AzureAdJoined : NO\r\n");
        Assert.Equal("YES", v["AzureAdJoined"]);
        Assert.Equal("A : B", v["TenantName"]);
        Assert.Equal("Workgroup", IdentityRules.JoinType(false, false));
        Assert.Equal("Entra joined", IdentityRules.JoinType(true, false));
    }

    [Fact]
    public void Netsh_ConnectedWithSsid()
    {
        var w = NetworkRules.ParseWlan("    Name : Wi-Fi\n    State : connected\n    SSID : Office 5G\n    BSSID : aa:bb\n    Band : 5 GHz\n    Signal : 88%\n    Name : Wi-Fi 2\n    SSID : Other\n");
        Assert.Equal(("Office 5G", 88, "5 GHz"), (w.Ssid, w.SignalPercent, w.Band));
        Assert.Null(w.Note);
    }

    [Fact]
    public void BatteryReport_ParsesAnyNamespace()
    {
        var b = Assert.Single(BatteryRules.ParseReport("<BatteryReport><Batteries><Battery><Id>X</Id><DesignCapacity>50000</DesignCapacity>" +
                                                       "<FullChargeCapacity>45000</FullChargeCapacity><CycleCount>0</CycleCount></Battery></Batteries></BatteryReport>"));
        Assert.Equal((50000L, 45000L, (int?)null), (b.DesignCapacityMWh!.Value, b.FullChargeCapacityMWh!.Value, b.CycleCount));
        Assert.Equal(90, b.HealthPercent);
        Assert.Empty(BatteryRules.ParseReport("not xml"));
        Assert.Empty(BatteryRules.ParseReport(null));
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\X\\x.exe\" /background", "C:\\Program Files\\X\\x.exe")]
    [InlineData("C:\\Tools\\y.exe -m", "C:\\Tools\\y.exe")]
    [InlineData("C:\\Program Files\\Z\\z.exe --flag", "C:\\Program Files\\Z\\z.exe")]
    [InlineData("\\??\\C:\\Windows\\a.exe", "C:\\Windows\\a.exe")]
    public void ExecutablePath(string command, string expected) => Assert.Equal(expected, StartupRules.ExecutablePath(command));

    [Fact]
    public void ExecutablePath_ExpandsSystemRoot()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert.Equal(Path.Combine(windows, @"system32\drivers\x.sys"), StartupRules.ExecutablePath(@"\SystemRoot\system32\drivers\x.sys"));
    }

    [Fact]
    public void StartupApproved()
    {
        Assert.True(StartupRules.IsApproved(null));
        Assert.True(StartupRules.IsApproved(new byte[] { 2, 0 }));
        Assert.True(StartupRules.IsApproved(new byte[] { 6, 0 }));
        Assert.False(StartupRules.IsApproved(new byte[] { 3, 0 }));
        Assert.False(StartupRules.IsApproved(new object[] { 7L, 0L }));
    }

    [Theory]
    [InlineData("AnyDesk", null, "AnyDesk")]
    [InlineData("ScreenConnect Client (abc123)", null, "ConnectWise ScreenConnect")]
    [InlineData("Windows Agent", "N-able Technologies", "N-able N-central agent")]
    [InlineData("Windows Agent", "Someone Else", null)]
    [InlineData("Some Helper", "TeamViewer Germany GmbH", "TeamViewer")]
    [InlineData("Google Chrome", "Google LLC", null)]
    public void RemoteAccess(string name, string? publisher, string? expected) => Assert.Equal(expected, SoftwareRules.RemoteAccessTool(name, publisher));

    [Theory]
    [InlineData("Zoom Workplace (64-bit) 6.1.0", "zoom workplace")]
    [InlineData("7-Zip 24.08 (x64)", "7-zip")]
    [InlineData("Everything 1.4.1.1030 (x64)", "everything")]
    public void NormalizeName(string name, string expected) => Assert.Equal(expected, SoftwareRules.NormalizeName(name));

    [Fact]
    public void Duplicates_IgnoreSideBySideRuntimes()
    {
        SoftwareEntry E(string name, string version) => new() { Name = name, Version = version, Scope = "Machine", Source = "Installed" };
        var dups = SoftwareRules.Duplicates(
        [
            E("Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.38", "14.38"),
            E("Microsoft Visual C++ 2015-2022 Redistributable (x86) - 14.36", "14.36"),
            E("Java 8 Update 401", "8.0.401"),
            E("Notepad++ (64-bit x64)", "8.6"),
            E("Notepad++ (32-bit x86)", "8.5"),
        ]);
        Assert.Equal(["8.5", "8.6"], Assert.Single(dups).Versions);
    }

    [Theory]
    [InlineData("http://officecdn.microsoft.com/pr/492350f6-3a01-4f97-b9c0-c7c6ddf67d60", "Current Channel")]
    [InlineData("http://officecdn.microsoft.com/pr/55336B82-A18D-4DD6-B5F6-9E5095C314A6/", "Monthly Enterprise Channel")]
    [InlineData("http://example.com/unknown", null)]
    public void OfficeChannel(string url, string? expected) => Assert.Equal(expected, SoftwareRules.OfficeChannel(url));

    [Theory]
    [InlineData("20240115", 2024, 1, 15)]
    [InlineData("7/4/2025", 2025, 7, 4)]
    public void InstallDate(string text, int y, int m, int d) => Assert.Equal(new DateOnly(y, m, d), SoftwareRules.ParseInstallDate(text));

    [Fact]
    public void Graphics()
    {
        Assert.Equal("DELL U2723QE", GraphicsRules.EdidString([68, 69, 76, 76, 32, 85, 50, 55, 50, 51, 81, 69, 0, 0]));
        Assert.Null(GraphicsRules.EdidString([0, 0, 0]));
        Assert.Equal("Samsung", GraphicsRules.MonitorMake("SAM"));
        Assert.Equal("XYZ", GraphicsRules.MonitorMake("XYZ"));
        Assert.Equal(27.2, GraphicsRules.Diagonal(60, 34));
        Assert.Null(GraphicsRules.Diagonal(0, 0));
        Assert.True(GraphicsRules.IsInternal(0x80000000));
        Assert.Equal("DisplayPort", GraphicsRules.Connection(10));
        Assert.True(GraphicsRules.IsPlaceholderAdapter("Microsoft Basic Display Adapter"));
    }

    [Theory]
    [InlineData("001A2B3C4D5E", "00:1A:2B:3C:4D:5E")]
    [InlineData("00-1a-2b-3c-4d-5e", "00:1A:2B:3C:4D:5E")]
    public void Mac(string raw, string expected) => Assert.Equal(expected, NetworkRules.Mac(raw));

    [Theory]
    [InlineData(9, false, null, "Wi-Fi")]
    [InlineData(14, false, null, "Ethernet")]
    [InlineData(14, true, null, "Virtual")]
    [InlineData(0, false, "Intel(R) Wireless-AC 9560", "Wi-Fi")]
    public void AdapterKind(int medium, bool isVirtual, string? description, string expected) =>
        Assert.Equal(expected, NetworkRules.Kind(medium, isVirtual, description));

    [Theory]
    [InlineData("2.0, 0, 1.59", "2.0")]
    [InlineData("Trusted Platform Module 2.0", "2.0")]
    [InlineData("1.2", "1.2")]
    [InlineData("Unknown", null)]
    public void TpmVersion(string text, string? expected) => Assert.Equal(expected, FirmwareRules.TpmVersion(text));

    [Fact]
    public void FixtureKeys_IncludeTheFilter_AndOldFixturesStillWork()
    {
        var fixture = new SystemFixture();
        fixture.Wmi[@"root\cimv2:Win32_PnPEntity|Service = 'TPM'"] = [new() { ["Name"] = "TPM 2.0" }];
        fixture.Wmi[@"root\cimv2:SoftwareLicensingProduct"] = [new() { ["Name"] = "Windows" }];
        var source = new FixtureSystemSource(fixture);

        Assert.Single(source.Query(WmiNamespaces.Cimv2, "Win32_PnPEntity", ["Name"], "Service = 'TPM'"));
        Assert.Throws<SystemSourceException>(() => source.Query(WmiNamespaces.Cimv2, "Win32_PnPEntity", ["Name"], "ConfigManagerErrorCode <> 0"));
        // Captured before filters were part of the key.
        Assert.Single(source.Query(WmiNamespaces.Cimv2, "SoftwareLicensingProduct", ["Name"], "ApplicationID = 'x'"));
    }

    [Fact]
    public void FixtureRegistry_SubKeysAreDerivedFromKeyPaths()
    {
        var fixture = new SystemFixture();
        fixture.Registry[@"HKLM\A"] = [];
        fixture.Registry[@"HKLM\A\B"] = new() { ["X"] = 1L };
        fixture.Registry[@"HKLM\A\C\D"] = [];
        var source = new FixtureSystemSource(fixture);
        Assert.Equal(["B", "C"], source.RegistrySubKeys(@"HKLM\A").Order());
        Assert.Equal(1L, source.RegistryValues(@"HKLM\A\B")["X"]);
        Assert.Empty(source.RegistryValues(@"HKLM\Missing"));
    }
}
