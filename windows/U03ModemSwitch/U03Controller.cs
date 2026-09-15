// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace U03ModemSwitch
{
    internal sealed class U03Controller
    {
        private const string VendorId = "19D2";
        private const string ProductStorage = "1484";
        private const string ProductRndis = "1483";
        private const string ProductModem = "1481";
        private const string ProductDiag = "0016";
        private const string DeviceIp = "192.168.100.1";
        private const string HostIp = "192.168.100.3";
        private const string DeviceHost = "speedusb-stick.home";
        private const string Referer = "http://speedusb-stick.home/index.html";
        private const string AuthSalt = "94CB8A5309AF41BDBFC855A9BF2A3A5E";

        private static readonly Regex ProductRegex = new Regex(
            @"VID_19D2&PID_(1484|1483|1481|0016)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ComRegex = new Regex(
            @"\((COM[0-9]+)\)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        internal string GetStatus(Action<string> report)
        {
            U03State state = GetState(false);
            string result = FormatStatus(state);
            ReportLines(report, result);
            return result;
        }

        internal string SwitchToModem(Action<string> report)
        {
            U03State state = GetState(false);
            report("[u03] found 19d2:" + state.ProductId.ToLowerInvariant() + " (" + state.Mode + ")");

            if (state.ProductId == ProductModem)
            {
                report("[u03] already in modem mode; nothing to change");
                return GetStatus(report);
            }

            if (state.ProductId == ProductStorage)
            {
                report("[u03] virtual CD-ROM detected; waiting for the installed ZTE driver to expose RNDIS");
                state = WaitForProduct(new[] { ProductRndis, ProductModem }, 12);
                if (state == null)
                    throw new InvalidOperationException(
                        "U03 is still in 19d2:1484 virtual CD-ROM mode. " +
                        "Run the original ZTE USB software once, or use the Linux tool to reach RNDIS.");
                if (state.ProductId == ProductModem)
                    return GetStatus(report);
            }

            if (state.ProductId == ProductDiag)
                throw new InvalidOperationException(
                    "U03 is in factory/diagnostic mode (19d2:0016). Restore RNDIS first.");
            if (state.ProductId != ProductRndis)
                throw new InvalidOperationException("Unexpected U03 USB state: 19d2:" + state.ProductId);

            RndisAdapter adapter = FindRndisAdapter();
            report("[u03] using RNDIS adapter: " + adapter.Name);
            string sourceAddress = FindDeviceSubnetAddress(adapter.InterfaceIndex);
            bool temporaryAddress = false;

            if (sourceAddress == null)
            {
                report("[u03] temporarily adding " + HostIp + "/24 to the RNDIS adapter");
                RunNetsh(
                    "interface ipv4 add address name=" + adapter.InterfaceIndex.ToString(CultureInfo.InvariantCulture) +
                    " address=" + HostIp + " mask=255.255.255.0 store=active");
                sourceAddress = HostIp;
                temporaryAddress = true;
                Thread.Sleep(400);
            }

            try
            {
                report("[u03] waiting for the Web UI at " + DeviceIp);
                string rd = GetRdToken(sourceAddress);
                string ad = MakeAd(rd);

                report("[u03] enabling the persistent KDDI modem product mode");
                string response = ApiRequest(
                    "POST",
                    "/goform/goform_set_cmd_process",
                    "isTest=false&goformId=SET_PRODUCT_MODE_FOR_KDDI&debug_enable=1&AD=" + ad,
                    sourceAddress,
                    false);
                if (!Regex.IsMatch(response, "\\\"result\\\"\\s*:\\s*\\\"success\\\"", RegexOptions.IgnoreCase))
                    throw new InvalidOperationException("The U03 rejected the mode switch: " + response);

                rd = GetRdToken(sourceAddress);
                ad = MakeAd(rd);
                report("[u03] device accepted the setting; requesting reboot");
                ApiRequest(
                    "POST",
                    "/goform/goform_set_cmd_process",
                    "isTest=false&goformId=REBOOT_DEVICE&AD=" + ad,
                    sourceAddress,
                    true);
            }
            finally
            {
                if (temporaryAddress)
                {
                    try
                    {
                        RunNetsh(
                            "interface ipv4 delete address name=" +
                            adapter.InterfaceIndex.ToString(CultureInfo.InvariantCulture) +
                            " address=" + HostIp);
                    }
                    catch (Exception ex)
                    {
                        report("[u03] warning: could not remove temporary address: " + ex.Message);
                    }
                }
            }

            report("[u03] waiting for modem USB ID 19d2:1481");
            state = WaitForProduct(new[] { ProductModem }, 45);
            if (state == null)
                throw new InvalidOperationException(
                    "The setting was accepted, but 19d2:1481 did not appear. Unplug and reconnect the U03.");

            report("[u03] success: U03 is in persistent modem mode (19d2:1481)");
            string status = FormatStatus(state);
            ReportLines(report, status);
            return status;
        }

        internal string SwitchToRndis(Action<string> report)
        {
            U03State state = GetState(false);
            report("[u03] found 19d2:" + state.ProductId.ToLowerInvariant() + " (" + state.Mode + ")");

            if (state.ProductId == ProductRndis)
            {
                report("[u03] already in RNDIS/Web UI mode; nothing to change");
                return GetStatus(report);
            }
            if (state.ProductId == ProductStorage)
                throw new InvalidOperationException(
                    "U03 is in virtual CD-ROM mode. Run the original ZTE USB software to expose RNDIS.");
            if (state.ProductId != ProductModem && state.ProductId != ProductDiag)
                throw new InvalidOperationException("Unexpected U03 USB state: 19d2:" + state.ProductId);

            string portName = FindAtPort(state.ProductId);
            report("[u03] using AT port: " + portName);
            using (var serial = new SerialPort(portName, 115200, Parity.None, 8, StopBits.One))
            {
                serial.Handshake = Handshake.None;
                serial.DtrEnable = false;
                serial.RtsEnable = false;
                serial.ReadTimeout = 250;
                serial.WriteTimeout = 2000;
                serial.Open();

                report("[u03] disabling the persistent KDDI/CPE modem product mode");
                SendAt(serial, "AT+ZCPE=o", "exit cpe mode result(0:FAIL 1:SUCCESS):1");
                SendAt(serial, "AT+ZCDRUN=8", "Close autorun state result(0:FAIL 1:SUCCESS):1");
                SendAt(serial, "AT+ZCDRUN=F", "exit download mode result(0:FAIL 1:SUCCESS):1");
                report("[u03] device accepted the settings; requesting reboot");
                SendAt(serial, "AT+ZRST", null);
            }

            state = WaitForProduct(new[] { ProductRndis, ProductStorage }, 45);
            if (state == null)
                throw new InvalidOperationException(
                    "The settings were accepted, but neither 19d2:1483 nor 19d2:1484 appeared.");

            if (state.ProductId == ProductStorage)
            {
                report("[u03] virtual CD-ROM appeared; waiting for the installed ZTE driver to expose RNDIS");
                state = WaitForProduct(new[] { ProductRndis }, 12);
            }
            if (state == null || state.ProductId != ProductRndis)
                throw new InvalidOperationException(
                    "The U03 stopped at 19d2:1484. Run the original ZTE USB software, " +
                    "or use the Linux tool once, to reach RNDIS.");

            report("[u03] success: U03 is in persistent RNDIS/Web UI mode (19d2:1483)");
            string status = FormatStatus(state);
            ReportLines(report, status);
            return status;
        }

        private static U03State GetState(bool quiet)
        {
            List<PnpEntity> entities = GetPresentPnpEntities()
                .Where(entity => ProductRegex.IsMatch(entity.InstanceId))
                .ToList();
            string[] products = entities
                .Select(entity => ProductRegex.Match(entity.InstanceId).Groups[1].Value.ToUpperInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (products.Length == 0)
            {
                if (quiet)
                    return null;
                throw new InvalidOperationException("No supported ZTE U03 USB device was found.");
            }
            if (products.Length != 1)
                throw new InvalidOperationException(
                    "Multiple U03 USB states are present: " + string.Join(", ", products));

            return new U03State
            {
                ProductId = products[0],
                Mode = ModeName(products[0]),
                Entities = entities
            };
        }

        private static List<PnpEntity> GetPresentPnpEntities()
        {
            var entities = new List<PnpEntity>();
            using (var searcher = new ManagementObjectSearcher(
                "SELECT PNPDeviceID, Name, ConfigManagerErrorCode FROM Win32_PnPEntity"))
            using (ManagementObjectCollection results = searcher.Get())
            {
                foreach (ManagementObject item in results)
                using (item)
                {
                    object idValue = item["PNPDeviceID"];
                    object codeValue = item["ConfigManagerErrorCode"];
                    if (idValue == null || codeValue == null || Convert.ToUInt32(codeValue) != 0)
                        continue;
                    entities.Add(new PnpEntity
                    {
                        InstanceId = Convert.ToString(idValue, CultureInfo.InvariantCulture) ?? string.Empty,
                        Name = Convert.ToString(item["Name"], CultureInfo.InvariantCulture) ?? string.Empty
                    });
                }
            }
            return entities;
        }

        private static string FormatStatus(U03State state)
        {
            var text = new StringBuilder();
            text.AppendLine("USB ID: 19d2:" + state.ProductId.ToLowerInvariant());
            text.AppendLine("Mode:   " + state.Mode);
            if (state.ProductId == ProductModem || state.ProductId == ProductDiag)
            {
                List<ComPort> ports = GetComPorts(state.ProductId);
                if (ports.Count == 0)
                {
                    text.AppendLine("Ports:  not available (install the ZTE serial/modem driver)");
                }
                else
                {
                    text.AppendLine("Ports:");
                    foreach (ComPort port in ports)
                        text.AppendLine("  " + port.PortName + "  " + port.Name);
                }
            }
            return text.ToString().TrimEnd();
        }

        private static List<ComPort> GetComPorts(string productId)
        {
            var ports = new List<ComPort>();
            foreach (PnpEntity entity in GetPresentPnpEntities())
            {
                if (entity.InstanceId.IndexOf(
                        "VID_" + VendorId + "&PID_" + productId,
                        StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                Match match = ComRegex.Match(entity.Name);
                if (!match.Success)
                    continue;
                ports.Add(new ComPort
                {
                    PortName = match.Groups[1].Value.ToUpperInvariant(),
                    InstanceId = entity.InstanceId,
                    Name = entity.Name
                });
            }
            return ports
                .GroupBy(port => port.PortName, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(port => port.PortName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string FindAtPort(string productId)
        {
            string wantedInterface = productId == ProductDiag ? "MI_02" : "MI_00";
            DateTime deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                List<ComPort> ports = GetComPorts(productId);
                ComPort preferred = ports.FirstOrDefault(port =>
                    port.InstanceId.IndexOf(wantedInterface, StringComparison.OrdinalIgnoreCase) >= 0);
                if (preferred != null)
                    return preferred.PortName;
                if (ports.Count == 1)
                    return ports[0].PortName;
                Thread.Sleep(500);
            }
            throw new InvalidOperationException(
                "Could not find the U03 AT COM port. Install the ZTE serial/modem driver.");
        }

        private static RndisAdapter FindRndisAdapter()
        {
            var adapters = new List<RndisAdapter>();
            using (var searcher = new ManagementObjectSearcher(
                "SELECT PNPDeviceID, InterfaceIndex, NetConnectionID, Name FROM Win32_NetworkAdapter"))
            using (ManagementObjectCollection results = searcher.Get())
            {
                foreach (ManagementObject item in results)
                using (item)
                {
                    string instanceId = Convert.ToString(item["PNPDeviceID"], CultureInfo.InvariantCulture);
                    if (string.IsNullOrEmpty(instanceId) ||
                        instanceId.IndexOf("VID_" + VendorId + "&PID_" + ProductRndis,
                            StringComparison.OrdinalIgnoreCase) < 0 ||
                        item["InterfaceIndex"] == null)
                        continue;
                    int interfaceIndex = Convert.ToInt32(item["InterfaceIndex"], CultureInfo.InvariantCulture);
                    string connectionName = Convert.ToString(item["NetConnectionID"], CultureInfo.InvariantCulture);
                    string deviceName = Convert.ToString(item["Name"], CultureInfo.InvariantCulture);
                    adapters.Add(new RndisAdapter
                    {
                        InterfaceIndex = interfaceIndex,
                        Name = string.IsNullOrEmpty(connectionName) ? deviceName : connectionName
                    });
                }
            }

            adapters = adapters.GroupBy(adapter => adapter.InterfaceIndex).Select(group => group.First()).ToList();
            if (adapters.Count == 0)
                throw new InvalidOperationException("Could not find the U03 RNDIS network adapter.");
            if (adapters.Count > 1)
                throw new InvalidOperationException("Multiple U03 RNDIS network adapters were found.");
            return adapters[0];
        }

        private static string FindDeviceSubnetAddress(int interfaceIndex)
        {
            string query = "SELECT IPAddress FROM Win32_NetworkAdapterConfiguration WHERE InterfaceIndex=" +
                           interfaceIndex.ToString(CultureInfo.InvariantCulture);
            using (var searcher = new ManagementObjectSearcher(query))
            using (ManagementObjectCollection results = searcher.Get())
            {
                foreach (ManagementObject item in results)
                using (item)
                {
                    var addresses = item["IPAddress"] as string[];
                    if (addresses == null)
                        continue;
                    foreach (string address in addresses)
                    {
                        IPAddress parsed;
                        if (IPAddress.TryParse(address, out parsed) &&
                            parsed.AddressFamily == AddressFamily.InterNetwork &&
                            address.StartsWith("192.168.100.", StringComparison.Ordinal) &&
                            address != DeviceIp)
                            return address;
                    }
                }
            }
            return null;
        }

        private static string GetRdToken(string sourceAddress)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(12);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    long stamp = DateTime.UtcNow.Ticks;
                    string response = ApiRequest(
                        "GET",
                        "/goform/goform_get_cmd_process?isTest=false&cmd=RD&multi_data=1&_=" +
                        stamp.ToString(CultureInfo.InvariantCulture),
                        null,
                        sourceAddress,
                        false);
                    Match match = Regex.Match(
                        response,
                        "\\\"RD\\\"\\s*:\\s*\\\"([0-9A-Fa-f]{32})\\\"");
                    if (match.Success)
                        return match.Groups[1].Value;
                }
                catch (WebException)
                {
                    // RNDIS and the Web UI can take a few seconds to become ready.
                }
                Thread.Sleep(500);
            }
            throw new InvalidOperationException("The U03 Web UI did not return a valid RD token.");
        }

        private static string MakeAd(string rd)
        {
            using (MD5 md5 = MD5.Create())
            {
                byte[] hash = md5.ComputeHash(Encoding.ASCII.GetBytes(AuthSalt + rd));
                var text = new StringBuilder(hash.Length * 2);
                foreach (byte value in hash)
                    text.Append(value.ToString("x2", CultureInfo.InvariantCulture));
                return text.ToString();
            }
        }

        private static string ApiRequest(
            string method,
            string path,
            string data,
            string sourceAddress,
            bool allowDisconnect)
        {
            IPAddress bindAddress = IPAddress.Parse(sourceAddress);
            var request = (HttpWebRequest)WebRequest.Create("http://" + DeviceIp + path);
            request.Method = method;
            request.Host = DeviceHost;
            request.Referer = Referer;
            request.UserAgent = "U03ModemSwitch/0.4";
            request.Accept = "application/json, text/javascript, */*; q=0.01";
            request.Headers["X-Requested-With"] = "XMLHttpRequest";
            request.Proxy = null;
            request.KeepAlive = false;
            request.Timeout = 10000;
            request.ReadWriteTimeout = 10000;
            request.ServicePoint.Expect100Continue = false;
            request.ServicePoint.BindIPEndPointDelegate = delegate(
                ServicePoint servicePoint,
                IPEndPoint remoteEndPoint,
                int retryCount)
            {
                return new IPEndPoint(bindAddress, 0);
            };

            if (data != null)
            {
                byte[] body = Encoding.ASCII.GetBytes(data);
                request.ContentType = "application/x-www-form-urlencoded";
                request.ContentLength = body.Length;
                using (Stream stream = request.GetRequestStream())
                    stream.Write(body, 0, body.Length);
            }

            try
            {
                using (var response = (HttpWebResponse)request.GetResponse())
                using (Stream stream = response.GetResponseStream())
                using (var reader = new StreamReader(stream ?? Stream.Null, Encoding.UTF8))
                    return reader.ReadToEnd();
            }
            catch (WebException) when (allowDisconnect)
            {
                return string.Empty;
            }
        }

        private static void RunNetsh(string arguments)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "netsh.exe",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (Process process = Process.Start(startInfo))
            {
                if (process == null)
                    throw new InvalidOperationException("Could not start netsh.exe.");
                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode != 0)
                    throw new InvalidOperationException(
                        "netsh.exe failed (" + process.ExitCode.ToString(CultureInfo.InvariantCulture) + "): " +
                        (string.IsNullOrWhiteSpace(error) ? output : error));
            }
        }

        private static void SendAt(SerialPort serial, string command, string requiredText)
        {
            serial.DiscardInBuffer();
            serial.Write(command + "\r");
            var response = new StringBuilder();
            DateTime deadline = DateTime.UtcNow.AddSeconds(6);
            while (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(100);
                response.Append(serial.ReadExisting());
                string current = response.ToString();
                Match error = Regex.Match(
                    current,
                    @"(?m)^\s*(ERROR|\+CME ERROR:.*|\+CMS ERROR:.*)\s*$");
                if (error.Success)
                    throw new InvalidOperationException(command + " was rejected: " + error.Groups[1].Value);
                if (!Regex.IsMatch(current, @"(?m)^\s*OK\s*$"))
                    continue;
                if (!string.IsNullOrEmpty(requiredText) &&
                    current.IndexOf(requiredText, StringComparison.Ordinal) < 0)
                    throw new InvalidOperationException("Unexpected response to " + command + ": " + current);
                return;
            }
            throw new InvalidOperationException("No OK response to " + command + ": " + response);
        }

        private static U03State WaitForProduct(string[] expected, int timeoutSeconds)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    U03State state = GetState(true);
                    if (state != null && expected.Contains(state.ProductId, StringComparer.OrdinalIgnoreCase))
                        return state;
                }
                catch (InvalidOperationException)
                {
                    // Old and new PnP interfaces can briefly overlap during re-enumeration.
                }
                Thread.Sleep(500);
            }
            return null;
        }

        private static void ReportLines(Action<string> report, string text)
        {
            foreach (string line in text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
                report(line);
        }

        private static string ModeName(string productId)
        {
            switch (productId)
            {
                case ProductStorage: return "virtual CD-ROM";
                case ProductRndis: return "RNDIS/Web UI";
                case ProductModem: return "modem";
                case ProductDiag: return "factory/diagnostic";
                default: return "unknown";
            }
        }

        private sealed class PnpEntity
        {
            internal string InstanceId { get; set; }
            internal string Name { get; set; }
        }

        private sealed class U03State
        {
            internal string ProductId { get; set; }
            internal string Mode { get; set; }
            internal List<PnpEntity> Entities { get; set; }
        }

        private sealed class ComPort
        {
            internal string PortName { get; set; }
            internal string InstanceId { get; set; }
            internal string Name { get; set; }
        }

        private sealed class RndisAdapter
        {
            internal int InterfaceIndex { get; set; }
            internal string Name { get; set; }
        }
    }
}
