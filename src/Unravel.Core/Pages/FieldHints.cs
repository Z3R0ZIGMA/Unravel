using Unravel.Core.Diagnostics;

namespace Unravel.Core.Pages;

/// <summary>
/// What a status icon means, by field label: shown when hovering the icon next to a value. Each hint says what the
/// value measures and what is good. Fields not listed here get a general explanation of the icon's colour.
/// </summary>
public static class FieldHints
{
    private static readonly Dictionary<string, string> Hints = new(StringComparer.OrdinalIgnoreCase)
    {
        // Diagnostics: stability
        ["Reliability index"] =
            "Windows' Reliability Monitor score, from 1 to 10. Crashes, failed updates and hardware errors lower it; it recovers over the following days when nothing goes wrong. 7 or more is good, 4 to 7 means some recent problems, below 4 means many. Open Reliability Monitor to see what lowered it, day by day.",
        ["Blue screens"] = "Windows crashed and restarted (a bug check). One can be a one-off; repeated blue screens usually point to a driver, memory or disk problem.",
        ["Unexpected shutdowns"] = "Windows stopped without shutting down properly: power loss, holding the power button, a hang or a crash. Repeated ones point to power, overheating or hardware.",
        ["Disk errors"] = "Errors the disk driver logged. \"I/O retried\" bursts are often harmless after sleep; bad blocks and paging errors mean the disk struggles to read data.",
        ["Hardware errors (WHEA)"] = "Errors the processor, memory or chipset reported to Windows. Even corrected ones can be an early sign of failing memory, an unstable CPU or overheating.",
        ["Last memory test"] = "The last result of Windows Memory Diagnostic. Any errors mean a memory module should be replaced.",

        // Diagnostics: updates and support
        ["Windows support"] = "Whether Microsoft still releases security updates for this Windows version and edition. Out of support means no more security fixes.",
        ["Support"] = "Whether Microsoft still releases security updates for this Windows version and edition. Out of support means no more security fixes.",
        ["Last monthly update"] = "The last cumulative (security) update Windows installed. They come every month; more than 45 days without one means updates are stuck or paused.",
        ["Pending restart"] = "Windows has installed updates that only finish after a restart. A restart pending for more than a week leaves the PC half-updated.",
        ["Update service"] = "The Windows Update service. Manual is normal (it starts when needed); Disabled stops updates entirely.",

        // Hardware and firmware
        ["Virtualisation"] = "Intel VT-x / AMD-V in the firmware. Only needed for Hyper-V, WSL, Windows Sandbox, Docker or virtual machines; off is fine otherwise.",
        ["BIOS date"] = "When the installed firmware was released. Firmware older than about 3 years may miss security and stability fixes: check the manufacturer's support page.",
        ["Boot mode"] = "UEFI is the modern boot mode and is needed for Secure Boot and Windows 11. Legacy BIOS mode is outdated.",
        ["Secure Boot"] = "Stops unsigned code from loading before Windows starts. Needed for Windows 11 support and recommended for security.",
        ["TPM"] = "The security chip that BitLocker, Windows Hello and Windows 11 rely on. TPM 2.0 is required for Windows 11.",
        ["Signal"] = "Wi-Fi signal strength. 60% or more is good; below 35% expect slow or dropping connections.",
        ["Health"] = "How much charge the battery holds compared with when it was new. 80% or more is good; below 80% the battery life is noticeably shorter; below 50% replace it.",

        // Windows
        ["Uptime"] = "Time since the last restart. More than two weeks means updates may be waiting and memory leaks build up. With Fast Startup, shutting down does not reset this.",
        ["Activation"] = "Whether Windows is activated (genuine and licensed). Unactivated Windows shows a watermark and limits personalisation.",

        // Security
        ["Microsoft Defender"] = "Microsoft Defender switches itself off when another antivirus is installed and active. That is normal: only one real-time scanner should run.",
        ["Defender real-time protection"] = "Scans files as they are opened. It should be on unless another antivirus protects the PC.",
        ["Definitions updated"] = "When the antivirus definitions were last updated. More than a week old means new threats are not recognised.",
        ["Tamper protection"] = "Stops malware and scripts from turning Microsoft Defender off.",
        ["Domain profile"] = "The firewall on networks where the PC can reach its company domain. It should be on.",
        ["Private profile"] = "The firewall on networks marked as private (home, trusted office). It should be on.",
        ["Public profile"] = "The firewall on public networks (cafés, hotels, guest Wi-Fi). It should always be on.",
        ["User Account Control"] = "Asks before apps make changes that need administrator rights. Off means any program can change the system silently.",
        ["Entra sign-in token (PRT)"] = "The Primary Refresh Token gives single sign-on to Microsoft 365 on an Entra-joined PC. Missing means users get extra sign-in prompts.",

        // Software
        ["Bitness"] = "64-bit Microsoft 365 Apps is the default and recommended. 32-bit is only needed for old 32-bit add-ins.",
    };

    public static string? For(string label) => Hints.GetValueOrDefault(label);

    /// <summary>The hint for a field's status icon: the field's own hint, the table's, or a general explanation.</summary>
    public static string ForIcon(string label, Severity severity) => For(label) ?? PageField.GeneralMeaning(severity);
}
