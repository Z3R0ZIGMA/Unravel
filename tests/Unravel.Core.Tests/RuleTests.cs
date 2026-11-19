using Unravel.Core.Collectors;
using Unravel.Core.Infrastructure;
using Unravel.Core.Models;

namespace Unravel.Core.Tests;

public class OemValuesTests
{
    [Theory]
    [InlineData("To be filled by O.E.M.")]
    [InlineData("TO BE FILLED BY O.E.M.")]
    [InlineData("Default string")]
    [InlineData("System Serial Number")]
    [InlineData("  ")]
    [InlineData("0000000000")]
    [InlineData("FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("........")]
    public void Placeholders_AreNotSet(string value) => Assert.Null(OemValues.Clean(value));

    [Theory]
    [InlineData("  PF3ABC12  ", "PF3ABC12")]
    [InlineData("5CG1234XYZ\0", "5CG1234XYZ")]
    [InlineData("1234-5678-9012-3456-7890-1234-56", "1234-5678-9012-3456-7890-1234-56")]
    public void RealValues_AreTrimmedAndKept(string value, string expected) => Assert.Equal(expected, OemValues.Clean(value));
}

public class DeviceRulesTests
{
    [Theory]
    [InlineData("Dell Inc.", "Dell")]
    [InlineData("HP", "HP")]
    [InlineData("Hewlett-Packard", "HP")]
    [InlineData("LENOVO", "Lenovo")]
    [InlineData("ASUSTeK COMPUTER INC.", "ASUS")]
    [InlineData("Micro-Star International Co., Ltd.", "MSI")]
    [InlineData("Microsoft Corporation", "Microsoft")]
    [InlineData("Framework Computer Inc.", "Framework Computer")]
    public void NormalizeMake(string manufacturer, string expected) => Assert.Equal(expected, DeviceRules.NormalizeMake(manufacturer));

    [Fact]
    public void Lenovo_FriendlyModelComesFromProductVersion()
    {
        var device = new DeviceInfo
        {
            Make = "Lenovo",
            Model = "21AHCTO1WW",
            FriendlyModel = DeviceRules.FriendlyModel("Lenovo", "21AHCTO1WW", "ThinkPad T14 Gen 3"),
        };
        Assert.Equal("Lenovo ThinkPad T14 Gen 3", device.DisplayName);
        Assert.Null(DeviceRules.FriendlyModel("Lenovo", "21AH", "Lenovo"));
        Assert.Null(DeviceRules.FriendlyModel("Dell", "Latitude 5440", "1.0"));
    }

    [Fact]
    public void DisplayName_DoesNotRepeatTheMake()
    {
        Assert.Equal("HP EliteBook 840 G9", new DeviceInfo { Make = "HP", Model = "HP EliteBook 840 G9" }.DisplayName);
        Assert.Equal("Dell Latitude 5440", new DeviceInfo { Make = "Dell", Model = "Latitude 5440" }.DisplayName);
    }

    [Theory]
    [InlineData("VMware, Inc.", "VMware7,1", null, "VMware")]
    [InlineData("innotek GmbH", "VirtualBox", null, "VirtualBox")]
    [InlineData("QEMU", "Standard PC (Q35 + ICH9, 2009)", null, "QEMU/KVM")]
    [InlineData("Microsoft Corporation", "Virtual Machine", "Hyper-V UEFI Release v4.1", "Hyper-V")]
    [InlineData("Microsoft Corporation", "Surface Laptop 5", "124I:00036T:000M:0300000D:11000000", null)]
    [InlineData("Dell Inc.", "OptiPlex 7010", null, null)]
    public void DetectVm(string manufacturer, string model, string? version, string? expected) =>
        Assert.Equal(expected, DeviceRules.DetectVm(manufacturer, model, version));

    [Theory]
    [InlineData(3, ChassisKind.Desktop)]
    [InlineData(10, ChassisKind.Laptop)]
    [InlineData(9, ChassisKind.Laptop)]
    [InlineData(13, ChassisKind.AllInOne)]
    [InlineData(35, ChassisKind.MiniPc)]
    [InlineData(31, ChassisKind.Convertible)]
    [InlineData(2, ChassisKind.Unknown)]
    public void Chassis(int code, ChassisKind expected) => Assert.Equal(expected, DeviceRules.Chassis(code));
}

public class CpuRulesTests
{
    [Theory]
    [InlineData("Intel(R) Core(TM) i7-8650U CPU @ 1.90GHz", "Intel Core i7-8650U")]
    [InlineData("13th Gen Intel(R) Core(TM) i5-1335U", "13th Gen Intel Core i5-1335U")]
    [InlineData("AMD Ryzen 7 5800X 8-Core Processor            ", "AMD Ryzen 7 5800X 8-Core")]
    public void CleanName(string raw, string expected) => Assert.Equal(expected, CpuRules.CleanName(raw));

    [Theory]
    [InlineData("Intel Core i7-8650U", "8th gen Intel Core", 8)]
    [InlineData("Intel Core i5-10400", "10th gen Intel Core", 10)]
    [InlineData("Intel Core i5-1035G1", "10th gen Intel Core", 10)]
    [InlineData("Intel Core i7-1185G7", "11th gen Intel Core", 11)]
    [InlineData("12th Gen Intel Core i5-1235U", "12th gen Intel Core", 12)]
    [InlineData("Intel Core i5-1335U", "13th gen Intel Core", 13)]
    [InlineData("Intel Core i7-14700K", "14th gen Intel Core", 14)]
    [InlineData("Intel Core i5-2500K", "2nd gen Intel Core", 2)]
    [InlineData("Intel Core i5-520M", "1st gen Intel Core", 1)]
    [InlineData("Intel Core Ultra 7 155H", "Intel Core Ultra (Series 1)", 1)]
    [InlineData("Intel Core Ultra 7 258V", "Intel Core Ultra (Series 2)", 2)]
    [InlineData("AMD Ryzen 5 5600X 6-Core", "Ryzen 5000 series", 5000)]
    [InlineData("AMD Ryzen 7 PRO 7840U w/ Radeon 780M Graphics", "Ryzen 7000 series", 7000)]
    [InlineData("AMD Ryzen AI 9 HX 370 w/ Radeon 890M", "Ryzen AI 300 series", 300)]
    [InlineData("Snapdragon(R) X Elite - X1E78100 - Qualcomm(R) Oryon(TM) CPU", "Snapdragon X series", null)]
    [InlineData("Intel Pentium Silver N5030", null, null)]
    public void Generation(string name, string? text, int? number)
    {
        var (t, n) = CpuRules.Generation(name);
        Assert.Equal(text, t);
        Assert.Equal(number, n);
    }
}

public class MemoryRulesTests
{
    [Theory]
    [InlineData(24, "DDR3")]
    [InlineData(26, "DDR4")]
    [InlineData(34, "DDR5")]
    [InlineData(29, "LPDDR3")]
    [InlineData(30, "LPDDR4")]
    [InlineData(35, "LPDDR5")]
    public void SmbiosType(int code, string expected)
    {
        var (gen, inferred) = MemoryRules.Generation(code, 0, null, null);
        Assert.Equal(expected, gen);
        Assert.False(inferred);
    }

    [Theory]
    [InlineData("M471A1K43DB1-CWE", null, "DDR4")]
    [InlineData("M425R2GA3EB0-CWMOL", null, "DDR5")]
    [InlineData("KF556C40-16", null, "DDR5")]
    [InlineData("CT8G4SFRA32A.C8FE", 3200, "DDR4")]
    [InlineData("UNKNOWNPART", 5600, "DDR5")]
    public void Inferred(string part, int? speed, string expected)
    {
        var (gen, inferred) = MemoryRules.Generation(0, 0, part, speed);
        Assert.Equal(expected, gen);
        Assert.True(inferred);
    }

    [Fact]
    public void Unknown_WhenNothingToGoOn() => Assert.Equal((null, false), MemoryRules.Generation(0, 0, null, null));

    [Theory]
    [InlineData(8, "DDR4", "DIMM")]
    [InlineData(12, "DDR5", "SODIMM")]
    [InlineData(12, "LPDDR5", "Soldered")]
    [InlineData(0, "DDR4", null)]
    public void FormFactor(int code, string generation, string? expected) => Assert.Equal(expected, MemoryRules.FormFactor(code, generation));

    [Theory]
    [InlineData("80CE", "Samsung")]
    [InlineData("80AD000080AD", "SK hynix")]
    [InlineData("Micron Technology", "Micron Technology")]
    public void Vendor(string raw, string expected) => Assert.Equal(expected, MemoryRules.Vendor(raw));
}

public class OsRulesTests
{
    [Theory]
    [InlineData("Microsoft Windows 11 Pro", 26100, null, "Windows 11 Pro")]
    [InlineData(null, 22631, "Windows 10 Pro", "Windows 11 Pro")] // registry ProductName is wrong on Windows 11
    [InlineData(null, 19045, "Windows 10 Home", "Windows 10 Home")]
    public void Name(string? caption, int build, string? productName, string expected) =>
        Assert.Equal(expected, OsRules.Name(caption, build, productName));

    [Theory]
    [InlineData("Windows(R) Operating System, OEM_DM channel", "OEM")]
    [InlineData("Windows(R) Operating System, RETAIL channel", "Retail")]
    [InlineData("Windows(R) Operating System, VOLUME_KMSCLIENT channel", "Volume (KMS)")]
    [InlineData("Windows(R) Operating System, VOLUME_MAK channel", "Volume (MAK)")]
    public void LicenseChannel(string description, string expected) => Assert.Equal(expected, OsRules.LicenseChannel(description));

    [Theory]
    [InlineData("ARM64", null, "ARM64")]
    [InlineData(null, "64-bit", "x64")]
    [InlineData(null, "ARM 64-bit Processor", "ARM64")]
    public void Architecture(string? env, string? os, string expected) => Assert.Equal(expected, OsRules.Architecture(env, os));

    [Fact]
    public void CultureName() => Assert.Equal("en-GB", OsRules.CultureName("0809"));
}

public class StorageRulesTests
{
    [Fact]
    public void Codes()
    {
        Assert.Equal("NVMe", StorageRules.BusType(17));
        Assert.Equal("SATA", StorageRules.BusType(11));
        Assert.Equal("USB", StorageRules.BusType(7));
        Assert.Equal("HDD", StorageRules.MediaType(3));
        Assert.Equal("SSD", StorageRules.MediaType(4));
        Assert.Null(StorageRules.MediaType(0));
        Assert.Equal(DiskHealth.Unhealthy, StorageRules.Health(2));
        Assert.Equal("GPT", StorageRules.PartitionStyle(2));
    }
}
