// A phone's USB tethering is for the picture only, never for the PC's internet.
//
// With USB tethering on, Windows sends all of the PC's internet through the phone: the phone's
// "Remote NDIS" network gets a metric as good as Wi-Fi or a 1 Gb/s LAN (25), and Windows
// Connection Manager prefers a wired network (tethering looks like Ethernet) whatever the
// metrics. And when Wi-Fi and LAN go away, the phone is the only way out, so a cable left plugged
// in quietly spends the phone's mobile data. CastLink asks once per phone network and then, with
// one Windows prompt:
//   - gives that network metric 9000 and sets the policy "Minimize the number of simultaneous
//     connections" to 0, so Wi-Fi or LAN carry the internet;
//   - adds Windows Firewall rules (group "CastLink USB", only on that network) that block
//     public addresses and DNS, so nothing reaches the internet through the phone even then.
// The phone itself (private address on the cable: picture, touches, WHO) stays reachable.
// Undo steps: docs/ipad.md.

using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Text;

namespace TVCast
{
    static class UsbRoute
    {
        public const int HighMetric = 9000;
        public const string RuleGroup = "CastLink USB";
        public const string PolicyKey = @"HKLM\SOFTWARE\Policies\Microsoft\Windows\WcmSvc\GroupPolicy";

        /// <summary>every IPv4 address that is not private, link-local, CGNAT or multicast</summary>
        internal static readonly string[] Public4 = {
            "0.0.0.0-9.255.255.255", "11.0.0.0-100.63.255.255", "100.128.0.0-169.253.255.255",
            "169.255.0.0-172.15.255.255", "172.32.0.0-192.167.255.255", "192.169.0.0-223.255.255.255" };

        /// <summary>a phone network (USB tethering) that is up but not yet limited to the picture:
        /// its IPv4 interface index and its name in Windows, else null</summary>
        public static KeyValuePair<int, string>? NotLimited()
        {
            HashSet<string> done = RuledInterfaces();
            if (done == null) return null; // the firewall could not be read: do not nag
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || !WebCast.IsUsbTethering(ni.Description)) continue;
                    IPv4InterfaceProperties v4;
                    try { v4 = ni.GetIPProperties().GetIPv4Properties(); } catch (NetworkInformationException) { continue; }
                    if (v4 == null) continue;
                    KeyValuePair<int, string>? d = Decide(v4.Index, ni.Name, done);
                    if (d != null) return d;
                }
            }
            catch (NetworkInformationException) { }
            return null;
        }

        internal static KeyValuePair<int, string>? Decide(int ifIndex, string name, ICollection<string> ruled)
        {
            if (ifIndex <= 0 || string.IsNullOrEmpty(name)) return null;
            foreach (string r in ruled)
                if (string.Equals(r, name, StringComparison.OrdinalIgnoreCase)) return null; // Windows ignores case
            return new KeyValuePair<int, string>(ifIndex, name);
        }

        /// <summary>the networks our firewall rules are on (read without admin rights through the
        /// firewall's COM object), null if it cannot be read</summary>
        static HashSet<string> RuledInterfaces()
        {
            HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                Type t = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
                if (t == null) return null;
                object policy = Activator.CreateInstance(t);
                object rules = t.InvokeMember("Rules", BindingFlags.GetProperty, null, policy, null);
                foreach (object r in (IEnumerable)rules)
                {
                    Type rt = r.GetType();
                    object g = rt.InvokeMember("Grouping", BindingFlags.GetProperty, null, r, null);
                    if (!RuleGroup.Equals(g as string)) continue;
                    object ifs = rt.InvokeMember("Interfaces", BindingFlags.GetProperty, null, r, null);
                    IEnumerable list = ifs as IEnumerable;
                    if (list != null) foreach (object n in list) if (n != null) set.Add(n.ToString());
                }
                return set;
            }
            catch (Exception) { return null; }
        }

        /// <summary>The script run with the Windows prompt. Only the interface number comes in;
        /// the network's name is looked up by the script itself.</summary>
        internal static string Script(int ifIndex)
        {
            StringBuilder s = new StringBuilder();
            s.Append("$ErrorActionPreference = 'Stop'\n");
            s.Append("$i = ").Append(ifIndex).Append('\n');
            s.Append("$a = (Get-NetAdapter -InterfaceIndex $i).Name\n");
            s.Append("Set-NetIPInterface -InterfaceIndex $i -InterfaceMetric ").Append(HighMetric).Append('\n');
            s.Append("reg.exe add '").Append(PolicyKey).Append("' /v fMinimizeConnections /t REG_DWORD /d 0 /f | Out-Null\n");
            s.Append("$g = '").Append(RuleGroup).Append("'\n");
            s.Append("Get-NetFirewallRule -Group $g -ErrorAction SilentlyContinue | Where-Object { ($_ | Get-NetFirewallInterfaceFilter).InterfaceAlias -contains $a } | Remove-NetFirewallRule\n");
            s.Append("New-NetFirewallRule -Group $g -DisplayName \"CastLink: no internet through the phone ($a, IPv4)\" -Direction Outbound -Action Block -InterfaceAlias $a -RemoteAddress ")
             .Append("'").Append(string.Join("','", Public4)).Append("' | Out-Null\n");
            s.Append("New-NetFirewallRule -Group $g -DisplayName \"CastLink: no internet through the phone ($a, IPv6)\" -Direction Outbound -Action Block -InterfaceAlias $a -RemoteAddress '2000::/3' | Out-Null\n");
            s.Append("New-NetFirewallRule -Group $g -DisplayName \"CastLink: no DNS through the phone ($a, UDP)\" -Direction Outbound -Action Block -InterfaceAlias $a -Protocol UDP -RemotePort 53 | Out-Null\n");
            s.Append("New-NetFirewallRule -Group $g -DisplayName \"CastLink: no DNS through the phone ($a, TCP)\" -Direction Outbound -Action Block -InterfaceAlias $a -Protocol TCP -RemotePort 53 | Out-Null\n");
            return s.ToString();
        }

        internal static string Arguments(int ifIndex)
        {
            return "-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand "
                + Convert.ToBase64String(Encoding.Unicode.GetBytes(Script(ifIndex)));
        }

        /// <summary>asks Windows for the change; false if the user said no to the prompt or it failed</summary>
        public static bool LimitToPicture(int ifIndex, out string error)
        {
            error = null;
            try
            {
                ProcessStartInfo si = new ProcessStartInfo(
                    Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"),
                    Arguments(ifIndex));
                si.Verb = "runas";
                si.UseShellExecute = true;
                si.WindowStyle = ProcessWindowStyle.Hidden;
                using (Process p = Process.Start(si))
                {
                    if (!p.WaitForExit(30000)) { error = "timeout"; return false; }
                    if (p.ExitCode != 0) { error = "exit " + p.ExitCode; return false; }
                }
                return true;
            }
            catch (Win32Exception e)
            {
                if (e.NativeErrorCode != 1223) error = e.Message; // 1223: "No" on the prompt
                return false;
            }
        }
    }
}
