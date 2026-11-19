using Unravel.Core.Infrastructure;
using Unravel.Core.Models;

namespace Unravel.Core.Collectors;

/// <summary>Entra / AD / Intune join state (dsregcmd /status) and local administrators (plan.md section 4.6).</summary>
public sealed class IdentityCollector : CollectorBase<IdentityInfo>
{
    public override string Section => Sections.Identity;

    /// <summary>BUILTIN\Administrators: the name is localised, the SID is not.</summary>
    public const string AdministratorsSid = "S-1-5-32-544";

    protected override IdentityInfo Collect(CollectContext ctx, CollectorNotes notes, CancellationToken ct)
    {
        var output = ctx.Source.RunCommand("dsregcmd", "/status");
        var status = output is null ? null : IdentityRules.ParseDsregcmd(output);
        ct.ThrowIfCancellationRequested();

        bool Yes(string key) => status?.TryGetValue(key, out var v) == true && v.Equals("YES", StringComparison.OrdinalIgnoreCase);
        string? Value(string key) => status?.TryGetValue(key, out var v) == true ? OemValues.Clean(v) : null;

        bool entra = Yes("AzureAdJoined"), domain = Yes("DomainJoined"), workplace = Yes("WorkplaceJoined");
        string? domainName = Value("DomainName");
        if (status is null)
        {
            notes.Warn("Couldn't run dsregcmd, so Entra join state is unknown");
            var cs = TryQuery(ctx, notes, "domain", WmiNamespaces.Cimv2, "Win32_ComputerSystem", ["PartOfDomain", "Domain"]).FirstOrDefault();
            domain = cs?.Bool("PartOfDomain") == true;
            domainName = domain ? cs?.RawString("Domain") : null;
        }

        var (mdm, mdmUser) = ReadEnrollment(ctx, Value("MdmUrl"));
        var admins = TryRows(ctx, notes, "local administrators", RowKinds.LocalGroupMembers, AdministratorsSid);

        return new IdentityInfo
        {
            JoinType = IdentityRules.JoinType(entra, domain),
            EntraJoined = entra,
            DomainJoined = domain,
            WorkplaceJoined = workplace,
            DomainName = domainName,
            TenantName = Value("TenantName"),
            TenantId = Value("TenantId"),
            DeviceId = Value("DeviceId"),
            Mdm = mdm,
            MdmUser = mdmUser,
            HasPrt = status is null ? null : Yes("AzureAdPrt"),
            LocalAdmins = admins.Count == 0 && notes.Warnings.Any(w => w.Contains("local administrators", StringComparison.Ordinal))
                ? null
                : admins.Select(a => new LocalAdmin(a.RawString("Name") ?? a.RawString("Sid") ?? "?", a.RawString("Kind") ?? "Other")).ToList(),
            FromDsregcmd = status is not null,
        };
    }

    /// <summary>
    /// MDM enrolment from HKLM\SOFTWARE\Microsoft\Enrollments (works for every join type), falling back to the
    /// MdmUrl dsregcmd shows for Entra-joined devices.
    /// </summary>
    private static (string? Mdm, string? User) ReadEnrollment(CollectContext ctx, string? mdmUrl)
    {
        const string enrollments = @"HKLM\SOFTWARE\Microsoft\Enrollments";
        foreach (var id in ctx.Source.RegistrySubKeys(enrollments))
        {
            var key = $@"{enrollments}\{id}";
            var provider = RegString(ctx, key, "ProviderID");
            // EnrollmentState 1 = enrolled. "MS DM Server" is Intune; other MDMs use EnrollmentType 6 (MDM) or 13
            // (Entra join + MDM). Windows' own entries ("Local Authority", "Deploy Authority"...) are not MDM.
            var type = RegNumber(ctx, key, "EnrollmentType");
            if (provider is null || RegNumber(ctx, key, "EnrollmentState") != 1 || (provider != "MS DM Server" && type is not (6 or 13)))
            {
                continue;
            }

            var name = provider == "MS DM Server" ? "Intune" : provider;
            return (name, RegString(ctx, key, "UPN"));
        }

        if (mdmUrl is not null)
        {
            return (mdmUrl.Contains("manage.microsoft.com", StringComparison.OrdinalIgnoreCase) ? "Intune" : mdmUrl, null);
        }

        return (null, null);
    }
}

public static class IdentityRules
{
    /// <summary>"   AzureAdJoined : YES" lines to a dictionary. The keys are the same on every display language.</summary>
    public static Dictionary<string, string> ParseDsregcmd(string output)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            var colon = line.IndexOf(" : ", StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }

            var key = line[..colon].Trim();
            // The first value wins: later sections ("Diagnostic Data") repeat some keys.
            values.TryAdd(key, line[(colon + 3)..].Trim());
        }

        return values;
    }

    public static string JoinType(bool entra, bool domain) => (entra, domain) switch
    {
        (true, true) => "Hybrid Entra joined",
        (true, false) => "Entra joined",
        (false, true) => "Domain joined",
        _ => "Workgroup",
    };
}
