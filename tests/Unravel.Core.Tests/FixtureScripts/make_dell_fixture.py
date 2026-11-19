"""Writes tests/Unravel.Core.Tests/Fixtures/dell-hybrid-joined-problems.json (synthetic Phase 2 machine)."""
import json
import os

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "..")


def chars(s, n=None):
    a = [ord(c) for c in s]
    if n:
        a += [0] * (n - len(a))
    return a


U = r"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"
U32 = r"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
UCU = r"HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall"
DC = r"HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}"
CV = r"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion"

REPORT = """<?xml version="1.0" encoding="utf-8"?>
<BatteryReport xmlns="http://schemas.microsoft.com/battery/2012">
  <Batteries>
    <Battery>
      <Id>DELL 7FHHV</Id>
      <Manufacturer>SMP</Manufacturer>
      <DesignCapacity>54000</DesignCapacity>
      <FullChargeCapacity>32400</FullChargeCapacity>
      <CycleCount>410</CycleCount>
    </Battery>
  </Batteries>
</BatteryReport>"""

DSREG = """
+----------------------------------------------------------------------+
| Device State                                                         |
+----------------------------------------------------------------------+

             AzureAdJoined : YES
          EnterpriseJoined : NO
              DomainJoined : YES
                DomainName : CONTOSO
               Device Name : LAPTOP-042.contoso.local

+----------------------------------------------------------------------+
| Tenant Details                                                       |
+----------------------------------------------------------------------+

                TenantName : Contoso Ltd
                  TenantId : 11111111-2222-3333-4444-555555555555
                    MdmUrl : https://enrollment.manage.microsoft.com/enrollmentserver/discovery.svc

+----------------------------------------------------------------------+
| User State                                                           |
+----------------------------------------------------------------------+

                    NgcSet : NO
           WorkplaceJoined : NO

+----------------------------------------------------------------------+
| SSO State                                                            |
+----------------------------------------------------------------------+

                AzureAdPrt : YES
"""

NETSH = """
There is 1 interface on the system:

    Name                   : Wi-Fi
    Description            : Intel(R) Wi-Fi 6 AX201 160MHz
    State                  : connected
    Network type           : Infrastructure
    Radio type             : 802.11ax
    Band                   : 2.4 GHz
    Channel                : 6
    Receive rate (Mbps)    : 86.7
    Transmit rate (Mbps)   : 86.7
    Signal                 : 31%

Network shell commands need location permission to access WLAN information. Turn on location services on the Location page in Privacy & security settings.
"""

APPROVED_OFF = [3] + [0] * 11
APPROVED_ON = [2] + [0] * 11

fx = {
    "description": "Synthetic Phase 2 machine: Dell Latitude, hybrid Entra joined + Intune, with a problem in every Phase 2 area "
                   "(worn battery, Secure Boot off, old BIOS, Public firewall off, two third-party AVs, problem device, stopped service, "
                   "duplicate Zoom, remote access tool, 32-bit Office, SSID hidden by location privacy).",
    "capturedAt": "2026-11-19T12:00:00+00:00",
    "wmi": {
        r"root\cimv2:Win32_ComputerSystem": [{"Manufacturer": "Dell Inc.", "Model": "Latitude 5420", "Name": "LAPTOP-042", "Domain": "contoso.local",
                                              "PartOfDomain": True, "Workgroup": None, "SystemSKUNumber": "0A8C", "TotalPhysicalMemory": 17000000000}],
        r"root\cimv2:Win32_ComputerSystemProduct": [{"Name": "Latitude 5420", "Version": "", "UUID": "4C4C4544-0042-0042-0042-000000000042", "Vendor": "Dell Inc."}],
        r"root\cimv2:Win32_BIOS": [{"SerialNumber": "TESTTAG", "Manufacturer": "Dell Inc.", "SMBIOSBIOSVersion": "1.21.0",
                                    "ReleaseDate": "20210315000000.000000+000", "Version": "DELL   - 1072009"}],
        r"root\cimv2:Win32_SystemEnclosure": [{"SerialNumber": "TESTTAG", "ChassisTypes": [10]}],
        r"root\cimv2:Win32_BaseBoard": [{"Manufacturer": "Dell Inc.", "Product": "0XTEST", "Version": "A00", "SerialNumber": "/TESTTAG/CNTEST00000000/"}],
        r"root\cimv2:Win32_VideoController": [
            {"Name": "Intel(R) Iris(R) Xe Graphics", "AdapterRAM": 134217728, "DriverVersion": "31.0.101.4502", "DriverDate": "20230601000000.000000-000",
             "CurrentHorizontalResolution": 1920, "CurrentVerticalResolution": 1080, "CurrentRefreshRate": 60, "PNPDeviceID": "PCI\\VEN_8086"},
            {"Name": "NVIDIA GeForce MX450", "AdapterRAM": 4293918720, "DriverVersion": "31.0.15.3623", "DriverDate": "20230710000000.000000-000",
             "CurrentHorizontalResolution": None, "CurrentVerticalResolution": None, "CurrentRefreshRate": None, "PNPDeviceID": "PCI\\VEN_10DE"},
            {"Name": "Microsoft Remote Display Adapter", "AdapterRAM": None, "DriverVersion": "10.0.22621.1", "DriverDate": None,
             "CurrentHorizontalResolution": None, "CurrentVerticalResolution": None, "CurrentRefreshRate": None, "PNPDeviceID": "SWD\\REMOTEDISPLAY"}],
        r"root\wmi:WmiMonitorID": [
            {"InstanceName": "DISPLAY\\AUO\\1", "Active": True, "ManufacturerName": chars("AUO", 16), "ProductCodeID": chars("B140HAN", 16),
             "SerialNumberID": chars("0", 16), "UserFriendlyName": [0] * 13, "YearOfManufacture": 2021},
            {"InstanceName": "DISPLAY\\DEL\\2", "Active": True, "ManufacturerName": chars("DEL", 16), "ProductCodeID": chars("D0D1", 16),
             "SerialNumberID": chars("TESTMON1", 16), "UserFriendlyName": chars("DELL P2422H", 13), "YearOfManufacture": 2022},
            {"InstanceName": "DISPLAY\\OLD\\3", "Active": False, "ManufacturerName": chars("SAM", 16), "ProductCodeID": chars("X", 16),
             "SerialNumberID": chars("X", 16), "UserFriendlyName": chars("OLD", 13), "YearOfManufacture": 2015}],
        r"root\wmi:WmiMonitorBasicDisplayParams": [
            {"InstanceName": "DISPLAY\\AUO\\1", "MaxHorizontalImageSize": 31, "MaxVerticalImageSize": 17},
            {"InstanceName": "DISPLAY\\DEL\\2", "MaxHorizontalImageSize": 53, "MaxVerticalImageSize": 30}],
        r"root\wmi:WmiMonitorConnectionParams": [
            {"InstanceName": "DISPLAY\\AUO\\1", "VideoOutputTechnology": 2147483648},
            {"InstanceName": "DISPLAY\\DEL\\2", "VideoOutputTechnology": 5}],
        r"root\cimv2:Win32_PnPEntity|Service = 'TPM'": [{"Name": "Trusted Platform Module 2.0", "Status": "OK"}],
        r"root\StandardCimv2:MSFT_NetAdapter": [
            {"Name": "Wi-Fi", "InterfaceDescription": "Intel(R) Wi-Fi 6 AX201 160MHz", "InterfaceIndex": 12, "PermanentAddress": "A0B1C2D3E4F5",
             "Speed": 86700000, "MediaConnectState": 1, "NdisPhysicalMedium": 9, "Virtual": False, "HardwareInterface": True},
            {"Name": "Ethernet", "InterfaceDescription": "Intel(R) Ethernet Connection (13) I219-LM", "InterfaceIndex": 7, "PermanentAddress": "A0B1C2D3E4F6",
             "Speed": 1000000000, "MediaConnectState": 2, "NdisPhysicalMedium": 14, "Virtual": False, "HardwareInterface": True},
            {"Name": "vEthernet (Default Switch)", "InterfaceDescription": "Hyper-V Virtual Ethernet Adapter", "InterfaceIndex": 30,
             "PermanentAddress": "00155D000001", "Speed": 10000000000, "MediaConnectState": 1, "NdisPhysicalMedium": 14, "Virtual": True,
             "HardwareInterface": False}],
        r"root\cimv2:Win32_NetworkAdapterConfiguration|IPEnabled = TRUE": [
            {"InterfaceIndex": 12, "IPAddress": ["10.0.0.42", "fe80::1"], "DefaultIPGateway": ["10.0.0.1"], "DNSServerSearchOrder": ["10.0.0.10", "10.0.0.11"],
             "DHCPEnabled": True, "MACAddress": "A0:B1:C2:D3:E4:F5", "Description": "Intel(R) Wi-Fi 6 AX201 160MHz"}],
        r"root\cimv2:Win32_Battery": [{"Name": "DELL 7FHHV", "DeviceID": "1", "EstimatedChargeRemaining": 64, "DesignCapacity": None, "FullChargeCapacity": None}],
        r"root\wmi:BatteryFullChargedCapacity": [{"InstanceName": "ACPI\\PNP0C0A\\1_0", "FullChargedCapacity": 32400}],
        r"root\wmi:BatteryCycleCount": [{"InstanceName": "ACPI\\PNP0C0A\\1_0", "CycleCount": 0}],
        # productState: 0x60100 Defender off; 0x41000 and 0x61000 on and up to date.
        r"root\SecurityCenter2:AntiVirusProduct": [
            {"displayName": "Windows Defender", "productState": 393472},
            {"displayName": "Sophos Intercept X", "productState": 266240},
            {"displayName": "Norton 360", "productState": 397312}],
        r"root\Microsoft\Windows\Defender:MSFT_MpComputerStatus": [
            {"AntivirusEnabled": True, "RealTimeProtectionEnabled": False, "IsTamperProtected": True, "AMRunningMode": "Passive Mode",
             "AntivirusSignatureLastUpdated": "20261118080000.000000+000", "AntivirusSignatureAge": 1, "AntivirusSignatureVersion": "1.421.0.0"}],
        r"root\cimv2:Win32_Service": [
            {"Name": "Dnscache", "DisplayName": "DNS Client", "State": "Running", "StartMode": "Auto",
             "PathName": "C:\\Windows\\system32\\svchost.exe -k NetworkService -p", "StartName": "NT AUTHORITY\\NetworkService", "DelayedAutoStart": False},
            {"Name": "LSM", "DisplayName": "Local Session Manager", "State": "Running", "StartMode": "Auto", "PathName": None,
             "StartName": "LocalSystem", "DelayedAutoStart": False},
            {"Name": "SophosAgent", "DisplayName": "Sophos Agent", "State": "Running", "StartMode": "Auto",
             "PathName": "\"C:\\Program Files\\Sophos\\Agent.exe\"", "StartName": "LocalSystem", "DelayedAutoStart": False},
            {"Name": "BackupSvc", "DisplayName": "Contoso Backup", "State": "Stopped", "StartMode": "Auto",
             "PathName": "\"C:\\Program Files\\Contoso\\backup.exe\" -service", "StartName": "LocalSystem", "DelayedAutoStart": False},
            {"Name": "edgeupdate", "DisplayName": "Microsoft Edge Update Service (edgeupdate)", "State": "Stopped", "StartMode": "Auto",
             "PathName": "\"C:\\Program Files (x86)\\Microsoft\\EdgeUpdate\\MicrosoftEdgeUpdate.exe\" /svc", "StartName": "LocalSystem",
             "DelayedAutoStart": True}],
        r"root\cimv2:Win32_PnPEntity|ConfigManagerErrorCode <> 0": [
            {"Name": None, "PNPClass": None, "ConfigManagerErrorCode": 28, "DeviceID": "PCI\\VEN_10EC&DEV_5260"},
            {"Name": "Bluetooth Device (Personal Area Network)", "PNPClass": "Net", "ConfigManagerErrorCode": 22, "DeviceID": "BTH\\1"},
            {"Name": "Old USB Stick", "PNPClass": "DiskDrive", "ConfigManagerErrorCode": 45, "DeviceID": "USBSTOR\\1"}],
        r"root\cimv2:Win32_PnPSignedDriver": [
            {"DeviceName": "Realtek Audio", "DriverProviderName": "Realtek", "DriverVersion": "6.0.9000.1",
             "DriverDate": "20190101000000.******+***", "DeviceClass": "MEDIA"},
            {"DeviceName": "Generic USB Hub", "DriverProviderName": "Microsoft", "DriverVersion": "10.0.1",
             "DriverDate": "20060621000000.******+***", "DeviceClass": "USB"},
            {"DeviceName": "Some Intel Thing", "DriverProviderName": "Intel", "DriverVersion": "1.0",
             "DriverDate": "20060621000000.******+***", "DeviceClass": "SYSTEM"},
            {"DeviceName": "Intel Wi-Fi", "DriverProviderName": "Intel", "DriverVersion": "22.200.0.6",
             "DriverDate": "20230301000000.******+***", "DeviceClass": "NET"}],
    },
    "wmiErrors": {r"root\wmi:BatteryStaticData": "access denied"},
    "registry": {
        CV: {"ProductName": "Windows 10 Enterprise", "EditionID": "Enterprise", "DisplayVersion": "24H2", "UBR": 2314},
        r"HKLM\SYSTEM\CurrentControlSet\Control\SecureBoot\State": {"UEFISecureBootEnabled": 0},
        DC: {},
        DC + r"\0000": {"DriverDesc": "Intel(R) Iris(R) Xe Graphics", "HardwareInformation.qwMemorySize": 134217728},
        DC + r"\0001": {"DriverDesc": "NVIDIA GeForce MX450", "HardwareInformation.qwMemorySize": 2147483648},
        DC + r"\Properties": {},
        U: {},
        U + r"\{CHROME}": {"DisplayName": "Google Chrome", "DisplayVersion": "131.0.6778.86", "Publisher": "Google LLC", "InstallDate": "20261101",
                           "EstimatedSize": 512000},
        U + r"\{ZOOM6}": {"DisplayName": "Zoom Workplace (64-bit)", "DisplayVersion": "6.1.0", "Publisher": "Zoom Video Communications, Inc.",
                          "InstallDate": "20260601"},
        U + r"\{KB1}": {"DisplayName": "Security Update for Microsoft Office (KB5002000)", "ParentKeyName": "Office16", "DisplayVersion": "1"},
        U + r"\{SYS}": {"DisplayName": "Intel Hidden Component", "SystemComponent": 1, "DisplayVersion": "1.0"},
        U + r"\{NONAME}": {"DisplayVersion": "1.0"},
        U32: {},
        U32 + r"\TeamViewer": {"DisplayName": "TeamViewer", "DisplayVersion": "15.60.3", "Publisher": "TeamViewer", "InstallDate": "7/4/2025",
                               "UninstallString": "C:\\Program Files (x86)\\TeamViewer\\uninstall.exe"},
        UCU: {},
        UCU + r"\ZoomUMX": {"DisplayName": "Zoom Workplace", "DisplayVersion": "5.17.11", "Publisher": "Zoom Video Communications, Inc."},
        r"HKLM\SOFTWARE\Microsoft\Office\ClickToRun\Configuration": {
            "ProductReleaseIds": "O365ProPlusRetail,VisioProRetail", "VersionToReport": "16.0.17928.20156", "Platform": "x86",
            "CDNBaseUrl": "http://officecdn.microsoft.com/pr/7ffbc6bf-bc32-4f92-8982-f9dd17fd3114"},
        r"HKCU\Software\Microsoft\Office\16.0\Common\Identity\Identities": {},
        r"HKCU\Software\Microsoft\Office\16.0\Common\Identity\Identities\abc_ADAL": {"EmailAddress": "alex@contoso.com"},
        r"HKLM\SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\DomainProfile": {"EnableFirewall": 1},
        r"HKLM\SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\StandardProfile": {"EnableFirewall": 1},
        r"HKLM\SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\PublicProfile": {"EnableFirewall": 0},
        r"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System": {"EnableLUA": 1, "ConsentPromptBehaviorAdmin": 5, "PromptOnSecureDesktop": 1},
        r"HKLM\SOFTWARE\Microsoft\Enrollments": {},
        r"HKLM\SOFTWARE\Microsoft\Enrollments\AAAA": {"ProviderID": "Local Authority", "EnrollmentState": 1, "EnrollmentType": 28},
        r"HKLM\SOFTWARE\Microsoft\Enrollments\BBBB": {"ProviderID": "MS DM Server", "EnrollmentState": 1, "EnrollmentType": 6, "UPN": "alex@contoso.com"},
        r"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run": {"SecurityHealth": "%windir%\\system32\\SecurityHealthSystray.exe"},
        r"HKCU\Software\Microsoft\Windows\CurrentVersion\Run": {
            "Spotify": "\"C:\\Users\\User\\AppData\\Roaming\\Spotify\\Spotify.exe\" --autostart",
            "Teams": "\"C:\\Program Files\\Teams\\ms-teams.exe\" --system-initiated"},
        r"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run": {"Spotify": APPROVED_OFF, "Teams": APPROVED_ON},
        r"HKLM\SYSTEM\CurrentControlSet\Services\LSM": {"ImagePath": "%SystemRoot%\\system32\\svchost.exe -k DcomLaunch -p"},
        r"HKLM\SYSTEM\CurrentControlSet\Services\edgeupdate\TriggerInfo": {},
    },
    "commands": {"dsregcmd /status": DSREG, "netsh wlan show interfaces": NETSH},
    "rows": {
        "StoreApps": [
            {"Name": "Microsoft.VCLibs.140.00", "DisplayName": "Microsoft Visual C++ Runtime", "Publisher": "Microsoft Corporation",
             "Version": "14.0.33519.0", "Architecture": "X64", "IsFramework": True, "IsResource": False, "SignatureKind": "Store",
             "InstalledDate": "2026-01-01T00:00:00Z"},
            {"Name": "Microsoft.WindowsCalculator", "DisplayName": "Windows Calculator", "Publisher": "Microsoft Corporation", "Version": "11.2409.0.0",
             "Architecture": "X64", "IsFramework": False, "IsResource": False, "SignatureKind": "System", "InstalledDate": "2026-01-01T00:00:00Z"},
            {"Name": "MicrosoftCorporationII.QuickAssist", "DisplayName": "Quick Assist", "Publisher": "Microsoft Corp.", "Version": "2.0.29.0",
             "Architecture": "X64", "IsFramework": False, "IsResource": False, "SignatureKind": "Store", "InstalledDate": "2026-02-03T10:00:00Z"},
            {"Name": "SpotifyAB.SpotifyMusic", "DisplayName": "ms-resource:AppName", "Publisher": "Spotify AB", "Version": "1.250.0.0",
             "Architecture": "X86", "IsFramework": False, "IsResource": False, "SignatureKind": "Store", "InstalledDate": "2026-03-04T10:00:00Z"},
            {"Name": "Microsoft.LanguageExperiencePacken-GB", "DisplayName": "English (UK)", "Publisher": "Microsoft", "Version": "1.0",
             "Architecture": "Neutral", "IsFramework": False, "IsResource": True, "SignatureKind": "Store", "InstalledDate": None}],
        "StartupTasks": [{"Name": "Contoso Updater", "Path": "\\Contoso Updater", "Enabled": False, "Trigger": "At logon",
                          "Command": "C:\\Program Files\\Contoso\\update.exe /silent", "Author": "Contoso"}],
        "LocalGroupMembers|S-1-5-32-544": [
            {"Name": "LAPTOP-042\\Administrator", "Kind": "User", "Sid": "S-1-5-21-1-500"},
            {"Name": "CONTOSO\\Domain Admins", "Kind": "Group", "Sid": "S-1-5-21-2-512"}],
        "BatteryReport": [{"Xml": REPORT}],
        "FileVersion|C:\\Program Files\\Sophos\\Agent.exe": [{"CompanyName": "Sophos Limited", "ProductName": "Sophos", "FileVersion": "1.0",
                                                               "FileDescription": "Agent"}],
        "FileVersion|C:\\Program Files\\Contoso\\backup.exe": [{"CompanyName": "Contoso Ltd", "ProductName": "Backup", "FileVersion": "1.0",
                                                                "FileDescription": "Backup"}],
        "FileVersion|C:\\Program Files\\Teams\\ms-teams.exe": [{"CompanyName": "Microsoft Corporation", "ProductName": "Teams",
                                                                "FileVersion": "1.0", "FileDescription": "Teams"}],
        "FileVersion|C:\\Program Files (x86)\\Microsoft\\EdgeUpdate\\MicrosoftEdgeUpdate.exe": [
            {"CompanyName": "Microsoft Corporation", "ProductName": "Edge Update", "FileVersion": "1.0", "FileDescription": "Edge Update"}],
    },
    "rowErrors": {},
}

path = os.path.join(ROOT, "tests", "Unravel.Core.Tests", "Fixtures", "dell-hybrid-joined-problems.json")
with open(path, "w", encoding="utf-8") as fh:
    json.dump(fx, fh, indent=2)
print("wrote", path)
