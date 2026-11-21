using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace MessageBridge.Services;

/// <summary>
/// 本机网络状态判断。
///
/// 「蓝牙兜底」只在电脑插着**那台**有线以太网时才启用 —— 家里这台路由器的
/// LAN 口 MAC 是固定的，靠网关 MAC 认比认网段稳（路由器换网段也认得出来）。
/// 电脑自己走 WiFi 时两台机器在同一张网里，不做额外动作。
/// </summary>
public static class NetworkInfo
{
    /// <summary>虚拟网卡的关键字，这些虽然也报成 Ethernet，但不是真的插网线。</summary>
    private static readonly string[] VirtualHints =
    {
        "virtual", "hyper-v", "vethernet", "vmware", "virtualbox",
        "tap", "tun", "vpn", "loopback", "bluetooth", "wsl", "docker",
    };

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int SendARP(int destIp, int srcIp, byte[] macAddr, ref int phyAddrLen);

    /// <summary>当前是否有一块真的插着网线的以太网卡在工作。</summary>
    public static bool IsOnEthernet()
    {
        try
        {
            foreach (var ni in EthernetInterfaces()) return true;
        }
        catch
        {
            // 查询失败时按「不是以太网」处理，宁可不启用也不乱发指令
        }
        return false;
    }

    /// <summary>
    /// 当前这块以太网的网关 MAC 是否等于 <paramref name="targetMac"/>。
    /// 只有匹配上才允许自动 WiFi 兜底。
    /// </summary>
    public static bool IsTargetEthernet(string targetMac)
    {
        if (string.IsNullOrWhiteSpace(targetMac)) return false;
        var now = GetGatewayMac();
        if (now == null) return false;
        var want = targetMac.Replace(':', '-').ToUpperInvariant();
        return string.Equals(now, want, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>读当前以太网的网关 MAC（形如 <c>88-C3-97-44-40-95</c>），读不到返回 null。</summary>
    public static string? GetGatewayMac()
    {
        try
        {
            foreach (var ni in EthernetInterfaces())
            {
                var gateway = ni.GetIPProperties().GatewayAddresses
                    .FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork)?.Address;
                if (gateway == null) continue;

                var mac = SendArp(gateway);
                if (mac != null) return mac;
            }
        }
        catch
        {
            // 忽略
        }
        return null;
    }

    /// <summary>给界面用的一句话描述，例如「以太网 192.168.66.244」。</summary>
    public static string DescribeEthernet()
    {
        try
        {
            foreach (var ni in EthernetInterfaces())
            {
                var ip = ni.GetIPProperties().UnicastAddresses
                    .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address;
                if (ip == null) continue;
                return $"{ni.Name} {ip}";
            }
        }
        catch
        {
            // 忽略
        }
        return string.Empty;
    }

    /// <summary>枚举「真的插着网线的物理以太网卡」。</summary>
    private static IEnumerable<NetworkInterface> EthernetInterfaces()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType != NetworkInterfaceType.Ethernet) continue;

            var text = (ni.Name + " " + ni.Description).ToLowerInvariant();
            if (VirtualHints.Any(h => text.Contains(h))) continue;

            var hasIpv4 = ni.GetIPProperties().UnicastAddresses
                .Any(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
            if (!hasIpv4) continue;

            yield return ni;
        }
    }

    private static string? SendArp(IPAddress ip)
    {
        try
        {
            // in_addr 就是 4 字节网络序，这里按内存原样塞进 int 交给 API
            var dest = BitConverter.ToInt32(ip.GetAddressBytes(), 0);
            var mac = new byte[6];
            var len = mac.Length;
            if (SendARP(dest, 0, mac, ref len) != 0 || len < 6) return null;
            return string.Join("-", mac.Select(b => b.ToString("X2")));
        }
        catch
        {
            return null;
        }
    }
}
